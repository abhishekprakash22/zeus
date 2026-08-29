// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Serial bring-up mirrors DeskHPSDR's launch_serial_rigctl
// (https://github.com/dl1bz/deskhpsdr, Heiko DL1BZ, GPL-2.0-or-later): open
// the g2-front line, let the Arduino bootloader settle, then drive the
// ANDROMEDA ZZZS/ZZZI handshake. See ATTRIBUTIONS.md for provenance.

using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Zeus.Contracts;

namespace Zeus.Server.FrontPanel;

/// <summary>
/// Background bridge between an ANAN G2 / G2-Ultra hardware front panel
/// (a serial ANDROMEDA controller) and Zeus's radio services. It opens the
/// panel's serial line, decodes button / encoder / VFO events into radio
/// actions via <see cref="G2PanelActionRouter"/>, and pushes LED state back
/// with <c>ZZZI</c> reports.
///
/// <para>Device resolution is presence-gated: with no <c>DevicePath</c>
/// configured it auto-detects the <c>g2-front-*</c> by-id symlink, and on a
/// host with no panel it simply idles and re-probes. So it is safe to leave
/// registered everywhere; it only does work on a machine the panel is wired
/// to (typically the G2's internal Pi).</para>
/// </summary>
public sealed class G2FrontPanelService : BackgroundService
{
    private readonly G2PanelOptions _opts;
    private readonly G2PanelSettingsStore _store;
    private readonly G2PanelMappingStore _mappingStore;

    // Press-to-identify: the last raw button/encoder event, recorded BEFORE
    // the type-5 routing gate (telemetry only — nothing is routed by it), so
    // the settings grid can flash the matching row and surface ids outside
    // the default table (e.g. the panel's PS button). Immutable record swapped
    // atomically; volatile read on the endpoint thread.
    private sealed record PanelInputStamp(string Kind, int Id, long TickMs);
    private volatile PanelInputStamp? _lastInput;
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly G2PanelActionRouter _router;
    private readonly ILogger<G2FrontPanelService> _log;

    private readonly AndromedaParser _parser = new();
    private readonly object _writeLock = new();
    private SerialPort? _port;

    // ---- CAT-over-TCP link (the p2app topology) -------------------------
    // When the radio is used through its own p2app (the display radio's
    // loopback row), p2app is the sole reader of the panel's serial line —
    // opening it here too would put two readers on one tty and they'd steal
    // each other's messages. Instead p2app FORWARDS the panel's traffic:
    // Zeus announces a callback port in the P2 high-priority packet (bytes
    // 1398-1399), p2app connects to it and relays ZZZS/ZZZE/ZZZP/ZZZU/ZZZD
    // verbatim. Same parser, same router, same LED writes (ZZZI goes back
    // over the socket and p2app relays it to the panel). While this link is
    // live the serial path stands down; when it drops, serial probing
    // resumes — so the checkbox governs the panel regardless of transport.
    private TcpListener? _catListener;
    private volatile NetworkStream? _catStream;
    private volatile bool _catActive;
    private int _catPort;

    // ---- CAT bounce watchdog (the once-per-session cure) ----------------
    // The stable port (above) is necessary but not sufficient: upstream
    // p2app's CAT thread runs while(SDRActive) and then DIES for that
    // p2app's whole life — one CAT session per p2app process. A p2app that
    // predates the current P2 session (fresh Zeus start over a supervised
    // child, or any reconnect within one p2app life) will never dial the
    // callback port again; the field cure was `pkill p2app` with the
    // session up. So: when the panel is enabled, a P2 session has been up
    // for a grace period, and NEITHER transport is delivering, ask the
    // supervisor to restart its own p2app once — the fresh process dials
    // the stable port within seconds. One attempt per stall: the latch
    // re-arms only when a panel link actually comes up, so a bounce that
    // doesn't cure can never loop. Never during MOX/TUNE, and never for a
    // p2app Zeus doesn't own (logged advice instead).
    private const int CatGraceMs = 8000;
    // ---- serial fallback (p2app abandoned the tty) ----------------------
    // Upstream p2app consumes ZZZS and ZZZP on the tty itself and, when its
    // startup detection fails, closes the tty and forwards NOTHING — while
    // this service (since the hands-off change) politely never opens it.
    // Net: a working panel can stream into a port nobody holds. So: stay
    // hands-off through p2app's detection window, but if no panel event has
    // arrived on ANY transport for FallbackQuietMs after that window, take
    // the tty ourselves. Sticky for the life of the serial session; while
    // active, LED writes go to the serial port even if the CAT relay is up.
    private const int FallbackQuietMs = 15_000;
    private volatile bool _serialFallback;
    private long _lastPanelEventMs;
    private readonly P2AppSupervisor _p2app;
    private long _p2SessionSinceMs;          // TickCount64 at P2 connect; 0 = down
    private volatile bool _catBounceSpent;

