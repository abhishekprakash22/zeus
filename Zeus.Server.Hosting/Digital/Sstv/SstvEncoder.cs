// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// SSTV encoder: RGB image → phase-continuous audio (VIS header + scan lines).
// Phase 1 uses it to drive the decoder's round-trip tests; phase 3 streams its
// output through TxAudioIngest as the SSTV transmitter.
//
// Segment ends are computed from the ABSOLUTE schedule (ms since start →
// sample index), never by accumulating rounded per-segment sample counts, so
// a 187 s picture ends exactly where the mode table says it does — the
// receiver's slant correction must not be spent undoing our own drift.

namespace Zeus.Server.Hosting.Digital.Sstv;

public static class SstvEncoder
{
    public const double VisLeaderMs = 300, VisBreakMs = 10, VisBitMs = 30;

    /// <summary>VIS header duration: leader, break, leader, start, 7 data,
    /// parity, stop.</summary>
    public const double VisMs = 2 * VisLeaderMs + VisBreakMs + 10 * VisBitMs;

    /// <summary>
    /// Synthesise <paramref name="rgb"/> (row-major, 3 bytes/pixel, exactly
    /// mode.Width × mode.Height) in <paramref name="mode"/>.
    /// <paramref name="freqOffsetHz"/> shifts every tone (a mistuned receiver,
    /// in tests); <paramref name="clockScale"/> stretches time (a sender whose
    /// sound card is off-nominal — the slant the decoder must correct).
    /// </summary>
    public static float[] Encode(
        SstvMode mode, ReadOnlySpan<byte> rgb, int sampleRate,
        float amplitude = 0.8f, double freqOffsetHz = 0, double clockScale = 1.0)
    {
        if (rgb.Length != mode.Width * mode.Height * 3)
            throw new ArgumentException(
                $"image must be {mode.Width}x{mode.Height} RGB ({mode.Width * mode.Height * 3} bytes)",
                nameof(rgb));

        double totalMs = VisMs + mode.DurationMs;
        var outBuf = new float[(int)Math.Ceiling(totalMs * clockScale * sampleRate / 1000.0) + 1];
        var w = new ToneWriter(outBuf, sampleRate, amplitude, freqOffsetHz, clockScale);

        WriteVis(ref w, mode.VisCode);
        if (mode.LeadInMs > 0) w.Tone(SstvModes.SyncHz, mode.LeadInMs);

        var planes = BuildPlanes(mode, rgb);
        double lineBase = w.NowMs;
        for (int line = 0; line < mode.TxLines; line++)
        {
            WriteLine(ref w, mode, planes, line, lineBase + line * mode.LineMs);
        }
        return outBuf.AsSpan(0, w.Written).ToArray();
    }

    // ---- VIS ----------------------------------------------------------------

    private static void WriteVis(ref ToneWriter w, int code)
    {
        w.Tone(SstvModes.LeaderHz, VisLeaderMs);
        w.Tone(SstvModes.SyncHz, VisBreakMs);
        w.Tone(SstvModes.LeaderHz, VisLeaderMs);
        w.Tone(SstvModes.SyncHz, VisBitMs);                     // start bit
        int ones = 0;
        for (int b = 0; b < 7; b++)
        {
            bool one = ((code >> b) & 1) != 0;                  // LSB first
            if (one) ones++;
            w.Tone(one ? SstvModes.VisOneHz : SstvModes.VisZeroHz, VisBitMs);
        }
        w.Tone((ones & 1) != 0 ? SstvModes.VisOneHz : SstvModes.VisZeroHz, VisBitMs); // even parity
        w.Tone(SstvModes.SyncHz, VisBitMs);                     // stop bit
    }

    // ---- lines --------------------------------------------------------------

    private static void WriteLine(ref ToneWriter w, SstvMode mode, byte[][] planes, int line, double t0)
    {
        double scanMs = mode.ScanMs;
        // Gaps: sync pulse at SyncOffset, porch after it, separators elsewhere.
        // Anything not covered by a scan is "gap": 1200 Hz inside the sync
        // pulse, 1500 Hz (black) otherwise — the standard porch/separator tone.
        double syncStart = mode.SyncOffsetMs, syncEnd = syncStart + mode.SyncMs;
        double cursor = 0;
        foreach (var scan in mode.Scans.OrderBy(s => s.StartMs))
        {
            FillGap(ref w, t0, cursor, scan.StartMs, syncStart, syncEnd);
            byte[] plane = planes[(int)scan.Channel];
            int rowBase = line * mode.Width;
            for (int x = 0; x < mode.Width; x++)
                w.ToneUntil(SstvModes.LumaToHz(plane[rowBase + x]),
                    t0 + scan.StartMs + (x + 1) * mode.PixelMs);
            cursor = scan.StartMs + scanMs;
        }
        FillGap(ref w, t0, cursor, mode.LineMs, syncStart, syncEnd);
    }

