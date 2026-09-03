// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// PaCalibrationService — automated per-band PA gain calibration, opened the
// way every keyed feature in this fork opens: COLD FIRST. This commit ships
// the state machine, the safety gates, and a DRY-RUN band walk that keys
// NOTHING — it visits each requested band, records the current gain and the
// board's power target, and produces the review table's skeleton. The keyed
// measurement iteration (short TUN bursts, closed-loop Δgain =
// 10·log10(Ptarget/Pmeasured), SWR/temp/no-response aborts, per-band and
// total keyed-time budgets) lands in the next commit behind these same
// gates, exactly as the TX arming ceremony followed the cold DUC probes.
//
// Plumbing this rests on (all verified in-tree): per-band gain lives in
// PaSettingsStore (board-keyed; PaBandSettingsDto.PaGainDb), consumed by
// EncodeDriveByte; the power target is the board profile's own
// PaMaxPowerWatts (100 for a G2, 1000 for a G2-1K — no model guessing);
// live forward watts + SWR arrive per meters frame on
// TxMetersService.TxMetersUpdated; keying goes through TxService.TrySetTun
// when the hot loop lands.

using System.Collections.Concurrent;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class PaCalibrationService : IDisposable
{
    public enum Phase { Idle, Running, Review, Failed, Aborted }

    public sealed record BandRow(
        string Band,
        double BeforeGainDb,
        double TargetWatts,
        double? MeasuredWatts,     // dry run: null — no RF was made
        double? ProposedGainDb,    // dry run: null — nothing measured
        string Status);

    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly PaSettingsStore _store;
    private readonly ILogger<PaCalibrationService> _log;

    private readonly object _lock = new();
    private Phase _phase = Phase.Idle;
    private string? _error;
    private string _board = "";
    private double _targetWatts;
    private readonly List<BandRow> _rows = new();
    private CancellationTokenSource? _cts;

    // Latest meters sample — the hot loop's sensor. Cached here so the
    // measurement commit only has to read fields, and so /status can show
    // the operator what the service sees.
    private volatile float _lastFwdW = -1;
    private volatile float _lastSwr = -1;
    private long _lastMetersTicks;

    public PaCalibrationService(
        RadioService radio, TxService tx, TxMetersService meters,
        PaSettingsStore store, ILogger<PaCalibrationService> log)
    {
        _radio = radio;
        _tx = tx;
        _store = store;
        _log = log;
        meters.TxMetersUpdated += (fwd, _refW, swr, _alcPk, _alcGr) =>
        {
            _lastFwdW = fwd;
            _lastSwr = swr;
            Interlocked.Exchange(ref _lastMetersTicks, DateTime.UtcNow.Ticks);
        };
    }

    public object Status()
    {
        lock (_lock)
        {
            double metersAgeS = _lastMetersTicks == 0
                ? -1
                : (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastMetersTicks)) / (double)TimeSpan.TicksPerSecond;
            return new
            {
                phase = _phase.ToString(),
                mode = "dry-run only — the keyed measurement loop is the next commit; this build makes no RF",
                board = _board,
                targetWatts = _targetWatts,
                rows = _rows.ToArray(),
                lastFwdWatts = _lastFwdW < 0 ? (float?)null : _lastFwdW,
                lastSwr = _lastSwr < 0 ? (float?)null : _lastSwr,
                metersAgeSeconds = metersAgeS < 0 ? (double?)null : Math.Round(metersAgeS, 1),
                error = _error,
            };
        }
    }

    /// <summary>The gate screen, server-side. Refusals name their reason;
    /// passing them starts the dry-run walk.</summary>
    public object Start(string? confirm, string[]? bands, out string? refusal)
    {
        refusal = null;
        if (!string.Equals(confirm, "i-have-a-rated-dummy-load", StringComparison.OrdinalIgnoreCase))
        { refusal = "calibration requires confirm:'i-have-a-rated-dummy-load' — and the load itself, rated for this radio's full power, which only you can verify"; return new { ok = false }; }
        if (!_radio.IsConnected)
        { refusal = "no radio session — connect (or START RX natively) first"; return new { ok = false }; }
        if (_tx.MoxOwner is not null)
        { refusal = "TX is currently keyed — calibration will not start under power"; return new { ok = false }; }

        lock (_lock)
        {
            if (_phase == Phase.Running)
            { refusal = "a calibration is already running — abort it first"; return new { ok = false }; }

            // Board resolution mirrors /api/pa-settings exactly: an explicit
            // request wins, otherwise the radio's effective board — so the
            // calibration always targets the profile the drive path uses.
            var kind = _radio.EffectiveBoardKind;
            PaSettingsDto cfg;
            try { cfg = _store.GetAll(kind); }
            catch (Exception ex)
            { refusal = $"pa-settings unavailable: {ex.Message}"; return new { ok = false }; }

            _board = kind.ToString();
            _targetWatts = cfg.Global.PaMaxPowerWatts;
            if (_targetWatts <= 0)
            { refusal = "board profile reports no PaMaxPowerWatts — cannot derive a power target"; return new { ok = false }; }

            _rows.Clear();
            _error = null;
            _phase = Phase.Running;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var wanted = (bands is { Length: > 0 })
                ? bands
                : cfg.Bands.Select(b => b.Band).ToArray();
            var byBand = cfg.Bands.ToDictionary(b => b.Band, b => b);

            _log.LogWarning(
                "pa-cal DRY RUN start: board={Board} target={W}W bands={N} (no RF in this build)",
                _board, _targetWatts, wanted.Length);

            _ = Task.Run(() => DryRunWalk(wanted, byBand, ct), ct);
            return new { ok = true, board = _board, targetWatts = _targetWatts, bands = wanted };
        }
    }

    private void DryRunWalk(
        string[] bands, Dictionary<string, PaBandSettingsDto> byBand, CancellationToken ct)
    {
        try
        {
            foreach (var band in bands)
            {
                if (ct.IsCancellationRequested) return;
                byBand.TryGetValue(band, out var cfg);
                lock (_lock)
                {
                    _rows.Add(new BandRow(
                        Band: band,
                        BeforeGainDb: cfg?.PaGainDb ?? 0,
                        TargetWatts: _targetWatts,
                        MeasuredWatts: null,
                        ProposedGainDb: null,
                        Status: cfg is null ? "no band config" : "dry-run: gates passed, awaiting hot loop"));
                }
                Thread.Sleep(120);   // the wizard's progress cadence, rehearsed
            }
            lock (_lock) { if (_phase == Phase.Running) _phase = Phase.Review; }
            _log.LogWarning("pa-cal DRY RUN complete: {N} bands walked, zero RF", bands.Length);
        }
        catch (Exception ex)
        {
            lock (_lock) { _phase = Phase.Failed; _error = ex.Message; }
            _log.LogError(ex, "pa-cal dry run failed");
        }
    }

    public object Abort()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            if (_phase == Phase.Running) _phase = Phase.Aborted;
        }
        // When the hot loop exists, abort ALSO unkeys unconditionally —
        // wired here now so the contract is already written:
        _tx.TrySetTun(false, out _);
        _log.LogWarning("pa-cal ABORT");
        return new { ok = true };
    }

    public void Dispose() => _cts?.Cancel();
}
