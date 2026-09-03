// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// SwrSweepService — the scalar SWR analyzer. Keys a single low-power TUN
// carrier and sweeps the VFO across one amateur band, reading SWR per
// point from the same bridge that feeds the TX meters. Design facts this
// rests on (verified in-tree): SetVfo's only guard is VfoLocked — no MOX
// gate, so the carrier retunes continuously; the meters code suppresses
// SWR below ~2 W forward (SwrMinFwdWatts), so points measured under that
// floor are flagged invalid rather than trusted; SWR protection may fold
// drive back into a bad mismatch — that changes forward power, not the
// bridge ratio, so readings stay honest and protection can stay ON.
//
// Boundaries as architecture: sweeps are IN-BAND ONLY — every point must
// FreqToBand-resolve to the same amateur band (a licensed transmitter
// cannot sweep between allocations, even quietly). One band per run; the
// operator's own TUN drive sets the power (guide: 2-5 W); the VFO is
// restored to where the operator left it, in a finally, alongside the
// unkey. Last two sweeps per band are kept for the compare overlay.

using Zeus.Contracts;

namespace Zeus.Server;

public sealed class SwrSweepService : IDisposable
{
    public enum Phase { Idle, Running, Done, Failed, Aborted }

    public sealed record SweepPoint(long Hz, double? Swr);
    public sealed record Sweep(
        string Band, DateTimeOffset At, IReadOnlyList<SweepPoint> Points,
        long? MinSwrHz, double? MinSwr, long? Span2Low, long? Span2High);

    private static readonly (string Band, long StartHz, long StopHz)[] BandEdges =
    {
        ("160m", 1_810_000, 2_000_000), ("80m", 3_500_000, 3_800_000),
        ("60m", 5_351_500, 5_366_500), ("40m", 7_000_000, 7_300_000),
        ("30m", 10_100_000, 10_150_000), ("20m", 14_000_000, 14_350_000),
        ("17m", 18_068_000, 18_168_000), ("15m", 21_000_000, 21_450_000),
        ("12m", 24_890_000, 24_990_000), ("10m", 28_000_000, 29_700_000),
        ("6m", 50_000_000, 52_000_000),
    };

    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly ILogger<SwrSweepService> _log;

    private readonly object _lock = new();
    private Phase _phase = Phase.Idle;
    private string? _error;
    private string? _band;
    private int _done;
    private int _total;
    private readonly List<SweepPoint> _live = new();
    private readonly Dictionary<string, (Sweep Current, Sweep? Previous)> _byBand = new();
    private CancellationTokenSource? _cts;

    private readonly List<(float Fwd, float Swr)> _capture = new();
    private volatile bool _capturing;

    public SwrSweepService(RadioService radio, TxService tx, TxMetersService meters, ILogger<SwrSweepService> log)
    {
        _radio = radio;
        _tx = tx;
        _log = log;
        meters.TxMetersUpdated += (fwd, _r, swr, _a, _g) =>
        {
            if (_capturing) { lock (_capture) _capture.Add((fwd, swr)); }
        };
    }

    public object Status()
    {
        lock (_lock)
        {
            _byBand.TryGetValue(_band ?? "", out var pair);
            return new
            {
                phase = _phase.ToString(),
                band = _band,
                progress = new { done = _done, total = _total },
                live = _live.ToArray(),
                current = pair.Current,
                previous = pair.Previous,
                error = _error,
                bands = BandEdges.Select(b => b.Band).ToArray(),
            };
        }
    }

    public object Start(string? confirm, string? band, int? points, out string? refusal)
    {
        refusal = null;
        if (!string.Equals(confirm, "antenna-connected", StringComparison.OrdinalIgnoreCase))
        { refusal = "sweep requires confirm:'antenna-connected' — this transmits into the antenna under test"; return new { ok = false }; }
        if (!_radio.IsConnected) { refusal = "no radio session"; return new { ok = false }; }
        if (_tx.MoxOwner is not null) { refusal = "TX is already keyed"; return new { ok = false }; }
        var snap = _radio.Snapshot();
        if (snap.VfoLocked) { refusal = "VFO is locked — unlock to sweep"; return new { ok = false }; }
        if (snap.TunePct < 3) { refusal = $"TUN drive is {snap.TunePct}% — set it for roughly 2-5 W forward"; return new { ok = false }; }

