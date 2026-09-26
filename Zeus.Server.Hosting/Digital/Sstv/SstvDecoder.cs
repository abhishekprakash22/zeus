// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// SSTV decoder. Push 12 kHz audio in with Process(); it demodulates to a
// frequency track, hunts for a VIS header, and once one is found renders the
// picture line by line as the audio arrives, raising events the service turns
// into SSE frames. Pure and single-threaded: no clocks, no I/O, no allocation
// on the per-sample path — the tests drive it with synthetic audio.
//
// TIMING MODEL
// Every transmitted line n has a sync pulse whose start is modelled as
//     ts(n) = A + B·n            (samples, on the demodulated track)
// seeded from the VIS stop-bit edge and the mode table, then refined by a
// robust least-squares fit over the sync pulses actually found. B ≠ nominal
// is the sender's sound-card clock error — the classic SSTV slant — and the
// same ratio stretches every pixel, so correcting it is one number. Lines are
// rendered live with the fit as it stands; when the picture ends, every line
// is re-rendered with the final fit (the "straightened" image the operator
// keeps).
//
// FREQUENCY OFFSET
// Measured on the VIS leader (1900 Hz nominal) and subtracted from every
// reading, so a receiver mistuned by up to ±250 Hz still decodes with true
// greyscale.

namespace Zeus.Server.Hosting.Digital.Sstv;

public enum SstvEndReason
{
    /// <summary>Every line of the mode was received.</summary>
    Complete,
    /// <summary>Sync disappeared mid-picture (the sender stopped, or QSB).</summary>
    SignalLost,
    /// <summary>A new VIS header started another picture.</summary>
    Interrupted,
    /// <summary>Stopped by the operator (or the decoder was disabled).</summary>
    Stopped,
    /// <summary>A VIS-shaped burst with no picture behind it. Never shown.</summary>
    FalseStart,
}

public sealed class SstvDecoder
{
    public const int SampleRate = FmDemodulator.SampleRate;
    private const double SamplesPerMs = SampleRate / 1000.0;

    // VIS search runs on 10 ms bins of the frequency track.
    private const int BinSamples = 120;
    private const int BinHistory = 128;
    private const int LeaderBins = 24;
    private const double MaxOffsetHz = 250;
    private const double CoarseTolHz = 150;

    // Sync tracking.
    private const double SyncTolHz = 150;          // pulse MEAN vs 1200 Hz
    private const double MinSyncContrastHz = 250;  // (before − pulse) + (after − pulse)
    private const int SettledSyncs = 8;            // fit trusted from here on…
    private const double SettledTolMs = 2.0;       // …so syncs must land within this of it
    private const double SettledHzTol = 60;        // …and sound like the syncs before them
    private const double FitOutlierMs = 1.5;
    private const double MaxClockError = 0.02;
    private const int FalseStartLines = 12;
    private const int FalseStartMinSyncs = 4;
    private const int LostAfterLines = 25;

    // Idle history kept for the VIS edge refinement and the FSK-ID scan.
    // (12 s: also the look-back a sync-train start can anchor to.)
    private const int IdleKeepSamples = 12 * SampleRate;

    // Sync-train start (no VIS): pulses found on a 2 ms moving average of
    // the track while idle; a mode is recognised when enough of them sit at
    // multiples of its line period.
    private const int PulseAvg = 24;                 // 2 ms
    private const double PulseBelowHz = 1350;        // between sync 1200 and black 1500
    private const int MaxPulses = 64;
    private const int TrainMinPulses = 5;
    private const int TrainMaxLines = 16;
    private const double TrainMinCoverage = 0.6;

    // FSK ID (MMSSTV format, also QSSTV/YONIQ): after the picture, 1500 Hz
    // 300 ms, 2100 Hz 100 ms guard, a 1900 Hz start bit, then 6-bit chars LSB
    // first at 22 ms/bit (1 = 1900 Hz, 0 = 2100 Hz): 0x2A, C1..Cn, 0x01,
    // XSUM (= C1 ^ … ^ Cn). Char value = ASCII − 0x20.
    public const double FskBitMs = 22;
    public const double FskMarkHz = 1900, FskSpaceHz = 2100;
    public const int FskMaxChars = 16;
    private const double FskWaitMs = 3500;          // lead-in + 16 chars ≈ 3 s

    private readonly FmDemodulator _fm = new();
    private readonly Track _track = new();
    private long _n;                                   // absolute samples processed

    // VIS bins
    private readonly float[] _bins = new float[BinHistory];
    private long _binCount;
    private double _binAcc;
    private int _binFill;
    private long _visHoldUntilBin;

    private Image? _img;

    // Sync-train hunt state (idle only).
    private readonly float[] _pAvgRing = new float[PulseAvg];
    private int _pAvgPos;
    private double _pAvgSum;
    private bool _inPulse;
    private long _pulseStart;
    private readonly long[] _pStart = new long[MaxPulses];
    private readonly int[] _pLen = new int[MaxPulses];
    private readonly float[] _pHz = new float[MaxPulses];
    private int _pCount;                               // total pulses recorded

    // FSK-ID scan armed by a completed picture.
    private SstvImage? _fskFor;
    private long _fskFrom, _fskTo, _fskScanAt;

    public SstvDecoder() => ClearPulses();

    /// <summary>The picture being received, or null while hunting for VIS.</summary>
    public SstvImage? Current => _img?.Public;