    // Per-iteration linked CTS so a settings change (RequestReconnect) can break
    // the current session/idle wait and force the loop to re-resolve the device.
    private volatile CancellationTokenSource? _wakeCts;

    // Live status for the Radio Settings card (GET /api/radio/front-panel).
    private volatile bool _connected;
    private volatile string? _activePath;
    private volatile int _activeBaud;

    // ANDROMEDA console type announced via ZZZS. 5 = G2 Ultra (Mk2). Actions
    // are only routed for type 5; an unrecognised type is logged and ignored
    // so a different panel's button map can never mis-fire MOX/TUNE.
    private int _panelType;

    // Last LED state pushed to the panel (index = LED number), -1 = unknown so
    // the first poll always writes. G2-Ultra LEDs: 1=MOX, 2=TUNE, 3=PS,
    // 6=RIT, 7=XIT, 9=LOCK.
    private readonly int[] _lastLed = new int[16];

    public G2FrontPanelService(
        IConfiguration config,
        G2PanelSettingsStore store,
        G2PanelMappingStore mappingStore,
        RadioService radio,
        TxService tx,
        BandMemoryStore bandMemory,
        ToolbarSettingsStore toolbarSettings,
        P2AppSupervisor p2app,
        ILogger<G2FrontPanelService> log,
        ILoggerFactory loggerFactory)
    {
        _p2app = p2app;
        _opts = new G2PanelOptions();
        config.GetSection(G2PanelOptions.Section).Bind(_opts);
        _store = store;
        _mappingStore = mappingStore;
        _radio = radio;
        _tx = tx;
        _log = log;
        _router = new G2PanelActionRouter(radio, tx, bandMemory, toolbarSettings,
            loggerFactory.CreateLogger<G2PanelActionRouter>(),
            mappingStore);
        // A mapping write takes effect on the very next panel event — no
        // reconnect, no restart.
        _mappingStore.Changed += _router.ReloadOverrides;
        // A Settings change re-resolves the device and reconnects without a
        // server restart (enable toggled, COM port / baud edited).
        _store.Changed += OnSettingsChanged;
        // Session lifecycle feeds the CAT bounce watchdog: the grace clock
        // starts at P2 connect and stops at disconnect.
        _radio.P2Connected += OnP2Connected;
        _radio.P2Disconnected += OnP2Disconnected;
    }

    private void OnSettingsChanged() => RequestReconnect();

    private void OnP2Connected(Zeus.Protocol2.Protocol2Client _) =>
        Interlocked.Exchange(ref _p2SessionSinceMs, Environment.TickCount64);

    private void OnP2Disconnected() =>
        Interlocked.Exchange(ref _p2SessionSinceMs, 0);

    /// <summary>Break the current serial session / idle wait so the run loop
    /// re-reads settings and re-resolves the device. Safe to call from the
    /// settings endpoint thread.</summary>
    public void RequestReconnect()
    {
        try { _wakeCts?.Cancel(); }
        catch (ObjectDisposedException) { /* loop already advanced */ }
    }

    /// <summary>Stored operator settings (Enabled / DevicePath / Baud) layered
    /// over the G2FrontPanel config defaults, with the live bridge status.
    /// Backs GET/PUT /api/radio/front-panel.</summary>
    public G2PanelSettingsDto Snapshot()
    {
        var s = _store.Get();
        return new G2PanelSettingsDto(
            Enabled: s.Enabled,
            DevicePath: s.DevicePath,
            Baud: s.Baud,
            Connected: _connected,
            ActiveDevicePath: _activePath,
            ActiveBaud: _activeBaud,
            PanelType: _panelType,
            AssumeUltra: s.AssumeUltra);
    }

