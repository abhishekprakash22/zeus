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

    // Hot-loop capture window: while _capturing, every meters frame lands in
    // _capture for median/max analysis. The window is opened after a settle
    // delay and closed before unkeying, so it sees only steady carrier.
    private readonly List<(float Fwd, float Swr)> _capture = new();
    private volatile bool _capturing;
    private readonly Dictionary<string, double> _beforeByBand = new();

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
            if (_capturing)
            {
                lock (_capture) _capture.Add((fwd, swr));
            }
        };
    }

    private string? _activeBand;   // the band a factory run is measuring now

    private static readonly (string Band, long CenterHz)[] BandCenters =
    {
        ("160m", 1_900_000), ("80m", 3_750_000), ("60m", 5_357_000),
        ("40m", 7_150_000), ("30m", 10_125_000), ("20m", 14_175_000),
        ("17m", 18_118_000), ("15m", 21_225_000), ("12m", 24_940_000),
        ("10m", 28_850_000), ("6m", 51_000_000),
    };

    public object Status()
    {
        string? currentBand = null;
        try
        {
            if (_radio.IsConnected)
                currentBand = BandUtils.FreqToBand(RadioService.TxFrequencyHz(_radio.Snapshot()));
        }
        catch { /* status stays best-effort */ }
        lock (_lock)
        {
            double metersAgeS = _lastMetersTicks == 0
                ? -1
                : (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastMetersTicks)) / (double)TimeSpan.TicksPerSecond;
            return new
            {
                phase = _phase.ToString(),
                currentBand,
                activeBand = _activeBand,
                mode = "hot loop armed — /measure calibrates the CURRENT band with short TUN bursts; /start remains the cold dry-run walk",
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

    /// <summary>THE HOT LOOP — calibrates the band the radio is currently
    /// on, and nothing else. The service changes no band and touches no
    /// drive slider: it reads the operator's TUN drive and NORMALIZES
    /// (power scales as drive², so a measurement at d% computes the gain
    /// that yields rated power at 100% — calibrate a G2-1K at 30% drive
    /// and the load sees under a tenth of the kilowatt). Bursts are keyed
    /// ≤0.8 s each, at most three per call with 1 s gaps; SWR above 1.5,
    /// a non-responding forward-power reading, or a meters gap aborts
    /// unkeyed. Gain moves at most ±3 dB per pass.</summary>
    public object Measure(string? confirm, out string? refusal)
    {
        refusal = null;
        if (!string.Equals(confirm, "i-have-a-rated-dummy-load", StringComparison.OrdinalIgnoreCase))
        { refusal = "measurement requires confirm:'i-have-a-rated-dummy-load'"; return new { ok = false }; }
        if (!_radio.IsConnected)
        { refusal = "no radio session"; return new { ok = false }; }
        if (_tx.MoxOwner is not null)
        { refusal = "TX is already keyed"; return new { ok = false }; }
        double metersAge = _lastMetersTicks == 0 ? double.MaxValue
            : (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastMetersTicks)) / (double)TimeSpan.TicksPerSecond;
        if (metersAge > 5)
        { refusal = "no TX meters flowing — is the session healthy?"; return new { ok = false }; }

        lock (_lock)
        {
            if (_phase == Phase.Running)
            { refusal = "already running"; return new { ok = false }; }

            var kind = _radio.EffectiveBoardKind;
            var variant = _radio.EffectiveOrionMkIIVariant;
            var cfg = _store.GetAll(kind, variant);
            _board = kind.ToString();
            _targetWatts = cfg.Global.PaMaxPowerWatts;
            if (_targetWatts <= 0)
            { refusal = "board profile reports no PaMaxPowerWatts"; return new { ok = false }; }

            var snap = _radio.Snapshot();
            var band = BandUtils.FreqToBand(RadioService.TxFrequencyHz(snap));
            if (band is null)
            { refusal = "TX frequency is outside any amateur band — tune into the band to calibrate"; return new { ok = false }; }
            int tunePct = snap.TunePct;
            if (tunePct < 5)
            { refusal = $"TUN drive is {tunePct}% — raise it to at least 5% (the math normalizes to 100%)"; return new { ok = false }; }

            var bandCfg = cfg.Bands.FirstOrDefault(b => b.Band == band) ?? new PaBandSettingsDto(band);
            _error = null;
            _phase = Phase.Running;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _log.LogWarning(
                "pa-cal HOT start: board={Board} band={Band} target={W}W tunDrive={D}% gain={G:F1}dB",
                _board, band, _targetWatts, tunePct, bandCfg.PaGainDb);
            _ = Task.Run(() => HotLoop(band, bandCfg.PaGainDb, tunePct, kind, variant, ct), ct);
            return new { ok = true, band, targetWatts = _targetWatts, tunDrivePct = tunePct, startGainDb = bandCfg.PaGainDb };
        }
    }

    private void HotLoop(
        string band, double gainDb, int tunePct,
        HpsdrBoardKind kind, OrionMkIIVariant variant, CancellationToken ct)
    {
        MeasureBandCore(band, gainDb, tunePct, kind, variant, ct, finishPhase: true);
    }

    /// <summary>The measurement core, shared by the single-band button and
    /// the factory run. Returns true when the band calibrated (or its row
    /// recorded a per-band verdict); false on a hard failure that should
    /// stop a sequence. When finishPhase, sets the service phase itself.</summary>
    private bool MeasureBandCore(
        string band, double gainDb, int tunePct,
        HpsdrBoardKind kind, OrionMkIIVariant variant, CancellationToken ct,
        bool finishPhase)
    {
        double beforeGain = gainDb;
        lock (_lock) { _beforeByBand.TryAdd(band, beforeGain); }
        double normalize = Math.Pow(100.0 / tunePct, 2);
        try
        {
            for (int pass = 1; pass <= 3; pass++)
            {
                if (ct.IsCancellationRequested) { if (finishPhase) Finish(Phase.Aborted, null); return false; }

                if (!_tx.TrySetTun(true, out var err))
                { Finish(Phase.Failed, $"could not key TUN: {err}"); return false; }
                try
                {
                    Thread.Sleep(350);                 // carrier + meters settle
                    lock (_capture) _capture.Clear();
                    _capturing = true;
                    Thread.Sleep(450);                 // capture window
                    _capturing = false;
                }
                finally
                {
                    _tx.TrySetTun(false, out _);       // unkey NO MATTER WHAT
                }

                (float Fwd, float Swr)[] samples;
                lock (_capture) samples = _capture.ToArray();
                if (samples.Length < 3)
                { Finish(Phase.Failed, "meters gap during capture — no verdict, gain untouched this pass"); return false; }
                float maxSwr = samples.Max(x => x.Swr);
                if (maxSwr > 1.5f)
                { Finish(Phase.Failed, $"SWR {maxSwr:F2} during burst — check the load; gain untouched this pass"); return false; }
                var fwd = samples.Select(x => (double)x.Fwd).OrderBy(x => x).ToArray();
                double median = fwd[fwd.Length / 2];
                double p100 = median * normalize;
                double expected = _targetWatts * Math.Pow(tunePct / 100.0, 2);
                if (median < Math.Max(0.05, 0.02 * expected))
                { Finish(Phase.Failed, $"forward power not responding ({median:F2} W at {tunePct}% drive) — check PA enable / TX path"); return false; }

                double errDb = 10.0 * Math.Log10(_targetWatts / p100);
                _log.LogWarning(
                    "pa-cal pass {Pass}: band={Band} median={Med:F1}W → P100={P:F1}W err={E:+0.00;-0.00}dB gain={G:F2}dB",
                    pass, band, median, p100, errDb, gainDb);

                if (Math.Abs(errDb) <= 0.13)           // within ~±3%
                {
                    lock (_lock)
                    {
                        _rows.RemoveAll(r => r.Band == band);
                        _rows.Add(new BandRow(band, beforeGain, _targetWatts, Math.Round(p100, 1),
                            Math.Round(gainDb, 2), $"calibrated in {pass} pass(es) — within ±3%"));
                    }
                    if (finishPhase) Finish(Phase.Review, null);
                    return true;
                }

                double newGain = Math.Clamp(gainDb + errDb, gainDb - 3, gainDb + 3);
                newGain = Math.Clamp(newGain, 0, 63);
                var cfg = _store.GetAll(kind, variant);
                var newBands = cfg.Bands
                    .Select(b => b.Band == band ? b with { PaGainDb = newGain } : b)
                    .ToList();
                if (!newBands.Any(b => b.Band == band))
                    newBands.Add(new PaBandSettingsDto(band) with { PaGainDb = newGain });
                _store.Save(new PaSettingsDto(cfg.Global, newBands));
                gainDb = newGain;

                Thread.Sleep(1000);                    // inter-burst cool gap
            }
            lock (_lock)
            {
                _rows.RemoveAll(r => r.Band == band);
                _rows.Add(new BandRow(band, beforeGain, _targetWatts, null,
                    Math.Round(gainDb, 2), "did not converge in 3 passes — gain left at last step; re-run or revert"));
            }
            if (finishPhase) Finish(Phase.Review, null);
            return true;
        }
        catch (Exception ex)
        {
            _tx.TrySetTun(false, out _);
            Finish(Phase.Failed, ex.Message);
            _log.LogError(ex, "pa-cal hot loop failed");
            return false;
        }
    }

    /// <summary>FACTORY MODE — every band, one POST. Retunes through the
    /// same VFO path the band buttons use, then VERIFIES the radio landed
    /// on the intended band (FreqToBand — the mismatch guard as a checked
    /// assertion) before a single burst keys. Any hard failure stops the
    /// whole run with the table showing exactly where.</summary>
    public object RunAll(string? confirm, out string? refusal)
    {
        refusal = null;
        if (!string.Equals(confirm, "i-have-a-rated-dummy-load", StringComparison.OrdinalIgnoreCase))
        { refusal = "the factory run requires confirm:'i-have-a-rated-dummy-load'"; return new { ok = false }; }
        if (!_radio.IsConnected) { refusal = "no radio session"; return new { ok = false }; }
        if (_tx.MoxOwner is not null) { refusal = "TX is already keyed"; return new { ok = false }; }

        lock (_lock)
        {
            if (_phase == Phase.Running) { refusal = "already running"; return new { ok = false }; }
            var kind = _radio.EffectiveBoardKind;
            var variant = _radio.EffectiveOrionMkIIVariant;
            var cfg = _store.GetAll(kind, variant);
            _board = kind.ToString();
            _targetWatts = cfg.Global.PaMaxPowerWatts;
            if (_targetWatts <= 0) { refusal = "board profile reports no PaMaxPowerWatts"; return new { ok = false }; }
            int tunePct = _radio.Snapshot().TunePct;
            if (tunePct < 5)
            { refusal = $"TUN drive is {tunePct}% — raise it to at least 5%"; return new { ok = false }; }

            var sequence = cfg.Bands.Select(b => b.Band)
                .Where(b => BandCenters.Any(c => c.Band == b)).ToArray();
            _rows.Clear();
            _error = null;
            _phase = Phase.Running;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _log.LogWarning("pa-cal FACTORY RUN start: board={Board} target={W}W bands={N} tunDrive={D}%",
                _board, _targetWatts, sequence.Length, tunePct);

            _ = Task.Run(() =>
            {
                try
                {
                    foreach (var band in sequence)
                    {
                        if (ct.IsCancellationRequested) { Finish(Phase.Aborted, null); return; }
                        _activeBand = band;
                        long hz = BandCenters.First(c => c.Band == band).CenterHz;
                        _radio.SetVfo(hz);
                        Thread.Sleep(1200);   // retune + PA table swap settle
                        var landed = BandUtils.FreqToBand(RadioService.TxFrequencyHz(_radio.Snapshot()));
                        if (landed != band)
                        { Finish(Phase.Failed, $"retune verify failed: asked for {band}, radio reads {landed ?? "out of band"}"); return; }
                        var freshCfg = _store.GetAll(kind, variant);
                        var bandGain = freshCfg.Bands.FirstOrDefault(b => b.Band == band)?.PaGainDb ?? 0;
                        if (!MeasureBandCore(band, bandGain, tunePct, kind, variant, ct, finishPhase: false))
                            return;           // core already set Failed/Aborted with its reason
                        Thread.Sleep(2000);   // inter-band cool gap
                    }
                    Finish(Phase.Review, null);
                    _log.LogWarning("pa-cal FACTORY RUN complete: {N} band rows", _rows.Count);
                }
                finally { _activeBand = null; }
            }, ct);
            return new { ok = true, bands = sequence, targetWatts = _targetWatts, tunDrivePct = tunePct };
        }
    }

    private void Finish(Phase phase, string? error)
    {
        _capturing = false;
        lock (_lock) { _phase = phase; if (error is not null) _error = error; }
    }

    /// <summary>Restore every gain this session touched to its pre-
    /// calibration value.</summary>
    public object Revert()
    {
        lock (_lock)
        {
            if (_beforeByBand.Count == 0) return new { ok = true, reverted = 0 };
            var kind = _radio.EffectiveBoardKind;
            var variant = _radio.EffectiveOrionMkIIVariant;
            var cfg = _store.GetAll(kind, variant);
            var newBands = cfg.Bands
                .Select(b => _beforeByBand.TryGetValue(b.Band, out var g) ? b with { PaGainDb = g } : b)
                .ToList();
            _store.Save(new PaSettingsDto(cfg.Global, newBands));
            int n = _beforeByBand.Count;
            _beforeByBand.Clear();
            _rows.Clear();
            _phase = Phase.Idle;
            _log.LogWarning("pa-cal REVERT: {N} band(s) restored", n);
            return new { ok = true, reverted = n };
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
