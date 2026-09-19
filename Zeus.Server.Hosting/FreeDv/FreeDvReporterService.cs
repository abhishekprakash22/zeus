// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// FreeDvReporterService — the FreeDV Reporter (qso.freedv.org) network client.
// Protocol and event shapes follow freedv-gui 2.1.0 src/reporting/
// FreeDVReporter.cpp (protocol_version 2); transport is FreeDvReporterClient.
//
// WHEN IT CONNECTS
//   - "report" role — the operator opted in (reportEnabled) AND gave a callsign
//     and grid square: stays connected, like freedv-gui, and puts the station
//     on the public map. Hidden (hide_self) while the radio is not in FREEDV,
//     the equivalent of freedv-gui's analog mode.
//   - "view" role — otherwise, only while someone is looking: every GET
//     /stations touches the service, and it disconnects after ViewIdle
//     without a poll. No personal data is sent in view role.
//
// WHAT IT SENDS (report role, once connection_successful arrives)
//   freq_change {freq}, tx_report {mode, transmitting}, message_update
//   {message} whenever they change (polled every Tick), and rx_report
//   {callsign, mode, snr} once per RADEV1 End-of-Over callsign decoded.
//
// WHAT IT KEEPS
//   One row per station from new_connection / remove_connection / freq_change
//   / tx_report / rx_report / message_update (bulk_update replays a list of
//   those on connect). Cleared on disconnect. Plus the last qsy_request sent
//   to us, which the panel shows until dismissed or five minutes old.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Server.Hosting.FreeDv;

public sealed class FreeDvReporterService : IHostedService, IDisposable
{
    internal const string DefaultHost = "qso.freedv.org";
    internal const int ProtocolVersion = 2;
    private static readonly TimeSpan ViewIdle = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    private readonly ILogger<FreeDvReporterService> _log;
    private readonly FreeDvSettingsStore _store;
    private readonly FreeDvModemService _modem;
    private readonly RadioService? _radio;
    private readonly Uri _uri;

    private readonly object _gate = new();
    private readonly Dictionary<string, Station> _stations = new(StringComparer.Ordinal);
    private string _connectionState = "Disconnected";
    private bool _fullyConnected;
    private bool _reportRole;
    private string? _mySid;
    private FreeDvReporterClient? _client;
    private CancellationTokenSource? _sessionCts;

    private long _lastTouchTicks;               // Environment.TickCount64 of the last /stations poll
    private CancellationTokenSource? _stopCts;
    private Task? _loop;

    // Last values sent in report role (so only changes go out).
    private long _sentFreq = -1;
    private string? _sentMode;
    private bool? _sentTx;
    private string? _sentMessage;
    private bool? _sentHidden;
    private int _rxCallsignSeq;

    public FreeDvReporterService(
        ILogger<FreeDvReporterService> log,
        FreeDvSettingsStore store,
        FreeDvModemService modem,
        RadioService? radio = null,
        string? host = null)
    {
        _log = log;
        _store = store;
        _modem = modem;
        _radio = radio;
        _uri = new Uri($"ws://{host ?? DefaultHost}/socket.io/?EIO=4&transport=websocket");
    }

    // ---- REST surface --------------------------------------------------------

    public sealed record Station(
        string Sid, string Callsign, string? GridSquare, long FreqHz, string Mode,
        bool Transmitting, bool RxOnly, string? Message, string? Version,
        double? LastRxSnr, string? LastRxCallsign, string? LastRxMode,
        string LastUpdate, string? ConnectTime);

    /// <summary>A QSY request another station sent us (the panel offers to tune to it).</summary>
    public sealed record QsyRequest(long Id, string Callsign, long FreqHz, string? Message, DateTimeOffset ReceivedUtc);

    public sealed record StationsSnapshot(
        string ConnectionState, bool Enabled, IReadOnlyList<Station> Stations,
        bool Reporting, string? MySid, QsyRequest? IncomingQsy);

    // Older requests are stale — the other station has likely moved on.
    private static readonly TimeSpan QsyRequestTtl = TimeSpan.FromMinutes(5);
    private QsyRequest? _incomingQsy;
    private long _qsyId;