    public event Action<SstvImage>? ImageStarted;
    /// <summary>(image, firstRow, rowCount) — rows are final for live display
    /// but will be redrawn by the end-of-picture re-render.</summary>
    public event Action<SstvImage, int, int>? RowsDecoded;
    public event Action<SstvImage, SstvEndReason>? ImageEnded;
    /// <summary>An FSK ID (sender's callsign) followed a finished picture.</summary>
    public event Action<SstvImage, string>? CallsignDecoded;
    /// <summary>An FSK burst (guard + start bit) followed the picture but did
    /// not read as a valid ID — with why, for the log. Tells "the sender sent
    /// none" apart from "we failed to read it".</summary>
    public event Action<SstvImage, string>? FskIdUnreadable;

    public long SamplesProcessed => _n;

    public void Reset()
    {
        _fm.Reset();
        _track.Clear(0);
        _n = 0;
        _binCount = 0; _binAcc = 0; _binFill = 0; _visHoldUntilBin = 0;
        _img = null;
        _fskFor = null;
        ClearPulses();
    }

    /// <summary>End the picture in progress (if any) as <see cref="SstvEndReason.Stopped"/>.</summary>
    public void Stop()
    {
        if (_img is not null) Finish(SstvEndReason.Stopped);
    }

    public void Process(ReadOnlySpan<float> audio12k)
    {
        for (int i = 0; i < audio12k.Length; i++)
        {
            float f = _fm.Process(audio12k[i]);
            _track.Append(f);
            _n++;

            float fc = Math.Clamp(f, 900f, 2600f);
            if (_img is null) HuntPulse(fc);

            _binAcc += fc;
            if (++_binFill == BinSamples)
            {
                _bins[_binCount % BinHistory] = (float)(_binAcc / BinSamples);
                _binCount++;
                _binAcc = 0; _binFill = 0;
                if (_binCount >= _visHoldUntilBin) TryVis();
            }
        }

        // Advance first: a picture that ends inside this block arms the FSK
        // scan, which may already have all the audio it needs.
        if (_img is not null) Advance();
        if (_fskFor is not null && _n >= _fskScanAt) ScanFskId();
        // Idle trim — never while a picture or a pending FSK scan needs the track.
        if (_img is null && _fskFor is null && _track.Length > 2 * IdleKeepSamples)
            _track.TrimBefore(_n - IdleKeepSamples);
    }

    // ---- VIS ----------------------------------------------------------------

    private float Bin(long k) => _bins[k % BinHistory];

    /// <summary>
    /// VIS hunt, run on every 10 ms bin. A cheap COARSE gate on the bins (a
    /// leader-like stretch near 1900 Hz, start/stop-bit bins near 1200 Hz)
    /// admits candidates to the FINE read on the track itself: each bit is the
    /// MEDIAN of its central 20 ms (the FM demodulator's noise clicks are
    /// outliers a mean can't shrug off), the bit grid is aligned to the
    /// sample, and the tuning offset is re-measured on the 1900 Hz leader
    /// right before the start bit. Parity and a known mode code reject what
    /// noise or picture content could still slip through.
    ///
    /// While a picture that has proved itself real is being received the
    /// fine read runs STRICT, so picture content can't interrupt it; an idle
    /// decoder (or one inside a likely false start) listens leniently.
    /// </summary>
    private void TryVis()
    {
        // Centre bin of the stop bit, taken 40 ms back so the fine read's
        // ±15 ms alignment search has the whole stop bit in hand.
        long c = _binCount - 4;
        long leaderEnd = c - 31;
        long leaderStart = leaderEnd - LeaderBins + 1;
        if (leaderStart < 0 || _binCount - leaderStart > BinHistory) return;

        // Coarse leader: median near 1900 Hz, most bins close to it.
        Span<float> lead = stackalloc float[LeaderBins];
        for (int i = 0; i < LeaderBins; i++) lead[i] = Bin(leaderStart + i);
        lead.Sort();
        double median = lead[LeaderBins / 2];
        double offset = median - SstvModes.LeaderHz;
        if (Math.Abs(offset) > MaxOffsetHz) return;
        int good = 0;
        for (int i = 0; i < LeaderBins; i++) if (Math.Abs(lead[i] - median) < CoarseTolHz) good++;
        if (good < LeaderBins * 6 / 10) return;

        // Coarse start / stop bits.
        if (Math.Abs(Bin(c - 27) - offset - SstvModes.SyncHz) > CoarseTolHz + 50) return;
        if (Math.Abs(Bin(c) - offset - SstvModes.SyncHz) > CoarseTolHz + 50) return;

        bool strict = _img is not null && _img.Syncs.Count >= FalseStartMinSyncs;
        long est = (c - 27) * BinSamples + BinSamples / 2 - (long)(15 * SamplesPerMs);
        if (ReadVis(est, offset, strict) is not { } vis) return;

        // Don't re-detect this same header on the next bins.
        _visHoldUntilBin = _binCount + 60;

        if (_img is not null) Finish(SstvEndReason.Interrupted);
        Begin(vis.Mode, vis.VisEnd, vis.Offset);
    }

