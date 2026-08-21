// P2AppUpdateService — the Updates tab learns to update p2app itself, from
// Laurence Barker's Saturn repository, using exactly the steps his own
// update_saturn_code.sh performs: git pull, make clean, make in
// sw_projects/P2_app. If no Saturn tree exists on this host, a shallow
// managed clone is created at ~/.zeus/Saturn (which the supervisor's binary
// search already knows about), so a factory-fresh radio can go from nothing
// to a running p2app in one button press.
//
// The supervisor is paused for the duration (same interlock as a native
// session — the build replaces the binary the child is running) and resumed
// afterwards, success or failure. An externally-managed p2app refuses the
// update for the same reason it refuses native sessions: Zeus does not stop
// processes it doesn't own.
using System.Diagnostics;
using System.Text;

namespace Zeus.Server;

public sealed class P2AppUpdateService
{
    private const string RepoUrl = "https://github.com/laurencebarker/Saturn";

    private readonly P2AppSupervisor _sup;
    private readonly ILogger<P2AppUpdateService> _log;
    private readonly object _lock = new();
    private readonly List<string> _logLines = new();

    private string _phase = "idle";     // idle|pausing|cloning|pulling|building|done|failed
    private string? _error;
    private string? _repoDir;
    private string? _headline;          // repo HEAD after a successful update
    private bool _rolledBack;
    private Task? _running;

    public P2AppUpdateService(P2AppSupervisor sup, ILogger<P2AppUpdateService> log)
    { _sup = sup; _log = log; }

    public object Status()
    {
        lock (_lock)
            return new
            {
                phase = _phase,
                error = _error,
                repoDir = _repoDir,
                head = _headline,
                running = _running is { IsCompleted: false },
                rolledBack = _rolledBack,
                log = _logLines.TakeLast(12).ToArray(),
            };
    }

    public (bool Ok, string? Error) Start()
    {
        if (!OperatingSystem.IsLinux())
            return (false, "p2app updates run on the radio's own Linux host only");
        lock (_lock)
        {
            if (_running is { IsCompleted: false })
                return (false, "an update is already running");
            _phase = "pausing"; _error = null; _headline = null; _rolledBack = false; _logLines.Clear();
            _running = Task.Run(RunAsync);
        }
        return (true, null);
    }