    /// <summary>Inventory + action catalogs + stored overrides + the
    /// press-to-identify stamp. Backs GET /api/radio/front-panel/mapping.</summary>
    public G2PanelMappingDto MappingSnapshot()
    {
        static G2PanelControlDto ToDto((int Id, string Label, string? DefaultAction, bool Pinned) c) =>
            new(c.Id, c.Label, c.DefaultAction, c.Pinned);

        var last = _lastInput;
        G2PanelLastInputDto? lastDto = last is null
            ? null
            : new G2PanelLastInputDto(
                last.Kind,
                last.Id,
                (int)Math.Clamp(Environment.TickCount64 - last.TickMs, 0, int.MaxValue));

        return new G2PanelMappingDto(
            Buttons: G2PanelActionRouter.ButtonInventory().Select(ToDto).ToArray(),
            Encoders: G2PanelActionRouter.EncoderInventory().Select(ToDto).ToArray(),
            ButtonActions: G2PanelActionRouter.ButtonActionNames(),
            EncoderActions: G2PanelActionRouter.EncoderActionNames(),
            ButtonOverrides: _mappingStore.Overrides(G2PanelMappingStore.KindButton)
                .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            EncoderOverrides: _mappingStore.Overrides(G2PanelMappingStore.KindEncoder)
                .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            LastInput: lastDto);
    }

    // Stored settings layered over config: a stored value wins, else the
    // G2FrontPanel config value, else unset (→ auto-detect / per-symlink baud).
    private (bool Enabled, string? DevicePath, int Baud) Effective()
    {
        var s = _store.Get();
        string? path = !string.IsNullOrWhiteSpace(s.DevicePath) ? s.DevicePath
            : (!string.IsNullOrWhiteSpace(_opts.DevicePath) ? _opts.DevicePath : null);
        int baud = s.Baud > 0 ? s.Baud : _opts.Baud;
        return (s.Enabled, path, baud);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = CatBounceWatchdogAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            // Fresh linked CTS each pass so RequestReconnect() (settings change)
            // can break this iteration without tearing down the service.
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _wakeCts = wake;
            var ct = wake.Token;

            var eff = Effective();
            if (!eff.Enabled)
            {
                // Disabled in Settings — wait until that changes (or shutdown),
                // holding the port closed. RequestReconnect wakes us.
                StopCatListener();
                _log.LogInformation("g2panel.disabled (settings/config Enabled=false)");
                await DelaySafe(Timeout.InfiniteTimeSpan, ct);
                continue;
            }

            EnsureCatListener(stoppingToken);
            bool panelQuiet = Environment.TickCount64 - Interlocked.Read(ref _lastPanelEventMs) > FallbackQuietMs;
            bool fallbackDue = panelQuiet && !_p2app.TtyDetectionGraceActive;
            if (_catActive && !fallbackDue)
            {
                // p2app is relaying the panel over TCP — the serial line
                // belongs to p2app; opening it here would contend. Idle.
                await DelaySafe(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            if (_p2app.TtyBelongsToP2App && !fallbackDue)
            {
                // Radio host, p2app alive or imminent: the panel tty is
                // p2app's even while the CAT link is down. Opening it here
                // puts two readers on one line — the original disease — and
                // worse, a p2app starting into that contention can lose its
                // own ZZZS exchange with the panel and come up blind: no
                // panel threads, no event forwarding, no identity announce,
                // for that p2app's whole life. Field log: g2panel.open
                // within 1 ms of every CAT drop, racing each fresh p2app.
                // On this host the panel path is the CAT relay, full stop;
                // idle and let the relay (and the bounce watchdog) work.
                await DelaySafe(TimeSpan.FromSeconds(2), ct);
                continue;
            }

            _serialFallback = _catActive || _p2app.TtyBelongsToP2App;
            if (_serialFallback)
                _log.LogInformation(
                    "g2panel.fallback — no panel traffic on any transport for {Quiet}s and p2app's detection window has passed; taking the serial line",
                    FallbackQuietMs / 1000);

            var dev = ResolveDevice(eff.DevicePath, eff.Baud);
            if (dev is null)
            {
                // No panel on this host — idle and re-probe. Quiet by design.
                _serialFallback = false;
                await DelaySafe(TimeSpan.FromSeconds(10), ct);
                continue;
            }

            try
            {
                await RunPanelAsync(dev.Value.Path, dev.Value.Baud, ct);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Wake from a settings change — loop and re-resolve.
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "g2panel.session.error device={Dev}; reconnecting", dev.Value.Path);
                await DelaySafe(TimeSpan.FromSeconds(5), ct);
            }
            finally
            {
                _serialFallback = false;
                ClosePort();
            }
        }
    }