    /// <summary>Fine VIS read around the estimated start-bit edge
    /// <paramref name="est"/> (±15 ms). Null unless it is a valid header.</summary>
    private (SstvMode Mode, long VisEnd, double Offset)? ReadVis(long est, double coarseOffset, bool strict)
    {
        int bit = (int)(SstvEncoder.VisBitMs * SamplesPerMs);         // 360
        int core = (int)(20 * SamplesPerMs), margin = (bit - core) / 2;
        int reach = (int)(15 * SamplesPerMs), step = (int)(0.5 * SamplesPerMs);
        int leadLen = (int)(200 * SamplesPerMs), leadGap = (int)(10 * SamplesPerMs);
        if (est - reach - leadGap - leadLen < _track.Base
            || est + reach + 9 * bit + margin + core > _track.End) return null;

        Span<double> d = stackalloc double[10], bestD = stackalloc double[10];
        double bestScore = double.MinValue;
        long bestS = est;
        for (long S = est - reach; S <= est + reach; S += step)
        {
            // Distance of each bit from the 1200 Hz decision line.
            for (int j = 0; j < 10; j++)
                d[j] = Median(S + j * bit + margin, core) - coarseOffset - SstvModes.SyncHz;
            // Start and stop (both 1200 Hz) should agree and sit near the
            // line; data bits well off their midpoint.
            double mid = 0.5 * (d[0] + d[9]);
            double score = -Math.Abs(d[0] - d[9]) - 0.5 * Math.Abs(mid);
            for (int j = 1; j <= 8; j++) score += Math.Min(Math.Abs(d[j] - mid), 100);
            if (score > bestScore) { bestScore = score; bestS = S; d.CopyTo(bestD); }
        }

        // Tuning offset from the leader right before the start bit.
        double offset = Median(bestS - leadGap - leadLen, leadLen) - SstvModes.LeaderHz;
        if (Math.Abs(offset) > MaxOffsetHz || Math.Abs(offset - coarseOffset) > 80) return null;
        for (int j = 0; j < 10; j++) bestD[j] += coarseOffset - offset;

        // Decide each data bit against the start/stop bits' own reading: they
        // ARE 1200 Hz, so the low-SNR pull of the median moves the reference
        // and the data bits together.
        double endTol = strict ? 60 : 120, minData = strict ? 50 : 15;
        double refHz = 0.5 * (bestD[0] + bestD[9]);
        if (Math.Abs(refHz) > endTol || Math.Abs(bestD[0] - bestD[9]) > endTol) return null;
        int code = 0, ones = 0;
        for (int j = 1; j <= 8; j++)
        {
            double v = bestD[j] - refHz;
            if (Math.Abs(v) < minData || Math.Abs(v) > 250) return null;
            if (v < 0) { ones++; if (j <= 7) code |= 1 << (j - 1); }        // 1100 Hz = 1
        }
        if ((ones & 1) != 0) return null;                                    // even parity
        var mode = SstvModes.ByVis(code);
        if (mode is null) return null;
        return (mode, bestS + 10 * bit, offset);
    }

    private float[] _medianScratch = new float[4096];

    /// <summary>Median of the track over [start, start+count), click-clamped.
    /// At low SNR the FM demodulator's noise drags it toward the band centre;
    /// ReadVis cancels that by deciding against the start/stop bits.</summary>
    private double Median(long start, int count)
    {
        if (_medianScratch.Length < count) _medianScratch = new float[count];
        var span = _medianScratch.AsSpan(0, count);
        for (int i = 0; i < count; i++) span[i] = Math.Clamp(_track[start + i], 900f, 2600f);
        span.Sort();
        return count % 2 == 1 ? span[count / 2] : 0.5 * (span[count / 2 - 1] + span[count / 2]);
    }

    // ---- FSK ID -------------------------------------------------------------

    private void ScanFskId()
    {
        var img = _fskFor!;
        _fskFor = null;
        var (call, why) = FindFskId(_track, Math.Max(_track.Base, _fskFrom),
            Math.Min(_track.End, _fskTo), img.OffsetHz);
        if (call is not null) CallsignDecoded?.Invoke(img, call);
        else if (why is not null) FskIdUnreadable?.Invoke(img, why);
    }

    /// <summary>Find and read an FSK ID in [from, to) of the track. Returns
    /// the callsign, or why the first burst that looked like one didn't read
    /// (both null: no burst at all).</summary>
    private static (string? Call, string? Why) FindFskId(Track track, long from, long to, double offset)
    {
        string? firstWhy = null;
        double bit = FskBitMs * SamplesPerMs;
        long guard = (long)(50 * SamplesPerMs);
        for (long t = from + guard; t + 10 * bit < to; t += 6)
        {
            if (Math.Abs(track.Mean(t - guard, t) - offset - FskSpaceHz) > 60) continue;
            if (Math.Abs(track.Mean(t + 0.25 * bit, t + 0.75 * bit) - offset - FskMarkHz) > 60) continue;

            // Refine the 2100 → 1900 edge of the start bit to the sample.
            long edge = t;
            double best = double.MinValue;
            long w = (long)(5 * SamplesPerMs);
            for (long e = t - 12; e <= t + 24; e++)
            {
                double score = track.Mean(e - w, e) - track.Mean(e, e + w);
                if (score > best) { best = score; edge = e; }
            }
            var (call, why) = ReadFskId(track, edge, to, offset);
            if (call is not null) return (call, null);
            firstWhy ??= why;
            t = edge + (long)bit;
        }
        return (null, firstWhy);
    }

    private static (string? Call, string? Why) ReadFskId(Track track, long startBitEdge, long to, double offset)
    {
        double bit = FskBitMs * SamplesPerMs;
        int k = 1;                                    // bit 0 is the start bit
        int? Char()
        {
            int v = 0;
            for (int i = 0; i < 6; i++, k++)
            {
                double c = startBitEdge + (k + 0.5) * bit;
                if (c + 0.3 * bit > to) return null;
                double hz = track.Mean(c - 0.3 * bit, c + 0.3 * bit) - offset;
                if (hz < 2000) v |= 1 << i;
            }
            return v;
        }

        int? head = Char();
        if (head != 0x2A) return (null, $"header 0x{head ?? -1:X2}, not 0x2A");
        var sb = new System.Text.StringBuilder();
        int xsum = 0;
        while (true)
        {
            int? c = Char();
            if (c is null) return (null, $"ran out of audio after '{sb}'");
            if (c == 0x01) break;
            if (sb.Length == FskMaxChars) return (null, $"no terminator after '{sb}'");
            xsum ^= c.Value;
            sb.Append((char)(c.Value + 0x20));
        }
        if (sb.Length == 0) return (null, "empty ID");
        int? sum = Char();
        if (sum != xsum) return (null, $"checksum 0x{sum ?? -1:X2} ≠ 0x{xsum:X2} for '{sb}'");
        string call = sb.ToString().Trim();
        return call.Length == 0 ? (null, "blank ID") : (call, null);
    }