        var edge = BandEdges.FirstOrDefault(b => b.Band == band);
        if (edge.Band is null)
        {
            var current = BandUtils.FreqToBand(RadioService.TxFrequencyHz(snap));
            edge = BandEdges.FirstOrDefault(b => b.Band == current);
            if (edge.Band is null)
            { refusal = "no band given and the VFO is outside any sweepable band"; return new { ok = false }; }
        }
        // In-band proof by construction: endpoints come from the band table
        // and every generated point lies between them.
        int n = Math.Clamp(points ?? 80, 20, 150);

        lock (_lock)
        {
            if (_phase == Phase.Running) { refusal = "a sweep is already running — abort it first"; return new { ok = false }; }
            _phase = Phase.Running;
            _error = null;
            _band = edge.Band;
            _done = 0;
            _total = n;
            _live.Clear();
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            long restoreHz = snap.RadioLoHz > 0 ? snap.RadioLoHz : edge.StartHz;
            _log.LogWarning("swr-sweep start: {Band} {Start}-{Stop} Hz, {N} pts, tunDrive={D}%",
                edge.Band, edge.StartHz, edge.StopHz, n, snap.TunePct);
            _ = Task.Run(() => Run(edge.Band, edge.StartHz, edge.StopHz, n, restoreHz, ct), ct);
            return new { ok = true, band = edge.Band, startHz = edge.StartHz, stopHz = edge.StopHz, points = n };
        }
    }

    private void Run(string band, long startHz, long stopHz, int n, long restoreHz, CancellationToken ct)
    {
        var pts = new List<SweepPoint>(n);
        bool keyed = false;
        try
        {
            if (!_tx.TrySetTun(true, out var err))
            { lock (_lock) { _phase = Phase.Failed; _error = $"could not key TUN: {err}"; } return; }
            keyed = true;
            Thread.Sleep(300);   // carrier + bridge settle

            for (int i = 0; i < n; i++)
            {
                if (ct.IsCancellationRequested) { lock (_lock) _phase = Phase.Aborted; return; }
                long hz = startHz + (stopHz - startHz) * i / (n - 1);
                _radio.SetVfo(hz);
                Thread.Sleep(60);                     // retune settle
                lock (_capture) _capture.Clear();
                _capturing = true;
                Thread.Sleep(120);                    // capture window
                _capturing = false;

                (float Fwd, float Swr)[] samples;
                lock (_capture) samples = _capture.ToArray();
                double? swr = null;
                if (samples.Length > 0)
                {
                    var valid = samples.Where(s => s.Fwd >= 2.0f).Select(s => (double)s.Swr).OrderBy(x => x).ToArray();
                    if (valid.Length > 0) swr = Math.Round(valid[valid.Length / 2], 2);
                }
                var pt = new SweepPoint(hz, swr);
                pts.Add(pt);
                lock (_lock) { _live.Add(pt); _done = i + 1; }
            }

            var good = pts.Where(p => p.Swr is not null).ToArray();
            long? minHz = null; double? minSwr = null; long? lo2 = null; long? hi2 = null;
            if (good.Length > 0)
            {
                var min = good.OrderBy(p => p.Swr).First();
                minHz = min.Hz; minSwr = min.Swr;
                var under = good.Where(p => p.Swr <= 2.0).ToArray();
                if (under.Length > 0) { lo2 = under.Min(p => p.Hz); hi2 = under.Max(p => p.Hz); }
            }
            var sweep = new Sweep(band, DateTimeOffset.UtcNow, pts, minHz, minSwr, lo2, hi2);
            lock (_lock)
            {
                _byBand.TryGetValue(band, out var pair);
                _byBand[band] = (sweep, pair.Current);
                _phase = good.Length == 0 ? Phase.Failed : Phase.Done;
                if (good.Length == 0)
                    _error = "no valid points — forward power stayed under the 2 W SWR-validity floor; raise TUN drive slightly";
            }
            _log.LogWarning("swr-sweep done: {Band} min {Min} @ {Hz}", band, minSwr, minHz);
        }
        catch (Exception ex)
        {
            lock (_lock) { _phase = Phase.Failed; _error = ex.Message; }
            _log.LogError(ex, "swr-sweep failed");
        }
        finally
        {
            _capturing = false;
            if (keyed) _tx.TrySetTun(false, out _);   // carrier drops NO MATTER WHAT
            try { _radio.SetVfo(restoreHz); } catch { /* best-effort restore */ }
        }
    }

    public object Abort()
    {
        _cts?.Cancel();
        _tx.TrySetTun(false, out _);
        _log.LogWarning("swr-sweep ABORT");
        return new { ok = true };
    }

    public void Dispose() => _cts?.Cancel();
}
