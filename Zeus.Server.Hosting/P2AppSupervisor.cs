// P2AppSupervisor — Zeus runs the radio's own p2app so the operator never
// has to. On the internal display radio (Pi IS the radio, discovered via
// the loopback leg) the row on the Discover tab only exists while p2app
// lives; before this service that meant a terminal and a manual start
// after every boot. Now Zeus owns the lifecycle:
//
//   SPAWN    when Linux + a local Saturn on the PCIe bus + a p2app binary
//            is found + nothing already owns UDP 1024
//   ADOPT    when something already listens on 1024 (the operator's own
//            systemd unit or manual start) — never fight it, just report
//   RESTART  with doubling backoff (3s→30s) when the child dies; the
//            backoff resets after a minute of health
//   PAUSE    around native XDMA sessions — the coexistence rule (p2app
//            and the native register plane must never run together)
//            enforced by code instead of operator discipline
//
// The child is stopped SIGTERM-first with a five-second grace before
// SIGKILL — the same discipline the G2 Suite Updater learned — so p2app's
// signal handler can close the xdma devices cleanly.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Zeus.Server;

public sealed class P2AppSupervisor : BackgroundService
{
    public enum Mode { Probing, Disabled, NoBinary, Supervised, Adopted, Backoff, Paused }

    private readonly SaturnXdmaProbe _probe;
    private readonly IConfiguration _cfg;
    private readonly ILogger<P2AppSupervisor> _log;
    private readonly object _lock = new();

    private Process? _child;
    private volatile bool _paused;
    private Mode _mode = Mode.Probing;
    private string? _binaryPath;
    private int _restarts;
    private int? _lastExitCode;
    private long _nextSpawnAtMs;
    private long _childBornMs;
    private int _backoffMs = 3000;
    private bool _noBinaryLogged;
    private int _catBounces;

    public P2AppSupervisor(SaturnXdmaProbe probe, IConfiguration cfg, ILogger<P2AppSupervisor> log)
    { _probe = probe; _cfg = cfg; _log = log; }

    public object Status()
    {
        lock (_lock)
            return new
            {
                mode = _mode.ToString(),
                pid = _child is { HasExited: false } c ? c.Id : (int?)null,
                binaryPath = _binaryPath,
                restarts = _restarts,
                lastExitCode = _lastExitCode,
                catBounces = _catBounces,
            };
    }

    /// <summary>
    /// True while a p2app owns — or is about to own — this host's radio, and
    /// with it the front-panel tty. Probing counts: it is exactly the startup
    /// window in which a spawned p2app performs its own ZZZS exchange with
    /// the panel, and a second reader on the line can steal the reply and
    /// blind p2app's panel detection for its whole life. Backoff counts too —
    /// the respawn is imminent. Only Disabled (not a radio host), NoBinary
    /// (nothing to spawn) and Paused (native session; p2app stopped) leave
    /// the tty free.
    /// </summary>
    public bool TtyBelongsToP2App
    {
        get { lock (_lock) return _mode is Mode.Probing or Mode.Supervised or Mode.Adopted or Mode.Backoff; }
    }

    /// <summary>
    /// True while a freshly started (or freshly adopted) p2app may still be
    /// running its startup panel detection — the ZZZS exchange on the tty
    /// that must never be raced by a second reader. The panel bridge holds
    /// its serial fallback until this window has passed.
    /// </summary>
    // Hands-off window after each p2app (re)start so its own panel detection
    // never races a second tty reader (the original two-reader disease).
    // Detection runs in p2app's first couple of seconds; 5 s covers it with
    // margin. Field consequence: on a stock p2app (which eats ZZZS/ZZZP and
    // forwards nothing) the panel always arrives via the serial fallback at
    // exactly grace-expiry — so this constant IS the panel's time-to-live
    // after Zeus starts. It was 15 s; the extra 10 s protected nothing.
    private const int TtyDetectionGraceMs = 5_000;

    public bool TtyDetectionGraceActive =>
        Environment.TickCount64 - Interlocked.Read(ref _ownershipStampMs) < TtyDetectionGraceMs;

    private long _ownershipStampMs = Environment.TickCount64;