    // ---- picture ------------------------------------------------------------

    private void Begin(SstvMode mode, long visEnd, double offset, bool viaSync = false)
    {
        if (_fskFor is not null) ScanFskId();       // before the trim discards it
        _track.TrimBefore(visEnd - (long)(50 * SamplesPerMs));
        ClearPulses();
        var img = new Image(mode, visEnd, offset);
        img.Public.ViaSync = viaSync;
        _img = img;
        ImageStarted?.Invoke(img.Public);
    }

    // ---- sync-train start (no VIS) -------------------------------------------
    //
    // A picture whose VIS was lost to a fade — or one tuned into halfway — is
    // still a train of sync pulses at the mode's line period. Recognise the
    // train and start there, the way MMSSTV's sync auto-start does; the
    // picture then fills from the top with whatever arrives. Pulse length
    // (4.9 / 9 / 20 ms) and period separate the modes; a coverage floor
    // stops Robot 36 (a pulse every 150 ms) claiming a Robot 72 train (every
    // 300 ms) and vice versa.

    /// <summary>Forget the pulse history and restart the moving average at a
    /// neutral mid-band value — a zero-filled average reads as "below 1350 Hz"
    /// and would open a phantom pulse before the start of the track.</summary>
    private void ClearPulses()
    {
        _pCount = 0;
        _inPulse = false;
        Array.Fill(_pAvgRing, (float)SstvModes.LeaderHz);
        _pAvgSum = SstvModes.LeaderHz * PulseAvg;
        _pAvgPos = 0;
    }

    /// <summary>Per-sample, idle only: track runs of the 2 ms moving average
    /// below 1350 Hz and record each as a pulse.</summary>
    private void HuntPulse(float fc)
    {
        _pAvgSum += fc - _pAvgRing[_pAvgPos];
        _pAvgRing[_pAvgPos] = fc;
        if (++_pAvgPos == PulseAvg) _pAvgPos = 0;
        double avg = _pAvgSum / PulseAvg;
        // The average lags by half its window: edges are placed PulseAvg/2 back.
        if (!_inPulse)
        {
            if (avg < PulseBelowHz)
            {
                _inPulse = true;
                _pulseStart = _n - PulseAvg / 2;
            }
            return;
        }
        if (avg < PulseBelowHz) return;
        _inPulse = false;
        int len = (int)(_n - PulseAvg / 2 - _pulseStart);
        if (len < 3 * SamplesPerMs || len > 25 * SamplesPerMs) return;
        if (_pulseStart + len / 4 < _track.Base) return;       // history already trimmed
        int k = _pCount % MaxPulses;
        // Tone from the pulse's central half only — the averaged run's edges
        // are smeared into the porch and would bias the offset upward.
        _pStart[k] = _pulseStart; _pLen[k] = len;
        _pHz[k] = (float)Median(_pulseStart + len / 4, Math.Max(1, len / 2));
        _pCount++;
        TrySyncTrain();
    }

    private void TrySyncTrain()
    {
        int last = (_pCount - 1) % MaxPulses;
        long pk = _pStart[last];
        int lenK = _pLen[last];
        int avail = Math.Min(_pCount, MaxPulses);

        SstvMode? bestMode = null;
        int bestCount = 0;
        long bestAnchor = 0;
        Span<bool> seen = stackalloc bool[TrainMaxLines + 1];
        foreach (var m in SstvModes.All)
        {
            double period = TrainPeriodMs(m) * SamplesPerMs, syncLen = m.SyncMs * SamplesPerMs;
            if (Math.Abs(lenK - syncLen) > 0.35 * syncLen + SamplesPerMs) continue;
            seen.Clear();
            int count = 1, maxN = 0;
            long anchor = pk;
            for (int i = 1; i < avail; i++)
            {
                int j = (_pCount - 1 - i) % MaxPulses;
                if (Math.Abs(_pLen[j] - syncLen) > 0.35 * syncLen + SamplesPerMs) continue;
                long delta = pk - _pStart[j];
                int n = (int)Math.Round(delta / period);
                if (n < 1 || n > TrainMaxLines || seen[n]) continue;
                if (Math.Abs(delta - n * period) > 0.005 * delta + 1.5 * SamplesPerMs) continue;
                seen[n] = true;
                count++;
                maxN = Math.Max(maxN, n);
                anchor = Math.Min(anchor, _pStart[j]);
            }
            if (count < TrainMinPulses || count < TrainMinCoverage * (maxN + 1)) continue;
            if (count > bestCount) { bestCount = count; bestMode = m; bestAnchor = anchor; }
        }
        if (bestMode is null || bestAnchor - 50 * SamplesPerMs < _track.Base) return;

        // Offset from the pulses themselves (median of their mean tone).
        Span<float> hz = stackalloc float[avail];
        for (int i = 0; i < avail; i++) hz[i] = _pHz[i];
        hz.Sort();
        double offset = Math.Clamp(hz[avail / 2] - SstvModes.SyncHz, -MaxOffsetHz, MaxOffsetHz);

        long anchorSync = bestAnchor;
        if (bestMode == SstvModes.R36 && IsRobot36OddLine(anchorSync, offset))
            anchorSync += (long)(150 * SamplesPerMs);         // start on an even (R-Y) line

        // Place "VIS end" so line 0's sync lands on the anchor pulse.
        long visEnd = anchorSync - (long)Math.Round((bestMode.LeadInMs + bestMode.SyncOffsetMs) * SamplesPerMs);
        Begin(bestMode, visEnd, offset, viaSync: true);
    }

