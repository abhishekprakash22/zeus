// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// SSTV mode table. One entry per analog mode Zeus decodes/encodes, with the
// line timing laid out as a list of scans at fixed offsets inside one
// transmitted line. Timings are the published ones (JL Barber, "Proposal for
// SSTV Mode Specifications", Dayton 2000) as also used by slowrx / QSSTV /
// MMSSTV. Every derived line time is asserted against the published figure in
// SstvTests, so a typo here fails CI instead of slanting every picture.
//
// Line anatomy by family (ms):
//   Martin   sync 4.862 | porch 0.572 | G | sep | B | sep | R | sep
//   Scottie  sep 1.5 | G | sep 1.5 | B | sync 9.0 | porch 1.5 | R
//            (plus ONE 9 ms "starting sync" right after the VIS header)
//   PD       sync 20 | porch 2.08 | Y(even row) | Cr | Cb | Y(odd row)
//            — one transmitted line carries TWO image rows.
//   Robot 72 sync 9 | porch 3 | Y 138 | sep 4.5 (1500) | porch 1.5 (1900) |
//            Cr 69 | sep 4.5 (2300) | porch 1.5 (1900) | Cb 69
//   Robot 36 two 150 ms lines, each: sync 9 | porch 3 | Y 88 | sep 4.5 |
//            porch 1.5 (1900) | chroma 44 — even lines carry Cr behind a
//            1500 Hz separator, odd lines Cb behind 2300 Hz. Modelled as one
//            300 ms "double line" (two rows, like PD) so the sync fit tracks
//            the even-line pulse and each row pair gets both chroma scans.
//            Chroma scans run at half the luma pixel time.

namespace Zeus.Server.Hosting.Digital.Sstv;

public enum SstvColor { Rgb, YCrCb }

/// <summary>What one scan inside a transmitted line carries.</summary>
public enum SstvChannel { R, G, B, Y0, Y1, Cr, Cb }

/// <summary>One scan: <paramref name="StartMs"/> from the start of the
/// transmitted line, lasting width × pixel time. <paramref name="PixelMs"/>
/// overrides the mode's pixel time (Robot chroma runs at half speed).</summary>
public readonly record struct SstvScan(SstvChannel Channel, double StartMs, double PixelMs = 0);

/// <summary>A fixed tone inside the line other than the sync pulse and black
/// (Robot's 2300 Hz separator, its 1900 Hz porches, Robot 36's second sync).
/// Anything a mode leaves unspecified is black (1500 Hz).</summary>
public readonly record struct SstvTone(double StartMs, double DurMs, double Hz);

public sealed record SstvMode(
    string Name,
    int VisCode,
    int Width,
    int Height,
    double SyncMs,
    double PixelMs,
    double LineMs,
    /// <summary>Offset of the line-sync pulse inside the transmitted line.</summary>
    double SyncOffsetMs,
    /// <summary>Time between the end of the VIS stop bit and the start of
    /// transmitted line 0 (Scottie's one-off starting sync).</summary>
    double LeadInMs,
    SstvColor Color,
    SstvScan[] Scans,
    SstvTone[]? Tones = null,
    /// <summary>Image rows per transmitted line; 0 = by colour (YCrCb 2, RGB 1).</summary>
    int Rows = 0)
{
    /// <summary>Image rows carried by one transmitted line (PD, Robot 36: 2).</summary>
    public int RowsPerLine => Rows > 0 ? Rows : Color == SstvColor.YCrCb ? 2 : 1;
    public int TxLines => Height / RowsPerLine;
    public double PixelOf(SstvScan s) => s.PixelMs > 0 ? s.PixelMs : PixelMs;
    public double ScanMsOf(SstvScan s) => Width * PixelOf(s);
    public double DurationMs => LeadInMs + TxLines * LineMs;
}

public static class SstvModes
{
    // ---- builders -----------------------------------------------------------

    private static SstvMode Martin(string name, int vis, double pixelMs)
    {
        const double sync = 4.862, porch = 0.572, sep = 0.572;
        double scan = 320 * pixelMs;
        double g = sync + porch, b = g + scan + sep, r = b + scan + sep;
        double line = r + scan + sep;
        return new SstvMode(name, vis, 320, 256, sync, pixelMs, line, 0, 0, SstvColor.Rgb,
            [new(SstvChannel.G, g), new(SstvChannel.B, b), new(SstvChannel.R, r)]);
    }

    private static SstvMode Scottie(string name, int vis, double pixelMs)
    {
        const double sync = 9.0, sep = 1.5, porch = 1.5;
        double scan = 320 * pixelMs;
        double g = sep, b = g + scan + sep, syncAt = b + scan, r = syncAt + sync + porch;
        double line = r + scan;
        return new SstvMode(name, vis, 320, 256, sync, pixelMs, line, syncAt, sync, SstvColor.Rgb,
            [new(SstvChannel.G, g), new(SstvChannel.B, b), new(SstvChannel.R, r)]);
    }