    /// <summary>Current station list; also keeps a view-role session alive.</summary>
    public StationsSnapshot GetStations()
    {
        Interlocked.Exchange(ref _lastTouchTicks, Environment.TickCount64);
        var settings = _store.GetReporter();
        lock (_gate)
        {
            return new StationsSnapshot(
                _connectionState,
                settings.ReportEnabled,
                _stations.Values.OrderBy(s => s.Callsign, StringComparer.Ordinal).ToArray(),
                Reporting: _fullyConnected && _reportRole,
                MySid: _fullyConnected ? _mySid : null,
                IncomingQsy: _incomingQsy is { } q && DateTimeOffset.UtcNow - q.ReceivedUtc < QsyRequestTtl ? q : null);
        }
    }

    /// <summary>Settings changed — reconnect so the auth (role, callsign, grid) is re-sent.</summary>
    public void ReporterSettingsChanged()
    {
        lock (_gate) _sessionCts?.Cancel();
    }

    /// <summary>Asks another station to QSY to our VFO. False when not reporting or the sid is unknown.</summary>
    public async Task<bool> RequestQsyAsync(string sid, CancellationToken ct)
    {
        FreeDvReporterClient? client;
        lock (_gate)
        {
            if (!(_fullyConnected && _reportRole) || !_stations.ContainsKey(sid) || sid == _mySid) return false;
            client = _client;
        }
        if (client is null || _radio is null) return false;
        await client.EmitAsync("qsy_request",
            new Dictionary<string, object> { ["dest_sid"] = sid, ["message"] = "", ["frequency"] = _radio.Snapshot().VfoHz },
            ct).ConfigureAwait(false);
        return true;
    }

