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
    private const double SyncTolHz = 150;          // pulse MEAN vs 1200 Hz
    private const double MinSyncContrastHz = 250;  // (before − pulse) + (after − pulse)
    private const double FitOutlierMs = 1.5;
    private const double MaxClockError = 0.02;
    private const int FalseStartLines = 12;
    private const int FalseStartMinSyncs = 4;
    private const int LostAfterLines = 25;

    // Idle history kept for the VIS edge refinement and the FSK-ID scan.
    private const int IdleKeepSamples = 6 * SampleRate;

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

    // FSK-ID scan armed by a completed picture.
    private SstvImage? _fskFor;
    private long _fskFrom, _fskScanAt;

    /// <summary>The picture being received, or null while hunting for VIS.</summary>
    public SstvImage? Current => _img?.Public;

    public event Action<SstvImage>? ImageStarted;
    /// <summary>(image, firstRow, rowCount) — rows are final for live display
    /// but will be redrawn by the end-of-picture re-render.</summary>
    public event Action<SstvImage, int, int>? RowsDecoded;
    public event Action<SstvImage, SstvEndReason>? ImageEnded;
    /// <summary>An FSK ID (sender's callsign) followed a finished picture.</summary>
    public event Action<SstvImage, string>? CallsignDecoded;

    public long SamplesProcessed => _n;

    public void Reset()
    {
        _fm.Reset();
        _track.Clear(0);
        _n = 0;
        _binCount = 0; _binAcc = 0; _binFill = 0; _visHoldUntilBin = 0;
        _img = null;
        _fskFor = null;
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

    // ---- FSK ID -------------------------------------------------------------

    private void ScanFskId()
    {
        var img = _fskFor!;
        _fskFor = null;
        if (FindFskId(_track, Math.Max(_track.Base, _fskFrom), _track.End, img.OffsetHz) is { } call)
            CallsignDecoded?.Invoke(img, call);
    }

    /// <summary>Find and read an FSK ID in [from, to) of the track, or null.</summary>
    private static string? FindFskId(Track track, long from, long to, double offset)
    {
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
            if (ReadFskId(track, edge, to, offset) is { } call) return call;
            t = edge + (long)bit;
        }
        return null;
    }

    private static string? ReadFskId(Track track, long startBitEdge, long to, double offset)
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

        if (Char() != 0x2A) return null;
        var sb = new System.Text.StringBuilder();
        int xsum = 0;
        while (true)
        {
            int? c = Char();
            if (c is null) return null;
            if (c == 0x01) break;
            if (sb.Length == FskMaxChars) return null;
            xsum ^= c.Value;
            sb.Append((char)(c.Value + 0x20));
        }
        if (sb.Length == 0 || Char() != xsum) return null;
        string call = sb.ToString().Trim();
        return call.Length == 0 ? null : call;
    }

    // ---- picture ------------------------------------------------------------

    private void Begin(SstvMode mode, long visEnd, double offset)
    {
        if (_fskFor is not null) ScanFskId();       // before the trim discards it
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

            if (FindSync(_track, img, predicted, win) is double ts)
            {
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
    private static double? FindSync(Track track, Image img, double predicted, double win)
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
        return sw > 0 ? st / sw - len / 2.0 : bestT;
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
            if (reason == SstvEndReason.Complete)
            {
                // Relative to where the picture ENDED on the track, not to how
                // much audio this Process() call happened to carry — a big
                // block (a worker catching up) would otherwise start the search
                // past the ID, or trim it away.
                long pictureEnd = (long)img.LineStart(lines);
                _fskFor = img.Public;
                _fskFrom = pictureEnd - (long)(300 * SamplesPerMs);
                _fskScanAt = pictureEnd + (long)(FskWaitMs * SamplesPerMs);
            }
        }
        img.Public.EndReason = reason;
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
            if (FindSync(track, img, predicted, img.SyncWindow) is double ts)
            {
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