    /// <summary>Robot 36 carries R-Y on even lines behind a 1500 Hz separator
    /// and B-Y on odd ones behind 2300 Hz; read the separator after this sync.</summary>
    private bool IsRobot36OddLine(long syncStart, double offset)
    {
        long sep = syncStart + (long)((9 + 3 + 88 + 0.75) * SamplesPerMs);
        int len = (int)(3 * SamplesPerMs);
        if (sep < _track.Base || sep + len > _track.End) return false;
        return Median(sep, len) - offset > SstvModes.LeaderHz;      // 2300 vs 1500, split at 1900
    }

    /// <summary>Spacing between consecutive sync pulses (Robot 36: two per
    /// modelled double line).</summary>
    private static double TrainPeriodMs(SstvMode m) => m == SstvModes.R36 ? m.LineMs / 2 : m.LineMs;

    private void Advance()
    {
        var img = _img!;
        while (_img == img && img.NextLine < img.Mode.TxLines)
        {
            int n = img.NextLine;
            double predicted = img.SyncAt(n);
            double win = img.SyncWindow;
            double lineEnd = img.LineStart(n) + img.Mode.LineMs * img.MsToSamples;
            if (Math.Max(predicted + win + img.SyncLen, lineEnd) + 2 > _n) return;  // need more audio

            // Once the line fit has settled, a pulse must also land where the
            // fit says: noise after the sender stops can still produce the
            // odd pulse-shaped stretch somewhere in the ±win window, and each
            // one would push "last sync" on and keep a dead picture alive.
            // Its tone must also match the pulses already accepted (their
            // median — at low SNR the demodulator pulls every pulse's mean up
            // by the same amount, so a fixed band would be too loose there
            // and too tight elsewhere).
            if (FindSync(_track, img, predicted, win) is var (ts, pulseHz)
                && (img.Syncs.Count < SettledSyncs
                    || (Math.Abs(ts - predicted) <= SettledTolMs * SamplesPerMs
                        && Math.Abs(pulseHz - img.SyncHzMedian()) <= SettledHzTol)))
            {
                img.AddSyncHz(pulseHz);
                img.Syncs.Add((n, ts));
                img.LastSyncLine = n;
                img.Refit();
            }

            RenderLine(_track, img, n);
            img.NextLine++;
            img.Public.RowsDone = img.NextLine * img.Mode.RowsPerLine;
            RowsDecoded?.Invoke(img.Public, n * img.Mode.RowsPerLine, img.Mode.RowsPerLine);

            if (img.NextLine == FalseStartLines && img.Syncs.Count < FalseStartMinSyncs)
            {
                Finish(SstvEndReason.FalseStart);
                return;
            }
            if (img.NextLine - 1 - img.LastSyncLine >= LostAfterLines)
            {
                Finish(SstvEndReason.SignalLost);
                return;
            }
        }
        if (_img == img && img.NextLine >= img.Mode.TxLines) Finish(SstvEndReason.Complete);
    }

    /// <summary>Best sync-pulse start within ±win of <paramref name="predicted"/>,
    /// or null if nothing pulse-shaped is there.
    ///
    /// Scored by CONTRAST, not by counting samples near 1200 Hz: a sync pulse
    /// is a stretch whose mean frequency sits near 1200 Hz with brighter-than-
    /// sync content on both sides (porch and picture never go below 1500 Hz).
    /// Means over the pulse and a ≤3 ms window each side average the FM
    /// demodulator's noise and clicks down, where the old per-sample ±110 Hz
    /// count collapsed below ~10 dB SNR and lost the picture.</summary>
    private static (double T, double Hz)? FindSync(Track track, Image img, double predicted, double win)
    {
        int len = (int)Math.Round(img.SyncLen);
        int side = Math.Min(len, (int)(3 * SamplesPerMs));
        // Never before the VIS stop-bit edge: Martin/PD line 0 starts with its
        // sync pulse flush against the (also 1200 Hz) stop bit.
        long lo = Math.Max(Math.Max(track.Base + side, img.VisEnd), (long)Math.Floor(predicted - win));
        long hi = Math.Min(track.End - len - side, (long)Math.Ceiling(predicted + win));
        if (hi <= lo) return null;

        // Prefix sums of the offset-corrected, click-clamped track over the
        // whole search span, so every window mean is O(1).
        long a0 = lo - side;
        int n = (int)(hi + len + side - a0);
        var ps = new double[n + 1];
        for (int i = 0; i < n; i++)
            ps[i + 1] = ps[i] + Math.Clamp(track[a0 + i] - img.Offset, 900.0, 2600.0);
        double Mean(long start, int count) => (ps[start - a0 + count] - ps[start - a0]) / count;

        double bestScore = double.MinValue;
        long bestT = -1;
        for (long t = lo; t <= hi; t++)
        {
            double inside = Mean(t, len);
            if (Math.Abs(inside - SstvModes.SyncHz) > SyncTolHz) continue;
            double score = (Mean(t - side, side) - inside) + (Mean(t + len, side) - inside);
            if (score > bestScore) { bestScore = score; bestT = t; }
        }
        if (bestT < 0 || bestScore < MinSyncContrastHz) return null;
        double pulseHz = Mean(bestT, len);

        // The contrast peak is biased by whatever picture sits beside the
        // pulse (brighter content pulls it that way), and a bias that changes
        // down the picture tilts the whole line fit. Locate precisely by the
        // pulse's own centroid instead: each sample weighs by how sync-like
        // it is (1 at ≤1250 Hz, 0 at ≥1450 Hz — porch and picture never go
        // below 1500), over a window symmetric about the coarse centre and
        // twice the pulse long, so picture content contributes nothing.
        double c = bestT + len / 2.0;
        long w0 = Math.Max(track.Base, (long)(c - len)), w1 = Math.Min(track.End - 1, (long)(c + len));
        double sw = 0, st = 0;
        for (long t = w0; t <= w1; t++)
        {
            double f = track[t] - img.Offset;
            double wt = Math.Clamp((SstvModes.SyncHz + 250 - f) / 200.0, 0, 1);
            sw += wt; st += wt * (t + 0.5);
        }
        return (sw > 0 ? st / sw - len / 2.0 : bestT, pulseHz);
    }

