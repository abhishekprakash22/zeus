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
    private const double ToneTolHz = 80;

    // Sync tracking.
    private const double SyncTolHz = 110;
    private const double FitOutlierMs = 1.5;
    private const double MaxClockError = 0.02;
    private const int FalseStartLines = 12;
    private const int FalseStartMinSyncs = 4;
    private const int LostAfterLines = 25;

    // Idle history kept for the VIS edge refinement.
    private const int IdleKeepSamples = 2 * SampleRate;

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

    /// <summary>The picture being received, or null while hunting for VIS.</summary>
    public SstvImage? Current => _img?.Public;

    public event Action<SstvImage>? ImageStarted;
    /// <summary>(image, firstRow, rowCount) — rows are final for live display
    /// but will be redrawn by the end-of-picture re-render.</summary>
    public event Action<SstvImage, int, int>? RowsDecoded;
    public event Action<SstvImage, SstvEndReason>? ImageEnded;

    public long SamplesProcessed => _n;

    public void Reset()
    {
        _fm.Reset();
        _track.Clear(0);
        _n = 0;
        _binCount = 0; _binAcc = 0; _binFill = 0; _visHoldUntilBin = 0;
        _img = null;
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

            _binAcc += Math.Clamp(f, 900f, 2600f);
            if (++_binFill == BinSamples)
            {
                _bins[_binCount % BinHistory] = (float)(_binAcc / BinSamples);
                _binCount++;
                _binAcc = 0; _binFill = 0;
                if (_binCount >= _visHoldUntilBin) TryVis();
            }
        }

        if (_img is not null) Advance();
        else if (_track.Length > 2 * IdleKeepSamples) _track.TrimBefore(_n - IdleKeepSamples);
    }

    // ---- VIS ----------------------------------------------------------------

    private float Bin(long k) => _bins[k % BinHistory];

    private void TryVis()
    {
        long c = _binCount - 2;                   // centre bin of the stop bit
        long leaderEnd = c - 31;
        long leaderStart = leaderEnd - LeaderBins + 1;
        if (leaderStart < 0 || _binCount - leaderStart > BinHistory) return;

        // Leader: median must sit near 1900, nearly every bin close to it.
        Span<float> lead = stackalloc float[LeaderBins];
        for (int i = 0; i < LeaderBins; i++) lead[i] = Bin(leaderStart + i);
        lead.Sort();
        double median = lead[LeaderBins / 2];
        double offset = median - SstvModes.LeaderHz;
        if (Math.Abs(offset) > MaxOffsetHz) return;
        int good = 0;
        for (int i = 0; i < LeaderBins; i++) if (Math.Abs(lead[i] - median) < ToneTolHz) good++;
        if (good < LeaderBins - 4) return;

        // Ten 30 ms bits, read at their centre bins.
        double BitHz(int j) => Bin(c - 3 * (9 - j)) - offset;
        if (Math.Abs(BitHz(0) - SstvModes.SyncHz) > ToneTolHz) return;   // start
        if (Math.Abs(BitHz(9) - SstvModes.SyncHz) > ToneTolHz) return;   // stop
        int code = 0, ones = 0;
        for (int j = 1; j <= 8; j++)
        {
            double hz = BitHz(j);
            bool one;
            if (Math.Abs(hz - SstvModes.VisOneHz) < ToneTolHz) one = true;
            else if (Math.Abs(hz - SstvModes.VisZeroHz) < ToneTolHz) one = false;
            else return;
            if (one) { ones++; if (j <= 7) code |= 1 << (j - 1); }
        }
        if ((ones & 1) != 0) return;                                    // even parity
        var mode = SstvModes.ByVis(code);
        if (mode is null) return;

        // Refine the start-bit edge (1900 → 1200) to the sample, then the
        // picture starts exactly ten bits later.
        long startBin = c - 27;
        long est = startBin * BinSamples + BinSamples / 2 - (long)(15 * SamplesPerMs);
        long edge = RefineFallingEdge(est, (long)(12 * SamplesPerMs), (long)(5 * SamplesPerMs));
        long visEnd = edge + (long)Math.Round(10 * SstvEncoder.VisBitMs * SamplesPerMs);

        // Don't re-detect this same header on the next bins.
        _visHoldUntilBin = _binCount + 60;

        if (_img is not null) Finish(SstvEndReason.Interrupted);
        Begin(mode, visEnd, offset);
    }

    /// <summary>Sample index in [est−span, est+span] maximising
    /// mean(before) − mean(after) over <paramref name="w"/> samples.</summary>
    private long RefineFallingEdge(long est, long span, long w)
    {
        long best = est;
        double bestScore = double.MinValue;
        long lo = Math.Max(_track.Base + w, est - span);
        long hi = Math.Min(_n - w, est + span);
        for (long t = lo; t <= hi; t++)
        {
            double score = _track.Mean(t - w, t) - _track.Mean(t, t + w);
            if (score > bestScore) { bestScore = score; best = t; }
        }
        return best;
    }

    // ---- picture ------------------------------------------------------------

    private void Begin(SstvMode mode, long visEnd, double offset)
    {
        _track.TrimBefore(visEnd - (long)(50 * SamplesPerMs));
        var img = new Image(mode, visEnd, offset);
        _img = img;
        ImageStarted?.Invoke(img.Public);
    }

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

            if (FindSync(img, predicted, win) is double ts)
            {
                img.Syncs.Add((n, ts));
                img.LastSyncLine = n;
                img.Refit();
            }

            RenderLine(img, n);
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
    /// or null if nothing pulse-shaped is there.</summary>
    private double? FindSync(Image img, double predicted, double win)
    {
        int len = (int)Math.Round(img.SyncLen);
        // Never before the VIS stop-bit edge: Martin/PD line 0 starts with its
        // sync pulse flush against the (also 1200 Hz) stop bit, and a window
        // reaching back into it finds one long plateau instead of the pulse.
        long lo = Math.Max(Math.Max(_track.Base, img.VisEnd), (long)Math.Floor(predicted - win));
        long hi = Math.Min(_n - len, (long)Math.Ceiling(predicted + win));
        if (hi <= lo) return null;

        double target = SstvModes.SyncHz + img.Offset;
        int score = 0;
        for (long t = lo; t < lo + len; t++) if (IsSync(t)) score++;
        int best = score; long first = lo, last = lo;
        for (long t = lo + 1; t <= hi; t++)
        {
            if (IsSync(t - 1)) score--;
            if (IsSync(t + len - 1)) score++;
            if (score > best) { best = score; first = last = t; }
            else if (score == best) last = t;
        }
        if (best < 0.5 * len) return null;
        return (first + last) / 2.0;

        bool IsSync(long t) => Math.Abs(_track[t] - target) < SyncTolHz;
    }

    private void RenderLine(Image img, int n)
    {
        var m = img.Mode;
        double lineStart = img.LineStart(n);
        double px = m.PixelMs * img.MsToSamples;
        int w = m.Width;
        Span<byte> scratch = img.Scratch;             // 4 planes × width (PD) / 3 (RGB)

        for (int s = 0; s < m.Scans.Length; s++)
        {
            double start = lineStart + m.Scans[s].StartMs * img.MsToSamples;
            int plane = (int)m.Scans[s].Channel;
            for (int x = 0; x < w; x++)
            {
                double a = start + x * px;
                double hz = _track.Mean(a, a + px) - img.Offset;
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
            img.Refit();
            for (int n = 0; n < lines; n++) RenderLine(img, n);
            int keep = lines * img.Mode.RowsPerLine * img.Mode.Width * 3;
            Array.Clear(img.Public.Rgb, keep, img.Public.Rgb.Length - keep);
            img.Public.RowsDone = lines * img.Mode.RowsPerLine;
            img.Public.ClockError = img.ClockScale - 1;
        }
        img.Public.EndReason = reason;
        _track.TrimBefore(_n - IdleKeepSamples);
        ImageEnded?.Invoke(img.Public, reason);
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
        public int NextLine;
        public int LastSyncLine = -1;
        public double A, B;

        public Image(SstvMode mode, long visEnd, double offset)
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
            Public = new SstvImage(mode, offset);
        }

        public double ClockScale => B / NominalB;
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

    public SstvImage(SstvMode mode, double offsetHz)
    {
        Id = Interlocked.Increment(ref _nextId);
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
}
