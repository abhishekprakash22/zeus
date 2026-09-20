// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR),
//                         Ramón Martínez (EA5IUE), and contributors.
//
// Spotting — the uploaders behind the Spotting settings panel. It listens to
// the decoders' typed events and reports what this station heard:
//
//   FT8 / FT4 decode  → PskReporterUploader  (IPFIX/UDP, batched, 5 min)
//   WSPR slot         → WsprnetUploader      (HTTP, one call per spot)
//                     → PskReporterUploader  (same batch, mode "WSPR")
//
// WSJT-X sends WSPR only to WSPRnet, but PSK Reporter accepts WSPR reports and
// the common map viewers show them, so a station that hears a WSPR beacon is
// reported to both. Each network still has its own switch.
//
// Both are OFF by default and stay off without a callsign AND grid: every
// spot is attributed to a station, so an anonymous upload is meaningless and
// a surprise upload is worse. The panel's toggles are the only way in.
//
// Frequencies reported are absolute: FT8/FT4 spots are the radio's dial plus
// the decode's audio offset, WSPR spots come out of the decoder already
// absolute. The radio is never touched — this is egress only.

using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Hosting.Digital;

public sealed class SpottingService : IHostedService, IDisposable
{
    /// <summary>PSK Reporter asks clients not to report more often than this.</summary>
    internal static readonly TimeSpan PskFlushInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<SpottingService> _log;
    private readonly SpottingSettingsStore _store;
    private readonly DigitalService _digital;
    private readonly RadioService? _radio;
    private readonly HttpClient _http;
    private readonly WsprnetUploader _wsprnet;
    private readonly PskReporterUploader _psk;

    private CancellationTokenSource? _cts;
    private Timer? _pskTimer;
    private volatile SpottingSettings _settings = SpottingSettings.Default;

    public SpottingService(
        ILogger<SpottingService> log,
        SpottingSettingsStore store,
        DigitalService digital,
        RadioService? radio = null,
        HttpClient? http = null)
    {
        _log = log;
        _store = store;
        _digital = digital;
        _radio = radio;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _wsprnet = new WsprnetUploader(_http, log);
        _psk = new PskReporterUploader(log);
    }

    /// <summary>What /spotting/status reports.</summary>
    public sealed record SpottingStatus(
        bool PskReporterEnabled, bool WsprnetEnabled, string Callsign, string Grid,
        bool IdentityResolved, int Uploaded);

    public SpottingStatus Status
    {
        get
        {
            var s = _settings;
            return new SpottingStatus(
                s.PskReporterEnabled, s.WsprnetEnabled, s.Callsign, s.Grid,
                s.IdentityResolved, _psk.Uploaded + _wsprnet.Uploaded);
        }
    }

    public SpottingStatus Configure(SpottingSettings settings)
    {
        var saved = _store.Set(settings);   // Changed → Apply
        return Status;
    }

    // ---- lifecycle -----------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        Apply(_store.Get());
        _store.Changed += Apply;
        _digital.Events.Ft8Decoded += OnFt8Decoded;
        _digital.Events.WsprSpotted += OnWsprSpotted;
        _pskTimer = new Timer(
            _ => _ = FlushPskAsync(), null, PskFlushInterval, PskFlushInterval);
        _log.LogInformation(
            "spotting: in core (psk={Psk}, wsprnet={Wsprnet}, identity={Id})",
            _settings.PskReporterEnabled, _settings.WsprnetEnabled, _settings.IdentityResolved);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _store.Changed -= Apply;
        _digital.Events.Ft8Decoded -= OnFt8Decoded;
        _digital.Events.WsprSpotted -= OnWsprSpotted;
        _pskTimer?.Dispose();
        _pskTimer = null;
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _pskTimer?.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private void Apply(SpottingSettings settings)
    {
        _settings = settings;
        if (!settings.PskReporterEnabled || !settings.IdentityResolved) _psk.Clear();
    }

    // ---- decode handlers (decoder thread — queue, never block) ---------------

    private void OnFt8Decoded(Ft8DecodeBatch batch)
    {
        var s = _settings;
        if (!s.PskReporterEnabled || !s.IdentityResolved || batch.Decodes.Count == 0) return;

        long dialHz = _radio?.Snapshot().VfoHz ?? 0;
        if (dialHz <= 0) return;                   // no radio, no frequency to report
        long flowStart = batch.SlotStartUnixMs / 1000;

        foreach (var d in batch.Decodes)
        {
            var call = PskReporterUploader.ExtractSenderCallsign(d.Text);
            if (call is null) continue;
            if (string.Equals(call, s.Callsign, StringComparison.OrdinalIgnoreCase)) continue;  // our own TX echo
            _psk.Add(new PskSpot(call, dialHz + d.FreqHz, d.SnrDb, batch.Protocol, flowStart));
        }
    }

    private void OnWsprSpotted(WsprSpotBatch batch)
    {
        var s = _settings;
        if (!s.IdentityResolved || batch.Spots.Length == 0) return;

        // PSK Reporter takes WSPR too, in the same batched report as FT8/FT4.
        // The decoder already gives absolute frequencies in MHz.
        if (s.PskReporterEnabled)
            foreach (var p in WsprPskSpots(batch, s.Callsign)) _psk.Add(p);

        if (!s.WsprnetEnabled) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try { await _wsprnet.UploadSlotAsync(batch, s, SoftwareVersion(), ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "spotting.wsprnet: slot upload failed"); }
        }, ct);
    }

    /// <summary>
    /// A WSPR slot as PSK Reporter sender records: the beacon's callsign at its
    /// absolute frequency, mode "WSPR". Beacons we cannot attribute, and our own
    /// transmissions heard back, are left out.
    /// </summary>
    internal static IEnumerable<PskSpot> WsprPskSpots(WsprSpotBatch batch, string ownCallsign)
    {
        long flowStart = batch.SlotStartUnixMs / 1000;
        foreach (var spot in batch.Spots)
        {
            var tx = WsprnetUploader.ParseMessage(spot.Message);
            if (tx is null) continue;
            if (string.Equals(tx.Callsign, ownCallsign, StringComparison.OrdinalIgnoreCase)) continue;
            yield return new PskSpot(
                tx.Callsign, (long)Math.Round(spot.FreqMhz * 1e6),
                (int)Math.Round(spot.SnrDb), "WSPR", flowStart);
        }
    }

    private async Task FlushPskAsync()
    {
        var s = _settings;
        if (!s.PskReporterEnabled || !s.IdentityResolved) return;
        try { await _psk.FlushAsync(s, SoftwareVersion(), _cts?.Token ?? CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "spotting.pskreporter: flush failed"); }
    }

    /// <summary>How this station identifies itself to both networks.</summary>
    internal static string SoftwareVersion()
    {
        var v = typeof(SpottingService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        return v.Length == 0 ? "ANAN Core" : $"ANAN Core {v}";
    }
}