    private static void RenderLine(Track track, Image img, int n)
    {
        var m = img.Mode;
        double lineStart = img.LineStart(n);
        int w = m.Width;
        Span<byte> scratch = img.Scratch;             // 4 planes × width (PD) / 3 (RGB)

        for (int s = 0; s < m.Scans.Length; s++)
        {
            double start = lineStart + m.Scans[s].StartMs * img.MsToSamples;
            double px = m.PixelOf(m.Scans[s]) * img.MsToSamples;
            int plane = (int)m.Scans[s].Channel;
            for (int x = 0; x < w; x++)
            {
                double a = start + x * px;
                double hz = track.Mean(a, a + px) - img.Offset;
                scratch[plane * w + x] = SstvModes.HzToLuma(hz);
            }
        }

        byte[] rgb = img.Public.Rgb;
        if (m.Color == SstvColor.Rgb)
        {
            int row = n * w * 3;
            for (int x = 0; x < w; x++)
            {
                rgb[row + 3 * x] = scratch[(int)SstvChannel.R * w + x];
                rgb[row + 3 * x + 1] = scratch[(int)SstvChannel.G * w + x];
                rgb[row + 3 * x + 2] = scratch[(int)SstvChannel.B * w + x];
            }
        }
        else if (m.RowsPerLine == 1)
        {
            int row = n * w * 3;
            for (int x = 0; x < w; x++)
                SstvColorSpace.ToRgb(scratch[(int)SstvChannel.Y0 * w + x],
                    scratch[(int)SstvChannel.Cr * w + x], scratch[(int)SstvChannel.Cb * w + x],
                    out rgb[row + 3 * x], out rgb[row + 3 * x + 1], out rgb[row + 3 * x + 2]);
        }
        else
        {
            int row0 = 2 * n * w * 3, row1 = row0 + w * 3;
            for (int x = 0; x < w; x++)
            {
                byte cr = scratch[(int)SstvChannel.Cr * w + x], cb = scratch[(int)SstvChannel.Cb * w + x];
                SstvColorSpace.ToRgb(scratch[(int)SstvChannel.Y0 * w + x], cr, cb,
                    out rgb[row0 + 3 * x], out rgb[row0 + 3 * x + 1], out rgb[row0 + 3 * x + 2]);
                SstvColorSpace.ToRgb(scratch[(int)SstvChannel.Y1 * w + x], cr, cb,
                    out rgb[row1 + 3 * x], out rgb[row1 + 3 * x + 1], out rgb[row1 + 3 * x + 2]);
            }
        }
    }

    private void Finish(SstvEndReason reason)
    {
        var img = _img!;
        _img = null;
        if (reason != SstvEndReason.FalseStart)
        {
            // Lines after the last sync are noise once the sender has gone.
            int lines = reason == SstvEndReason.SignalLost ? img.LastSyncLine + 1 : img.NextLine;
            FinalRender(_track, img, lines);
            img.Public.Recording = new SstvRecording(_track.Copy(), _track.Base, img.VisEnd);
            if (reason is SstvEndReason.Complete or SstvEndReason.SignalLost)
            {
                // Relative to where the picture ENDED on the track — the last
                // line that had a sync — not to how much audio this Process()
                // call carried. SignalLost matters as much as Complete: a
                // picture started from the sync train can never "complete" (it
                // began mid-transmission), and QSB turns many others into
                // SignalLost; the sender's ID follows either way, and the
                // track still holds it (nothing is trimmed while a picture
                // runs). A few lines of slack cover a fade that ate the last
                // lines before the ID.
                long pictureEnd = (long)img.LineStart(lines);
                double slack = (FskWaitMs + 3 * img.Mode.LineMs) * SamplesPerMs;
                _fskFor = img.Public;
                _fskFrom = pictureEnd - (long)(300 * SamplesPerMs);
                _fskTo = pictureEnd + (long)slack;
                _fskScanAt = pictureEnd + (long)(FskWaitMs * SamplesPerMs);
            }
        }
        img.Public.EndReason = reason;
        ClearPulses();
        if (_fskFor is null) _track.TrimBefore(_n - IdleKeepSamples);
        ImageEnded?.Invoke(img.Public, reason);
    }

    private static void FinalRender(Track track, Image img, int lines)
    {
        img.Refit();
        img.Adjust();
        for (int n = 0; n < lines; n++) RenderLine(track, img, n);
        int keep = lines * img.Mode.RowsPerLine * img.Mode.Width * 3;
        Array.Clear(img.Public.Rgb, keep, img.Public.Rgb.Length - keep);
        img.Public.RowsDone = lines * img.Mode.RowsPerLine;
        img.Public.ClockError = img.ClockScale - 1;
    }