    private static void FillGap(ref ToneWriter w, double t0, double from, double to,
                                double syncStart, double syncEnd)
    {
        if (to <= from) return;
        // Split [from,to) around the sync pulse.
        double a = Math.Max(from, syncStart), b = Math.Min(to, syncEnd);
        if (b > a)
        {
            if (a > from) w.ToneUntil(SstvModes.BlackHz, t0 + a);
            w.ToneUntil(SstvModes.SyncHz, t0 + b);
            if (to > b) w.ToneUntil(SstvModes.BlackHz, t0 + to);
        }
        else
        {
            w.ToneUntil(SstvModes.BlackHz, t0 + to);
        }
    }

    /// <summary>
    /// Per-channel planes indexed by <see cref="SstvChannel"/>, each laid out
    /// [txLine × width]. RGB modes: one row per line. PD: Y0/Y1 are the even
    /// and odd rows; Cr/Cb the average of the pair (4:2:0 vertically).
    /// </summary>
    private static byte[][] BuildPlanes(SstvMode mode, ReadOnlySpan<byte> rgb)
    {
        int w = mode.Width, lines = mode.TxLines;
        var planes = new byte[7][];
        for (int c = 0; c < 7; c++) planes[c] = new byte[lines * w];

        if (mode.Color == SstvColor.Rgb)
        {
            for (int y = 0; y < lines; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3, o = y * w + x;
                planes[(int)SstvChannel.R][o] = rgb[i];
                planes[(int)SstvChannel.G][o] = rgb[i + 1];
                planes[(int)SstvChannel.B][o] = rgb[i + 2];
            }
            return planes;
        }

        for (int line = 0; line < lines; line++)
        for (int x = 0; x < w; x++)
        {
            int i0 = ((2 * line) * w + x) * 3, i1 = ((2 * line + 1) * w + x) * 3, o = line * w + x;
            SstvColorSpace.ToYCrCb(rgb[i0], rgb[i0 + 1], rgb[i0 + 2], out byte y0, out byte cr0, out byte cb0);
            SstvColorSpace.ToYCrCb(rgb[i1], rgb[i1 + 1], rgb[i1 + 2], out byte y1, out byte cr1, out byte cb1);
            planes[(int)SstvChannel.Y0][o] = y0;
            planes[(int)SstvChannel.Y1][o] = y1;
            planes[(int)SstvChannel.Cr][o] = (byte)((cr0 + cr1 + 1) / 2);
            planes[(int)SstvChannel.Cb][o] = (byte)((cb0 + cb1 + 1) / 2);
        }
        return planes;
    }

    // ---- oscillator ---------------------------------------------------------

    private struct ToneWriter
    {
        private readonly float[] _buf;
        private readonly int _rate;
        private readonly float _amp;
        private readonly double _offset, _scale;
        private double _phase;
        public int Written;
        public double NowMs;

        public ToneWriter(float[] buf, int rate, float amp, double offset, double scale)
        {
            _buf = buf; _rate = rate; _amp = amp; _offset = offset; _scale = scale;
            _phase = 0; Written = 0; NowMs = 0;
        }

        public void Tone(double hz, double ms) => ToneUntil(hz, NowMs + ms);

        /// <summary>Emit <paramref name="hz"/> until absolute schedule time
        /// <paramref name="endMs"/>; phase carries across calls.</summary>
        public void ToneUntil(double hz, double endMs)
        {
            if (endMs <= NowMs) return;
            NowMs = endMs;
            int end = Math.Min(_buf.Length, (int)Math.Round(endMs * _scale * _rate / 1000.0));
            double step = 2 * Math.PI * (hz + _offset) / _rate;
            for (; Written < end; Written++)
            {
                _buf[Written] = (float)(_amp * Math.Sin(_phase));
                _phase += step;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            }
        }
    }
}

/// <summary>ITU-R BT.601 studio-swing RGB ⇄ YCrCb, the form MMSSTV (and so
/// most PD senders on the air, the ISS included) uses.</summary>
public static class SstvColorSpace
{
    public static void ToYCrCb(byte r, byte g, byte b, out byte y, out byte cr, out byte cb)
    {
        y = Clamp(16.0 + (65.738 * r + 129.057 * g + 25.064 * b) / 256.0);
        cb = Clamp(128.0 + (-37.945 * r - 74.494 * g + 112.439 * b) / 256.0);
        cr = Clamp(128.0 + (112.439 * r - 94.154 * g - 18.285 * b) / 256.0);
    }

    public static void ToRgb(byte y, byte cr, byte cb, out byte r, out byte g, out byte b)
    {
        double yy = 1.164383 * (y - 16), dr = cr - 128.0, db = cb - 128.0;
        r = Clamp(yy + 1.596027 * dr);
        g = Clamp(yy - 0.812968 * dr - 0.391762 * db);
        b = Clamp(yy + 2.017232 * db);
    }

    private static byte Clamp(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);
}