    private (string Path, int Baud)? ResolveDevice(string? devicePath, int baud)
    {
        if (!string.IsNullOrWhiteSpace(devicePath))
            return (devicePath!, baud > 0 ? baud : 9600);

        // Auto-detect the udev-published symlink (Linux / G2 Pi).
        foreach (var (path, symlinkBaud) in G2PanelOptions.KnownSymlinks)
        {
            try { if (File.Exists(path)) return (path, baud > 0 ? baud : symlinkBaud); }
            catch { /* path probing is best-effort */ }
        }
        return null;
    }

    private async Task RunPanelAsync(string path, int baud, CancellationToken ct)
    {
        _panelType = 0;
        Array.Fill(_lastLed, -1);

        var port = new SerialPort(path, baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 500,
            WriteTimeout = 500,
            NewLine = ";",
        };
        port.Open();
        _port = port;
        _activePath = path;
        _activeBaud = baud;
        _connected = true;
        _log.LogInformation("g2panel.open device={Dev} baud={Baud}", path, baud);

        // Opening the line resets Arduino-class panels into the bootloader for
        // ~0.5 s; wait before the first byte so ZZZS isn't lost.
        await Task.Delay(TimeSpan.FromMilliseconds(700), ct);

        // Ask the panel to identify itself; the ZZZS reply sets _panelType.
        Send("ZZZS;");
        if (_store.Get().AssumeUltra)
        {
            _panelType = 5;
            _log.LogInformation("g2panel.assume — operator override: treating serial panel as G2-Ultra (type 5)");
            RefreshLeds();
        }

        var ledLoop = LedPollLoop(ct);
        var panelFlushLoop = PanelFlushLoop(ct);
        try
        {
            await ReadLoop(port, ct);
        }
        finally
        {
            try { await ledLoop; } catch { /* surfaced by ReadLoop */ }
            try { await panelFlushLoop; } catch { /* surfaced by ReadLoop */ }
        }
    }