    // ---- lifecycle -----------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopCts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_stopCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopCts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch { /* shutting down */ }
        }
    }

    public void Dispose()
    {
        _stopCts?.Cancel();
        _stopCts?.Dispose();
    }

    private bool ShouldBeConnected(FreeDvReporterSettings s) =>
        IsReportRole(s)
        || Environment.TickCount64 - Interlocked.Read(ref _lastTouchTicks) < (long)ViewIdle.TotalMilliseconds;

    internal static bool IsReportRole(FreeDvReporterSettings s) =>
        s.ReportEnabled && s.Callsign.Length > 0 && s.GridSquare.Length > 0;

    private async Task RunLoopAsync(CancellationToken stop)
    {
        var backoff = MinBackoff;
        bool everConnected = false;
        while (!stop.IsCancellationRequested)
        {
            var settings = _store.GetReporter().Normalized();
            if (!ShouldBeConnected(settings))
            {
                SetState("Disconnected");
                everConnected = false;
                await Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
                continue;
            }

            SetState(everConnected ? "Reconnecting" : "Connecting");
            bool reachedServer = await RunSessionAsync(settings, stop).ConfigureAwait(false);
            if (stop.IsCancellationRequested) break;
            everConnected |= reachedServer;
            backoff = reachedServer ? MinBackoff : Min(backoff * 2, MaxBackoff);
            if (!ShouldBeConnected(_store.GetReporter().Normalized())) continue;
            await Delay(reachedServer ? TimeSpan.FromSeconds(1) : backoff, stop).ConfigureAwait(false);
        }
        SetState("Disconnected");
    }

    /// <summary>One connection, start to finish. True when the server accepted us.</summary>
    private async Task<bool> RunSessionAsync(FreeDvReporterSettings settings, CancellationToken stop)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var client = new FreeDvReporterClient();
        bool report = IsReportRole(settings);
        bool accepted = false;
        lock (_gate)
        {
            _client = client;
            _sessionCts = session;
            _reportRole = report;
            _stations.Clear();
            _fullyConnected = false;
            _mySid = null;
        }
        ResetSent();

        client.Connected += () => { lock (_gate) _mySid = client.Sid; };
        client.EventReceived += (name, args) =>
        {
            if (name == "connection_successful")
            {
                accepted = true;
                lock (_gate) { _fullyConnected = true; _connectionState = "Connected"; }
                _log.LogInformation("freedv.reporter: connected to {Host} as {Role}", _uri.Host, report ? "report" : "view");
                return;
            }
            HandleEvent(name, args);
        };

        var pump = client.RunAsync(_uri, BuildAuthJson(settings), session.Token);
        var ticker = report ? ReportLoopAsync(client, settings, session.Token) : Task.CompletedTask;
        try
        {
            while (!pump.IsCompleted)
            {
                // A view-role session ends once nobody is looking; a settings
                // change cancels the session from ReporterSettingsChanged.
                if (!ShouldBeConnected(_store.GetReporter().Normalized())) session.Cancel();
                await Task.WhenAny(pump, Task.Delay(1000, stop)).ConfigureAwait(false);
            }
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            // Watchdog, idle view or settings change — just reconnect (or not).
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or IOException or System.Net.Http.HttpRequestException)
        {
            _log.LogInformation("freedv.reporter: connection to {Host} failed ({Reason})", _uri.Host, ex.Message);
        }
        catch (OperationCanceledException) { }
        finally
        {
            session.Cancel();
            try { await ticker.ConfigureAwait(false); } catch { }
            lock (_gate)
            {
                _client = null;
                _sessionCts = null;
                _fullyConnected = false;
                _stations.Clear();
                _mySid = null;
            }
            await client.DisposeAsync().ConfigureAwait(false);
        }
        return accepted;
    }

    // ---- report role: push our state ----------------------------------------

    private async Task ReportLoopAsync(FreeDvReporterClient client, FreeDvReporterSettings settings, CancellationToken ct)
    {
        // Skip RX callsigns decoded before this session started.
        _modem.TryGetRxCallsign(-1, out _rxCallsignSeq, out _, out _);
        while (!ct.IsCancellationRequested)
        {
            await Delay(Tick, ct).ConfigureAwait(false);
            bool ready;
            lock (_gate) ready = _fullyConnected;
            if (!ready || ct.IsCancellationRequested) continue;
            try { await PushStateAsync(client, settings, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or IOException)
            {
                _log.LogDebug(ex, "freedv.reporter: send failed");
                return;
            }
        }
    }

    private async Task PushStateAsync(FreeDvReporterClient client, FreeDvReporterSettings settings, CancellationToken ct)
    {
        var state = _radio?.Snapshot();
        bool inFreeDv = state?.Mode == RxMode.FreeDv;
        var modem = _modem.Snapshot();
        string mode = ModeName(modem.Submode);
        bool tx = inFreeDv && (_radio?.IsMox ?? false);

        // freedv-gui hides the station while in analog mode.
        if (_sentHidden != !inFreeDv)
        {
            await client.EmitAsync(inFreeDv ? "show_self" : "hide_self", null, ct).ConfigureAwait(false);
            _sentHidden = !inFreeDv;
            if (inFreeDv) ResetSent(keepHidden: true);   // show_self → resend everything
        }
        if (!inFreeDv) return;

        long freq = state?.VfoHz ?? 0;
        if (freq != _sentFreq)
        {
            await client.EmitAsync("freq_change", new Dictionary<string, object> { ["freq"] = freq }, ct).ConfigureAwait(false);
            _sentFreq = freq;
        }
        if (mode != _sentMode || tx != _sentTx)
        {
            await client.EmitAsync("tx_report",
                new Dictionary<string, object> { ["mode"] = mode, ["transmitting"] = tx }, ct).ConfigureAwait(false);
            _sentMode = mode;
            _sentTx = tx;
        }
        if (settings.Message != _sentMessage)
        {
            await client.EmitAsync("message_update",
                new Dictionary<string, object> { ["message"] = settings.Message }, ct).ConfigureAwait(false);
            _sentMessage = settings.Message;
        }
        if (_modem.TryGetRxCallsign(_rxCallsignSeq, out int seq, out string heard, out int snr))
        {
            await client.EmitAsync("rx_report",
                new Dictionary<string, object> { ["callsign"] = heard, ["mode"] = mode, ["snr"] = snr }, ct).ConfigureAwait(false);
        }
        _rxCallsignSeq = seq;
    }

    private void ResetSent(bool keepHidden = false)
    {
        _sentFreq = -1;
        _sentMode = null;
        _sentTx = null;
        _sentMessage = null;
        if (!keepHidden) _sentHidden = null;
    }

    internal static string ModeName(FreeDvSubmode m) => m switch
    {
        FreeDvSubmode.Mode700D => "700D",
        FreeDvSubmode.Mode700E => "700E",
        FreeDvSubmode.Mode700C => "700C",
        FreeDvSubmode.Mode1600 => "1600",
        FreeDvSubmode.Mode800XA => "800XA",
        FreeDvSubmode.RadeV1 => "RADEV1",
        _ => m.ToString(),
    };

    internal static string BuildAuthJson(FreeDvReporterSettings s)
    {
        var auth = new Dictionary<string, object>();
        if (IsReportRole(s))
        {
            auth["role"] = "report";
            auth["callsign"] = s.Callsign;
            auth["grid_square"] = s.GridSquare;
            auth["version"] = SoftwareVersion();
            auth["rx_only"] = false;
            auth["os"] = OsName();
        }
        else
        {
            auth["role"] = "view";
        }
        auth["protocol_version"] = ProtocolVersion;
        return JsonSerializer.Serialize(auth);
    }

    private static string SoftwareVersion()
    {
        var v = typeof(FreeDvReporterService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        return v.Length == 0 ? "ANAN Core" : $"ANAN Core {v}";
    }

    private static string OsName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
        : "Linux";

    // ---- incoming events -----------------------------------------------------

    /// <summary>Applies one reporter event to the station table (receive loop / tests).</summary>
    internal void HandleEvent(string name, JsonElement args)
    {
        if (name == "bulk_update")
        {
            if (args.ValueKind != JsonValueKind.Array) return;
            foreach (var item in args.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 1
                    && item[0].ValueKind == JsonValueKind.String)
                {
                    HandleEvent(item[0].GetString()!, item.GetArrayLength() > 1 ? item[1] : default);
                }
            }
            return;
        }
        if (args.ValueKind != JsonValueKind.Object) return;
        string? sid = Str(args, "sid");

        lock (_gate)
        {
            switch (name)
            {
                case "new_connection" when sid is not null:
                {
                    string update = Str(args, "last_update") ?? "";
                    _stations[sid] = new Station(
                        sid, Str(args, "callsign") ?? "", Str(args, "grid_square"), 0, "",
                        false, Bool(args, "rx_only"), null, Str(args, "version"),
                        null, null, null, update, update);
                    break;
                }
                case "remove_connection" when sid is not null:
                    _stations.Remove(sid);
                    break;
                case "freq_change" when sid is not null && _stations.TryGetValue(sid, out var st):
                    _stations[sid] = st with
                    {
                        FreqHz = Long(args, "freq") ?? st.FreqHz,
                        LastUpdate = Str(args, "last_update") ?? st.LastUpdate,
                    };
                    break;
                case "tx_report" when sid is not null && _stations.TryGetValue(sid, out var st):
                    _stations[sid] = st with
                    {
                        Mode = Str(args, "mode") ?? st.Mode,
                        Transmitting = Bool(args, "transmitting"),
                        LastUpdate = Str(args, "last_update") ?? st.LastUpdate,
                    };
                    break;
                case "rx_report" when sid is not null && _stations.TryGetValue(sid, out var st):
                    _stations[sid] = st with
                    {
                        LastRxCallsign = Str(args, "callsign"),
                        LastRxSnr = Double(args, "snr"),
                        LastRxMode = Str(args, "mode"),
                        LastUpdate = Str(args, "last_update") ?? st.LastUpdate,
                    };
                    break;
                case "message_update" when sid is not null && _stations.TryGetValue(sid, out var st):
                    _stations[sid] = st with
                    {
                        Message = Str(args, "message"),
                        LastUpdate = Str(args, "last_update") ?? st.LastUpdate,
                    };
                    break;
                case "qsy_request" when Str(args, "callsign") is { } from && Long(args, "frequency") is long hz and > 0:
                    _incomingQsy = new QsyRequest(++_qsyId, from, hz, Str(args, "message"), DateTimeOffset.UtcNow);
                    _log.LogInformation(
                        "freedv.reporter: {Callsign} asks for a QSY to {Freq} Hz ({Message})",
                        from, hz, Str(args, "message"));
                    break;
            }
        }
    }

    private static string? Str(JsonElement o, string p) =>
        o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement o, string p) =>
        o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
    private static long? Long(JsonElement o, string p) =>
        o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : null;
    private static double? Double(JsonElement o, string p) =>
        o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private void SetState(string state)
    {
        lock (_gate)
        {
            if (state != "Connected" || _fullyConnected) _connectionState = state;
            if (state == "Disconnected") _fullyConnected = false;
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static async Task Delay(TimeSpan t, CancellationToken ct)
    {
        try { await Task.Delay(t, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