    /// <summary>
    /// Called before a native XDMA session opens the register plane. Stops
    /// the supervised child and verifies nothing else owns port 1024. The
    /// session must not start unless this returns ok — the check replaces
    /// the old honor-system confirm string with a measured fact.
    /// </summary>
    public async Task<(bool Ok, string? Error)> PauseForNativeSessionAsync()
    {
        _paused = true;                       // loop stops respawning first
        await StopChildAsync("native session opening");
        if (PortInUse())
        {
            // Not ours (ours is dead) — an externally-managed p2app. We
            // never kill a process we didn't start: the operator (or their
            // systemd unit, which would only respawn it) must stop it.
            _paused = false;
            return (false,
                "p2app is running outside Zeus's management (UDP 1024 is owned by another process) — " +
                "stop it (systemctl stop p2app, or kill it) and retry");
        }
        return (true, null);
    }

    /// <summary>
    /// The process is about to die via Environment.Exit (the Exit button or
    /// INSTALL &amp; RESTART) — BackgroundService.StopAsync never runs on that
    /// path, so this synchronous kill is the only thing standing between the
    /// operator and an orphaned p2app that the NEXT Zeus can only ADOPT (and
    /// then can't pause for native sessions). SIGTERM, short grace, SIGKILL.
    /// </summary>
    public void KillChildOnProcessExit()
    {
        _paused = true;   // the loop must not respawn into a dying process
        Process? c;
        lock (_lock) { c = _child; _child = null; }
        if (c is null || c.HasExited) { c?.Dispose(); return; }
        try
        {
            _log.LogInformation("p2app stopping pid={Pid} (Zeus exiting)", c.Id);
            using var term = Process.Start(new ProcessStartInfo
            { FileName = "kill", Arguments = $"-TERM {c.Id}", UseShellExecute = false });
            term?.WaitForExit(1000);
            if (!c.WaitForExit(2000))
            {
                c.Kill(entireProcessTree: true);
                c.WaitForExit(1000);
            }
        }
        catch { /* the process is exiting — best effort */ }
        finally { c.Dispose(); }
    }

    /// <summary>Where p2app lives on this host, or null. Config wins; then the
    /// usual Saturn build trees; then the Zeus-managed clone.</summary>
    public string? ResolveBinaryPath() => FindBinary();

    /// <summary>Native session closed (cleanly or by failure) — respawn soon.</summary>
    public void Resume()
    {
        _paused = false;
        lock (_lock) { _nextSpawnAtMs = 0; if (_mode == Mode.Paused) _mode = Mode.Probing; }
    }

    /// <summary>
    /// The front-panel bridge saw a live P2 session but no CAT callback ever
    /// arrived. Upstream p2app's CAT thread is born once per process life and
    /// dies with the session it served — a p2app that predates the current
    /// session will never dial the callback port again. Restarting the child
    /// WHILE the session is up hands it a fresh CAT thread that latches the
    /// stable port and connects within seconds (the field cure was exactly
    /// `pkill p2app` at that moment). Owned children only: an Adopted p2app
    /// belongs to the operator and is never touched — then this returns false
    /// and the caller advises a manual restart instead. Never called during
    /// MOX/TUNE (the caller checks) and inert while paused for a native
    /// session.
    /// </summary>
    public async Task<bool> BounceOwnedChildAsync(string why)
    {
        lock (_lock)
        {
            if (_paused) return false;
            if (_child is not { HasExited: false }) return false;   // Adopted / NoBinary / dead — nothing of ours to bounce
        }
        _log.LogInformation("p2app bounce — {Why}", why);
        await StopChildAsync(why);
        lock (_lock)
        {
            _catBounces++;
            _nextSpawnAtMs = 0;        // respawn on the very next tick
            _mode = Mode.Probing;
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || _probe.Probe() is null)
        {
            lock (_lock) _mode = Mode.Disabled;
            _log.LogInformation("p2app.supervisor disabled — {Why}",
                OperatingSystem.IsLinux() ? "no Saturn on the local PCIe bus" : "not the radio host");
            return;
        }
        _log.LogInformation("p2app.supervisor armed — local Saturn present, Zeus manages p2app");

        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { _log.LogWarning(ex, "p2app.supervisor tick fault"); }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
        await StopChildAsync("Zeus shutting down");
    }

