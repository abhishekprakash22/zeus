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
        RadioService radio,
        TxService tx,
        BandMemoryStore bandMemory,
        ToolbarSettingsStore toolbarSettings,
        ILogger<G2FrontPanelService> log,
        ILoggerFactory loggerFactory)
    {
        _opts = new G2PanelOptions();
        config.GetSection(G2PanelOptions.Section).Bind(_opts);
        _store = store;
        _radio = radio;
        _tx = tx;
        _log = log;
        _router = new G2PanelActionRouter(radio, tx, bandMemory, toolbarSettings,
            loggerFactory.CreateLogger<G2PanelActionRouter>());
        // A Settings change re-resolves the device and reconnects without a
        // server restart (enable toggled, COM port / baud edited).
        _store.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged() => RequestReconnect();

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
            PanelType: _panelType);
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
            if (_catActive)
            {
                // p2app is relaying the panel over TCP — the serial line
                // belongs to p2app; opening it here would contend. Idle.
                await DelaySafe(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            var dev = ResolveDevice(eff.DevicePath, eff.Baud);
            if (dev is null)
            {
                // No panel on this host — idle and re-probe. Quiet by design.
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
        // Whichever link carries the panel carries the LEDs.
        var cat = _catStream;
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

    /// <summary>Start the CAT callback listener once and publish its port in
    /// the P2 high-priority packet. p2app connects only when a panel exists
    /// and a P2 session is up, so an idle listener costs nothing.</summary>
    private void EnsureCatListener(CancellationToken serviceCt)
    {
        if (_catListener is not null) return;
        try
        {
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
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
        _log.LogInformation("g2panel.cat connected from {Remote} — serial path standing down", client.Client.RemoteEndPoint);
        RequestReconnect();   // break an open serial session; the loop idles while _catActive

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
        StopCatListener();
        ClosePort();
        base.Dispose();
    }
}