    // ---- re-render ----------------------------------------------------------

    /// <summary>Largest manual slant the adjust path accepts (±2 %).</summary>
    public const double MaxSlantPpm = 20_000;

    /// <summary>
    /// Redraw a finished picture from its recorded frequency track: the sync
    /// search and line fit run again (in <paramref name="mode"/> if given —
    /// "decode as…" for a missed or mis-read VIS), then the operator's manual
    /// corrections are applied on top of the fit: <paramref name="slantPpm"/>
    /// scales the line period, <paramref name="shiftPx"/> moves the picture
    /// sideways. Parameters are absolute, not cumulative, so the same call
    /// always yields the same picture. Returns a new image with the same id.
    /// </summary>
    public static SstvImage Rerender(
        SstvImage source, SstvMode? mode = null, double slantPpm = 0, double shiftPx = 0)
    {
        var rec = source.Recording
                  ?? throw new InvalidOperationException("picture has no recording to re-render");
        var m = mode ?? source.Mode;
        var track = Track.Wrap(rec.Samples, rec.Base);
        var img = new Image(m, rec.VisEnd, source.OffsetHz, source.Id)
        {
            SlantPpm = Math.Clamp(slantPpm, -MaxSlantPpm, MaxSlantPpm),
            ShiftPx = Math.Clamp(shiftPx, -m.Width, m.Width),
        };

        bool lost = false;
        for (int n = 0; n < m.TxLines; n++)
        {
            double predicted = img.SyncAt(n);
            double lineEnd = img.LineStart(n) + m.LineMs * img.MsToSamples;
            if (Math.Max(predicted + img.SyncWindow + img.SyncLen, lineEnd) + 2 > track.End) break;
            if (FindSync(track, img, predicted, img.SyncWindow) is var (ts, pulseHz)
                && (img.Syncs.Count < SettledSyncs
                    || (Math.Abs(ts - predicted) <= SettledTolMs * SamplesPerMs
                        && Math.Abs(pulseHz - img.SyncHzMedian()) <= SettledHzTol)))
            {
                img.AddSyncHz(pulseHz);
                img.Syncs.Add((n, ts));
                img.LastSyncLine = n;
                img.Refit();
            }
            img.NextLine = n + 1;
            if (n - img.LastSyncLine >= LostAfterLines) { lost = true; break; }
        }

        int lines = lost ? img.LastSyncLine + 1 : img.NextLine;
        FinalRender(track, img, Math.Max(0, lines));
        img.Public.Recording = rec;
        img.Public.EndReason = source.EndReason;
        return img.Public;
    }

    // ---- per-picture state --------------------------------------------------

    private sealed class Image
    {
        public readonly SstvMode Mode;
        public readonly SstvImage Public;
        public readonly double Offset;
        public readonly long VisEnd;
        public readonly double NominalA, NominalB;
        public readonly double SyncLen;
        public readonly double SyncWindow;
        public readonly byte[] Scratch;
        public readonly List<(int Line, double T)> Syncs = new();
        private readonly float[] _syncHz = new float[32];
        private int _syncHzCount;

        public void AddSyncHz(double hz) => _syncHz[_syncHzCount++ % _syncHz.Length] = (float)hz;

        /// <summary>Median tone of the recent accepted sync pulses.</summary>
        public double SyncHzMedian()
        {
            int n = Math.Min(_syncHzCount, _syncHz.Length);
            if (n == 0) return SstvModes.SyncHz;
            Span<float> tmp = stackalloc float[n];
            _syncHz.AsSpan(0, n).CopyTo(tmp);
            tmp.Sort();
            return tmp[n / 2];
        }
        public int NextLine;
        public int LastSyncLine = -1;
        public double A, B;

        /// <summary>Manual corrections applied after the fit (re-render only).</summary>
        public double SlantPpm, ShiftPx;

        public Image(SstvMode mode, long visEnd, double offset, int? id = null)
        {
            Mode = mode;
            Offset = offset;
            VisEnd = visEnd;
            NominalA = visEnd + (mode.LeadInMs + mode.SyncOffsetMs) * SamplesPerMs;
            NominalB = mode.LineMs * SamplesPerMs;
            A = NominalA; B = NominalB;
            SyncLen = mode.SyncMs * SamplesPerMs;
            SyncWindow = Math.Max(4.0, 0.02 * mode.LineMs) * SamplesPerMs;
            Scratch = new byte[7 * mode.Width];
            Public = new SstvImage(mode, offset, id);
        }

        public double ClockScale => B / NominalB;

        /// <summary>Apply the manual slant/shift on top of the fitted line.</summary>
        public void Adjust()
        {
            if (SlantPpm == 0 && ShiftPx == 0) return;
            // Keep the middle line where the fit put it, so slant pivots on
            // the picture's centre instead of swinging the bottom half away.
            double mid = Mode.TxLines / 2.0;
            double pivot = A + B * mid;
            B *= 1 + SlantPpm * 1e-6;
            A = pivot - B * mid;
            A -= ShiftPx * Mode.PixelOf(Mode.Scans[0]) * MsToSamples;
            Public.SlantPpm = SlantPpm;
            Public.ShiftPx = ShiftPx;
        }
        public double MsToSamples => SamplesPerMs * ClockScale;
        public double SyncAt(int n) => A + B * n;
        public double LineStart(int n) => SyncAt(n) - Mode.SyncOffsetMs * MsToSamples;