    private void Tick()
    {
        if (_paused)
        {
            lock (_lock) _mode = Mode.Paused;
            return;
        }

        lock (_lock)
        {
            if (_child is { } c)
            {
                if (!c.HasExited)
                {
                    // Healthy for a minute? Forgive past crashes.
                    if (_backoffMs > 3000 && Environment.TickCount64 - _childBornMs > 60_000)
                        _backoffMs = 3000;
                    _mode = Mode.Supervised;
                    return;
                }
                _lastExitCode = SafeExitCode(c);
                _log.LogWarning("p2app exited code={Code} — respawn in {Ms}ms", _lastExitCode, _backoffMs);
                c.Dispose(); _child = null;
                _nextSpawnAtMs = Environment.TickCount64 + _backoffMs;
                _backoffMs = Math.Min(_backoffMs * 2, 30_000);
                _mode = Mode.Backoff;
                return;
            }
        }

        if (PortInUse())
        {
            lock (_lock)
            {
                if (_mode != Mode.Adopted) Interlocked.Exchange(ref _ownershipStampMs, Environment.TickCount64);
                _mode = Mode.Adopted;
            }
            return;
        }

        var bin = FindBinary();
        if (bin is null)
        {
            lock (_lock) _mode = Mode.NoBinary;
            if (!_noBinaryLogged)
            {
                _noBinaryLogged = true;
                _log.LogWarning("p2app binary not found — set P2App:Path in appsettings or ZEUS_P2APP_PATH; " +
                                "searched the usual Saturn build locations. Discovery still works if you run p2app yourself.");
            }
            return;
        }

        if (Environment.TickCount64 < Interlocked.Read(ref _nextSpawnAtMs)) return;
        Spawn(bin);
    }

    private void Spawn(string bin)
    {
        var psi = new ProcessStartInfo
        {
            FileName = bin,
            Arguments = _cfg["P2App:Args"] ?? "",
            WorkingDirectory = Path.GetDirectoryName(bin) ?? "/",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            var p = Process.Start(psi);
            if (p is null) throw new InvalidOperationException("Process.Start returned null");
            p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } s) _log.LogDebug("p2app: {Line}", s); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } s) _log.LogInformation("p2app! {Line}", s); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            lock (_lock)
            {
                _child = p; _binaryPath = bin; _restarts++;
                _childBornMs = Environment.TickCount64;
                Interlocked.Exchange(ref _ownershipStampMs, Environment.TickCount64);
                _mode = Mode.Supervised;
            }
            _log.LogInformation("p2app spawned pid={Pid} bin={Bin}", p.Id, bin);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "p2app spawn failed bin={Bin} — retry in {Ms}ms", bin, _backoffMs);
            lock (_lock)
            {
                _nextSpawnAtMs = Environment.TickCount64 + _backoffMs;
                _backoffMs = Math.Min(_backoffMs * 2, 30_000);
                _mode = Mode.Backoff;
            }
        }
    }

    private async Task StopChildAsync(string why)
    {
        Process? c;
        lock (_lock) { c = _child; _child = null; }
        if (c is null || c.HasExited) { c?.Dispose(); return; }
        _log.LogInformation("p2app stopping pid={Pid} ({Why}) — SIGTERM, 5s grace", c.Id, why);
        try
        {
            // SIGTERM first so p2app's handler closes the xdma devices;
            // .NET's Process.Kill is SIGKILL on Unix, so shell out for TERM.
            using var term = Process.Start(new ProcessStartInfo
            { FileName = "kill", Arguments = $"-TERM {c.Id}", UseShellExecute = false });
            if (term is not null) await term.WaitForExitAsync();
            using var cts = new CancellationTokenSource(5000);
            try { await c.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                _log.LogWarning("p2app ignored SIGTERM for 5s — SIGKILL");
                c.Kill(entireProcessTree: true);
                await c.WaitForExitAsync();
            }
            _lastExitCode = SafeExitCode(c);
        }
        catch (Exception ex) { _log.LogWarning(ex, "p2app stop fault (continuing)"); }
        finally { c.Dispose(); }
    }

    private string? FindBinary()
    {
        var configured = Environment.GetEnvironmentVariable("ZEUS_P2APP_PATH") ?? _cfg["P2App:Path"];
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in new[]
        {
            Path.Combine(home, "Saturn/sw_projects/P2_app/p2app"),
            Path.Combine(home, "github/Saturn/sw_projects/P2_app/p2app"),
            Path.Combine(home, ".zeus/Saturn/sw_projects/P2_app/p2app"),
            "/home/pi/Saturn/sw_projects/P2_app/p2app",
            "/usr/local/bin/p2app",
        })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static bool PortInUse()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Any, 1024));
            return false;   // we could bind — nobody owns it
        }
        catch (SocketException) { return true; }
    }

    private static int? SafeExitCode(Process p)
    { try { return p.ExitCode; } catch { return null; } }
}