    private async Task RunAsync()
    {
        try
        {
            // Same measured interlock as a native session: stop OUR child,
            // refuse if someone else owns the port.
            var (ok, error) = await _sup.PauseForNativeSessionAsync();
            if (!ok) { Fail(error ?? "p2app could not be paused"); return; }

            var repo = ResolveRepoDir();
            lock (_lock) _repoDir = repo;
            var p2appDir = Path.Combine(repo, "sw_projects", "P2_app");

            if (!Directory.Exists(Path.Combine(repo, ".git")))
            {
                SetPhase("cloning");
                Append($"cloning {RepoUrl} -> {repo}");
                Directory.CreateDirectory(Path.GetDirectoryName(repo)!);
                if (!await RunStepAsync("git",
                        $"clone --depth 1 {RepoUrl} \"{repo}\"", null, 300)) return;
            }
            else
            {
                SetPhase("pulling");
                Append($"git pull --ff-only in {repo}");
                if (!await RunStepAsync("git", "pull --ff-only", repo, 180)) return;
            }

            SetPhase("building");
            var bin = Path.Combine(p2appDir, "p2app");
            var backup = bin + ".zeus-previous";
            UnixFileMode? previousMode = null;
            if (File.Exists(bin))
            {
                // 'make clean' is 'rm -rf $(TARGET) *.o' in the Saturn
                // Makefile — it deletes the running binary before the new
                // one exists. Preserve it, or a failed build (broken
                // upstream commit, missing dep) leaves the radio with NO
                // p2app until some build succeeds.
                // Guard for the CA1416 platform analyzer: the whole run is
                // Linux-gated in Start(), but the analyzer can't see across
                // methods — Zeus also compiles for Windows.
                if (OperatingSystem.IsLinux())
                    previousMode = File.GetUnixFileMode(bin);
                File.Copy(bin, backup, overwrite: true);
                Append($"previous binary preserved as {Path.GetFileName(backup)}");
            }

            // Laurence's update script verbatim: clean, then build.
            var built =
                await RunStepAsync("make", "clean", p2appDir, 120)
                && await RunStepAsync("make", "", p2appDir, 900);
            if (built && !File.Exists(bin))
            {
                Fail("build finished but no p2app binary was produced");
                built = false;
            }
            if (!built)
            {
                RestoreBackup(bin, backup, previousMode);
                return;
            }

            // Record what we're now running.
            var head = await CaptureAsync("git", "log -1 --format=%h %cd --date=short", repo);
            lock (_lock) _headline = head?.Trim();
            SetPhase("done");
            Append($"p2app updated — {head?.Trim() ?? "HEAD unknown"}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "p2app.update fault");
            Fail(ex.Message);
        }
        finally
        {
            // Success or failure, the radio gets its p2app back. On failure
            // the old binary usually survives (make replaces on link only);
            // if not, the supervisor's backoff loop reports it honestly.
            _sup.Resume();
        }
    }

    private string ResolveRepoDir()
    {
        // Prefer the tree the supervisor's binary actually lives in — the
        // operator's own checkout, updated in place like Laurence's script.
        var bin = _sup.ResolveBinaryPath();
        if (bin is not null)
        {
            var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(bin)!, "..", ".."));
            if (Directory.Exists(Path.Combine(candidate, ".git"))) return candidate;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".zeus", "Saturn");
    }

    private async Task<bool> RunStepAsync(string file, string args, string? cwd, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            WorkingDirectory = cwd ?? "/",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p is null) { Fail($"{file} failed to start"); return false; }
        // Keep the tool's last words: the settings card shows only the Fail
        // line, and 'git pull --ff-only exited 1' without git's actual
        // complaint sent an operator to the terminal to learn what git had
        // already told us (field report). The full stream still lands in the
        // update log via Append.
        var tail = new List<string>(4);
        void Remember(string l)
        {
            Append(l);
            lock (tail)
            {
                tail.Add(l);
                if (tail.Count > 3) tail.RemoveAt(0);
            }
        }
        p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } l) Remember(l); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } l) Remember(l); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            Fail($"{file} {args} timed out after {timeoutSeconds}s");
            return false;
        }
        if (p.ExitCode != 0)
        {
            string why;
            lock (tail) { why = tail.Count > 0 ? $" — {string.Join(" | ", tail)}" : ""; }
            Fail($"{file} {args} exited {p.ExitCode}{why}");
            return false;
        }
        return true;
    }

    private static async Task<string?> CaptureAsync(string file, string args, string cwd)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file, Arguments = args, WorkingDirectory = cwd,
                UseShellExecute = false, RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var sb = new StringBuilder();
            sb.Append(await p.StandardOutput.ReadToEndAsync());
            await p.WaitForExitAsync();
            return sb.ToString();
        }
        catch { return null; }
    }

    private void RestoreBackup(string bin, string backup, UnixFileMode? mode)
    {
        if (!File.Exists(backup))
        {
            Append("no previous binary to restore (first install) — p2app stays absent until a build succeeds");
            return;
        }
        try
        {
            File.Copy(backup, bin, overwrite: true);
            if (OperatingSystem.IsLinux() && mode is { } m) File.SetUnixFileMode(bin, m);
            lock (_lock) _rolledBack = true;
            Append("ROLLED BACK — previous p2app binary restored; the radio keeps working on the old version");
        }
        catch (Exception ex)
        {
            Append($"rollback failed: {ex.Message} — p2app may be absent until a build succeeds");
        }
    }

    private void SetPhase(string phase) { lock (_lock) _phase = phase; }
    private void Fail(string error)
    {
        lock (_lock) { _phase = "failed"; _error = error; }
        Append($"FAILED: {error}");
    }
    private void Append(string line)
    {
        lock (_lock)
        {
            _logLines.Add(line);
            if (_logLines.Count > 300) _logLines.RemoveRange(0, _logLines.Count - 300);
        }
        _log.LogDebug("p2app.update: {Line}", line);
    }
}