    private async Task PanelFlushLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (_panelType == 5 && _router.FlushPendingPanelWork())
                RefreshLeds();
        }
    }

    private async Task ReadLoop(SerialPort port, CancellationToken ct)
    {
        var buf = new byte[256];
        var stream = port.BaseStream;
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            }
            catch (TimeoutException) { continue; }

            if (n <= 0) { await Task.Delay(20, ct); continue; }

            // ANDROMEDA is 7-bit ASCII; decode straight to chars.
            var text = Encoding.ASCII.GetString(buf, 0, n);
            _parser.Feed(text, OnEvent);
        }
    }

    private void OnEvent(PanelEvent ev)
    {
        Interlocked.Exchange(ref _lastPanelEventMs, Environment.TickCount64);

        // Identify telemetry (mapping grid row flash). Deliberately before the
        // type-5 gate — reporting a raw id routes nothing — and deliberately
        // NOT for VFO events, so spinning the dial can't hijack the flash.
        switch (ev)
        {
            case PanelEvent.Button b:
                _lastInput = new PanelInputStamp(G2PanelMappingStore.KindButton, b.Id, Environment.TickCount64);
                break;
            case PanelEvent.Encoder e:
                _lastInput = new PanelInputStamp(G2PanelMappingStore.KindEncoder, e.Id, Environment.TickCount64);
                break;
        }

        if (ev is PanelEvent.Version ver)
        {
            _panelType = ver.Type;
            _log.LogInformation("g2panel.version type={Type} raw={Raw}", ver.Type, ver.Raw);
            if (ver.Type != 5)
                _log.LogWarning("g2panel.unsupported type={Type} (only G2-Ultra type-5 actions are mapped)", ver.Type);
            // Push current LED state immediately on (re)identification.
            RefreshLeds();
            return;
        }

        // Don't route buttons/encoders until we know we're talking to a
        // G2-Ultra — a wrong button map could mis-key the transmitter.
        if (_panelType != 5) return;

        _router.Dispatch(ev);
        // VFO ticks do not change LED state, and retunes are flushed off the
        // serial read path. Other controls still get immediate LED feedback.
        if (ev is not PanelEvent.Vfo)
            RefreshLeds();
    }

    private async Task LedPollLoop(CancellationToken ct)
    {
        // Periodic LED reconciliation mirrors deskhpsdr's 500 ms andromeda
        // timer; covers state changes the panel didn't originate.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (_panelType == 5) RefreshLeds();
        }
    }

    // Compute the G2-Ultra LED set from current radio state and emit ZZZI for
    // any that changed. LEDs Zeus has no state for (ATU=4, active-RX=8) stay
    // off — see the gap table.
    private void RefreshLeds()
    {
        var s = _radio.Snapshot();
        SetLed(1, _tx.IsMoxOn);        // MOX
        SetLed(2, _tx.IsTunOn);        // TUNE
        SetLed(3, s.PsEnabled);        // PureSignal (readback only)
        SetLed(6, s.RitEnabled);       // RIT
        SetLed(7, s.XitEnabled);       // XIT
        SetLed(9, s.VfoLocked);        // LOCK
    }

    private void SetLed(int led, bool on)
    {
        int v = on ? 1 : 0;
        if (_lastLed[led] == v) return;
        _lastLed[led] = v;
        Send(AndromedaParser.LedCommand(led, on));
    }

    private void Send(string cmd)
    {
        // Whichever link carries the panel carries the LEDs. During serial
        // fallback the CAT relay may be up but panel-less (p2app closed the
        // tty), so the serial port is the panel path — write there.
        var cat = _serialFallback ? null : _catStream;
        if (cat is not null)
        {
            try
            {
                var bytes = Encoding.ASCII.GetBytes(cmd);
                lock (_writeLock) cat.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "g2panel.cat.write.failed cmd={Cmd}", cmd);
            }
            return;
        }
        var port = _port;
        if (port is null || !port.IsOpen) return;
        try
        {
            lock (_writeLock)
            {
                if (port.IsOpen) port.Write(cmd);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "g2panel.write.failed cmd={Cmd}", cmd);
        }
    }

    private async Task CatBounceWatchdogAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(ct))
            {
                // Panel alive on either transport — nothing to cure, and a
                // proven link re-arms the one-shot for the NEXT stall (each
                // fresh P2 session needs its own p2app CAT thread upstream,
                // so the next reconnect may legitimately stall again).
                // A live CAT link that never identified gets ZZZS asked
                // again each pass — the connect-time ask can race p2app's
                // own tty bring-up, and an unanswered identify keeps the
                // type-5 gate shut with the panel looking dead.
                if (_catActive && _panelType != 5) Send("ZZZS;");
                if (_catActive || _panelType == 5) { _catBounceSpent = false; continue; }
                if (_catBounceSpent) continue;
                if (!_store.Get().Enabled) continue;
                long since = Interlocked.Read(ref _p2SessionSinceMs);
                if (since == 0 || Environment.TickCount64 - since < CatGraceMs) continue;
                if (_tx.IsMoxOn || _tx.IsTunOn) continue;   // never yank the data path mid-transmit; retry after

                _catBounceSpent = true;   // one attempt per stall, cure or not
                bool bounced = await _p2app.BounceOwnedChildAsync(
                    "panel enabled, P2 session up, no CAT callback in 8s — a fresh p2app will dial the stable port");
                if (bounced)
                    _log.LogInformation(
                        "g2panel.cat.bounce — p2app restarted; the session will blip and reconnect, panel CAT should follow");
                else
                    _log.LogInformation(
                        "g2panel.cat.stalled — no CAT callback and p2app is not Zeus-owned; " +
                        "restart it yourself (pkill p2app) while the session is up");
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { _log.LogDebug(ex, "g2panel.cat.watchdog fault"); }
    }

    /// <summary>Start the CAT callback listener once and publish its port in
    /// the P2 high-priority packet. p2app connects only when a panel exists
    /// and a P2 session is up, so an idle listener costs nothing.</summary>
    // Fixed CAT callback port (loopback traffic only in practice; announced in
    // the P2 HP packet). Chosen from the dynamic range, unregistered.
    private const int StableCatPort = 51730;

    private void EnsureCatListener(CancellationToken serviceCt)
    {
        if (_catListener is not null) return;
        try
        {
            // A STABLE port, not an ephemeral one. p2app latches the CAT
            // callback port from the first high-priority packet it examines
            // and keeps it for its whole (long-lived, supervised) life — an
            // ephemeral port meant only the first Zeus session after a p2app
            // start ever connected; every Zeus restart announced a new number
            // p2app ignored (field: manual p2app restart connects, fresh Zeus
            // start doesn't). Same fixed port every session keeps the latch
            // valid forever. Ephemeral fallback only if the port is taken.
            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Any, StableCatPort);
                listener.Start();
            }
            catch (SocketException)
            {
                listener = new TcpListener(IPAddress.Any, 0);
                listener.Start();
                _log.LogWarning("g2panel.cat stable port {Port} busy — ephemeral fallback (panel reconnect may need a p2app restart)", StableCatPort);
            }
            _catListener = listener;
            _catPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            Zeus.Protocol2.Protocol2Client.PanelCatAnnouncePort = _catPort;
            _log.LogInformation("g2panel.cat listening on {Port} (announced in the P2 high-priority packet)", _catPort);
            _ = Task.Run(() => CatAcceptLoop(listener, serviceCt), serviceCt);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "g2panel.cat listener failed to start — serial path only");
        }
    }

    private async Task CatAcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { return; }   // listener stopped
            _ = Task.Run(() => CatSessionAsync(client, ct), ct);
        }
    }

    private async Task CatSessionAsync(TcpClient client, CancellationToken ct)
    {
        // Adopt the newest connection (p2app reconnects across sessions).
        var old = _catStream;
        try { old?.Dispose(); } catch { /* superseded link */ }

        using var _ = client;
        var stream = client.GetStream();
        _catStream = stream;
        _catActive = true;
        _connected = true;
        _activePath = $"p2app CAT ({client.Client.RemoteEndPoint})";
        _activeBaud = 0;
        if (_serialFallback)
        {
            _log.LogInformation("g2panel.cat connected from {Remote} — serial fallback active, keeping the serial panel path", client.Client.RemoteEndPoint);
        }
        else
        {
            _log.LogInformation("g2panel.cat connected from {Remote} — serial path standing down", client.Client.RemoteEndPoint);
            RequestReconnect();   // break an open serial session; the loop idles while _catActive
        }

        // The serial path resets state and asks ZZZS at open; the CAT path
        // must do the same. The identify p2app itself performs against the
        // panel happens at p2app STARTUP — before this relay exists — so
        // that reply is never forwarded here. Without our own ZZZS the
        // type-5 gate stays shut and every button dies silently (field:
        // CAT connected, panel dead, no g2panel.version in the log). ZZZI
        // already proves the socket is write-through to the panel; ZZZS
        // rides the same path and the Version reply opens the gate.
        _panelType = 0;
        Array.Fill(_lastLed, -1);
        Send("ZZZS;");

        // piHPSDR trust model, opt-in: the operator has declared the panel a
        // G2-Ultra, so the gate opens on configuration instead of waiting for
        // an identify that some panel paths never deliver. ZZZS still goes
        // out — a real Version reply simply confirms (or corrects) the type.
        if (_store.Get().AssumeUltra)
        {
            _panelType = 5;
            _log.LogInformation("g2panel.assume — operator override: treating CAT peer as G2-Ultra (type 5)");
            RefreshLeds();
        }

        var buf = new byte[256];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                if (n <= 0) break;
                _parser.Feed(Encoding.ASCII.GetString(buf, 0, n), OnEvent);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Session end / supervisor pause / superseded link — all normal.
        }
        if (ReferenceEquals(_catStream, stream))
        {
            _catStream = null;
            _catActive = false;
            _connected = false;
            _panelType = 0;
            _activePath = null;
            _log.LogInformation("g2panel.cat disconnected — serial probing resumes");
            RequestReconnect();
        }
    }

    private void StopCatListener()
    {
        Zeus.Protocol2.Protocol2Client.PanelCatAnnouncePort = 0;
        var l = _catListener;
        _catListener = null;
        try { l?.Stop(); } catch { /* teardown */ }
        var stream = _catStream;
        _catStream = null;
        _catActive = false;
        try { stream?.Dispose(); } catch { /* teardown */ }
    }

    private void ClosePort()
    {
        _connected = false;
        _panelType = 0;
        var port = _port;
        _port = null;
        if (port is null) return;
        try { if (port.IsOpen) port.Close(); } catch { /* ignore */ }
        port.Dispose();
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }

    public override void Dispose()
    {
        _store.Changed -= OnSettingsChanged;
        _radio.P2Connected -= OnP2Connected;
        _radio.P2Disconnected -= OnP2Disconnected;
        StopCatListener();
        ClosePort();
        base.Dispose();
    }
}