        /// <summary>Robust line fit of sync start vs line index. Few points →
        /// keep the nominal slope and fit only the intercept.</summary>
        public void Refit()
        {
            int cnt = Syncs.Count;
            if (cnt == 0) { A = NominalA; B = NominalB; return; }

            var keep = new bool[cnt];
            Array.Fill(keep, true);
            double a = A, b = B;
            for (int pass = 0; pass < 3; pass++)
            {
                if (!Fit(keep, out a, out b)) break;
                double tol = FitOutlierMs * SamplesPerMs;
                var next = new bool[cnt];
                int kept = 0;
                for (int i = 0; i < cnt; i++)
                    if (next[i] = Math.Abs(Syncs[i].T - (a + b * Syncs[i].Line)) < tol) kept++;
                // Too early to tell good from bad (e.g. two points that
                // disagree): keep what we had rather than fit nothing.
                if (kept == 0 || next.AsSpan().SequenceEqual(keep)) break;
                keep = next;
            }
            if (Fit(keep, out a, out b)) { A = a; B = b; }
        }

        private bool Fit(bool[] keep, out double a, out double b)
        {
            int k = 0; double sx = 0, sy = 0; int minL = int.MaxValue, maxL = int.MinValue;
            for (int i = 0; i < Syncs.Count; i++)
            {
                if (!keep[i]) continue;
                k++; sx += Syncs[i].Line; sy += Syncs[i].T;
                minL = Math.Min(minL, Syncs[i].Line); maxL = Math.Max(maxL, Syncs[i].Line);
            }
            a = A; b = B;
            if (k == 0) return false;
            double mx = sx / k, my = sy / k;

            b = NominalB;
            if (k >= 3 && maxL - minL >= 4)
            {
                double sxx = 0, sxy = 0;
                for (int i = 0; i < Syncs.Count; i++)
                {
                    if (!keep[i]) continue;
                    double dx = Syncs[i].Line - mx;
                    sxx += dx * dx; sxy += dx * (Syncs[i].T - my);
                }
                b = Math.Clamp(sxy / sxx, NominalB * (1 - MaxClockError), NominalB * (1 + MaxClockError));
            }
            a = my - b * mx;
            return true;
        }
    }

    // ---- growable frequency track ------------------------------------------

    private sealed class Track
    {
        private float[] _buf = new float[1 << 16];
        private int _len;
        public long Base { get; private set; }
        public int Length => _len;

        public float this[long abs] => _buf[abs - Base];
        public long End => Base + _len;

        public static Track Wrap(float[] samples, long baseAbs) =>
            new() { _buf = samples, _len = samples.Length, Base = baseAbs };

        public float[] Copy() => _buf.AsSpan(0, _len).ToArray();

        public void Append(float v)
        {
            if (_len == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
            _buf[_len++] = v;
        }

        public void Clear(long newBase) { _len = 0; Base = newBase; }

        public void TrimBefore(long abs)
        {
            long drop = abs - Base;
            if (drop <= 0) return;
            if (drop >= _len) { Base += _len; _len = 0; return; }
            Array.Copy(_buf, drop, _buf, 0, _len - drop);
            _len -= (int)drop;
            Base = abs;
        }

        /// <summary>Mean over [a, b) treating sample i as constant on [i, i+1);
        /// fractional ends weighted. Out-of-range parts are clipped.</summary>
        public double Mean(double a, double b)
        {
            double lo = Math.Max(a, Base), hi = Math.Min(b, Base + _len);
            if (hi <= lo) return 0;
            double sum = 0;
            long i0 = (long)Math.Floor(lo), i1 = (long)Math.Floor(hi);
            if (i0 == i1) return _buf[i0 - Base];
            sum += _buf[i0 - Base] * (i0 + 1 - lo);
            for (long i = i0 + 1; i < i1; i++) sum += _buf[i - Base];
            if (i1 < Base + _len) sum += _buf[i1 - Base] * (hi - i1);
            return sum / (hi - lo);
        }
    }
}

/// <summary>A picture as the outside world sees it: pixels plus metadata.
/// <see cref="Rgb"/> is written in place as lines arrive.</summary>
public sealed class SstvImage
{
    private static int _nextId;

    /// <summary>Allocate an id from the same sequence pictures use, for
    /// gallery entries loaded from disk.</summary>
    public static int NextId() => Interlocked.Increment(ref _nextId);

    public SstvImage(SstvMode mode, double offsetHz, int? id = null)
    {
        Id = id ?? Interlocked.Increment(ref _nextId);
        Mode = mode;
        OffsetHz = offsetHz;
        Rgb = new byte[mode.Width * mode.Height * 3];
    }

    public int Id { get; }
    public SstvMode Mode { get; }
    public double OffsetHz { get; }
    public byte[] Rgb { get; }
    public int RowsDone { get; internal set; }
    /// <summary>Sender clock error from the final sync fit (0.001 = +0.1 %).</summary>
    public double ClockError { get; internal set; }
    public SstvEndReason? EndReason { get; internal set; }
    /// <summary>Started from a train of sync pulses, without a VIS header
    /// (tuned in mid-picture, or the VIS lost to a fade).</summary>
    public bool ViaSync { get; internal set; }
    /// <summary>Manual slant correction (ppm) applied on top of the fit.</summary>
    public double SlantPpm { get; internal set; }
    /// <summary>Manual horizontal shift (pixels; + moves the picture right).</summary>
    public double ShiftPx { get; internal set; }
    /// <summary>The demodulated track behind the picture, for re-rendering.
    /// Null once the owner drops it to bound memory.</summary>
    public SstvRecording? Recording { get; set; }
}

/// <summary>Frequency track (Hz per 12 kHz sample) from just before the VIS
/// stop-bit edge to the end of the picture.</summary>
public sealed record SstvRecording(float[] Samples, long Base, long VisEnd);