    private static SstvMode Pd(string name, int vis, int w, int h, double pixelMs)
    {
        const double sync = 20.0, porch = 2.08;
        double scan = w * pixelMs;
        double y0 = sync + porch;
        double line = y0 + 4 * scan;
        return new SstvMode(name, vis, w, h, sync, pixelMs, line, 0, 0, SstvColor.YCrCb,
            [new(SstvChannel.Y0, y0), new(SstvChannel.Cr, y0 + scan),
             new(SstvChannel.Cb, y0 + 2 * scan), new(SstvChannel.Y1, y0 + 3 * scan)]);
    }

    private static SstvMode Robot36()
    {
        const double sync = 9, porch = 3, sep = 4.5, porch2 = 1.5, half = 150;
        double y = 88.0 / 320, c = 44.0 / 320;
        double y0 = sync + porch, cr = y0 + 88 + sep + porch2;
        double y1 = half + y0, cb = y1 + 88 + sep + porch2;
        return new SstvMode("Robot 36", 8, 320, 240, sync, y, 2 * half, 0, 0, SstvColor.YCrCb,
            [new(SstvChannel.Y0, y0), new(SstvChannel.Cr, cr, c),
             new(SstvChannel.Y1, y1), new(SstvChannel.Cb, cb, c)],
            [new(y0 + 88, sep, SstvModes.BlackHz), new(y0 + 88 + sep, porch2, SstvModes.LeaderHz),
             new(half, sync, SstvModes.SyncHz),
             new(y1 + 88, sep, SstvModes.WhiteHz), new(y1 + 88 + sep, porch2, SstvModes.LeaderHz)],
            Rows: 2);
    }

    private static SstvMode Robot72()
    {
        const double sync = 9, porch = 3, sep = 4.5, porch2 = 1.5;
        double y = 138.0 / 320, c = 69.0 / 320;
        double y0 = sync + porch, cr = y0 + 138 + sep + porch2, cb = cr + 69 + sep + porch2;
        double line = cb + 69;
        return new SstvMode("Robot 72", 12, 320, 240, sync, y, line, 0, 0, SstvColor.YCrCb,
            [new(SstvChannel.Y0, y0), new(SstvChannel.Cr, cr, c), new(SstvChannel.Cb, cb, c)],
            [new(y0 + 138, sep, SstvModes.BlackHz), new(y0 + 138 + sep, porch2, SstvModes.LeaderHz),
             new(cr + 69, sep, SstvModes.WhiteHz), new(cr + 69 + sep, porch2, SstvModes.LeaderHz)],
            Rows: 1);
    }

    // ---- table --------------------------------------------------------------

    public static readonly SstvMode M1 = Martin("Martin 1", 44, 0.4576);
    public static readonly SstvMode M2 = Martin("Martin 2", 40, 0.2288);
    public static readonly SstvMode S1 = Scottie("Scottie 1", 60, 0.4320);
    public static readonly SstvMode S2 = Scottie("Scottie 2", 56, 0.2752);
    public static readonly SstvMode SDX = Scottie("Scottie DX", 76, 1.0800);
    public static readonly SstvMode PD50 = Pd("PD 50", 93, 320, 256, 0.286);
    public static readonly SstvMode PD90 = Pd("PD 90", 99, 320, 256, 0.532);
    public static readonly SstvMode PD120 = Pd("PD 120", 95, 640, 496, 0.190);
    public static readonly SstvMode PD160 = Pd("PD 160", 98, 512, 400, 0.382);
    public static readonly SstvMode PD180 = Pd("PD 180", 96, 640, 496, 0.286);
    public static readonly SstvMode PD240 = Pd("PD 240", 97, 640, 496, 0.382);
    public static readonly SstvMode PD290 = Pd("PD 290", 94, 800, 616, 0.286);
    public static readonly SstvMode R36 = Robot36();
    public static readonly SstvMode R72 = Robot72();

    public static readonly IReadOnlyList<SstvMode> All =
        [M1, M2, S1, S2, SDX, PD50, PD90, PD120, PD160, PD180, PD240, PD290, R36, R72];

    public static SstvMode? ByVis(int code)
    {
        foreach (var m in All) if (m.VisCode == code) return m;
        return null;
    }

    public static SstvMode? ByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string key = Normalise(name);
        foreach (var m in All) if (Normalise(m.Name) == key) return m;
        return null;

        static string Normalise(string s) =>
            new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    // ---- tones --------------------------------------------------------------

    public const double SyncHz = 1200, BlackHz = 1500, WhiteHz = 2300;
    public const double LeaderHz = 1900, VisOneHz = 1100, VisZeroHz = 1300;

    public static double LumaToHz(byte v) => BlackHz + v * (WhiteHz - BlackHz) / 255.0;

    public static byte HzToLuma(double hz)
    {
        double v = (hz - BlackHz) * 255.0 / (WhiteHz - BlackHz);
        return v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);
    }
}
