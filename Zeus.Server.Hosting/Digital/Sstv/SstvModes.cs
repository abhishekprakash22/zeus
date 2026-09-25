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

namespace Zeus.Server.Hosting.Digital.Sstv;

public enum SstvColor { Rgb, YCrCb }

/// <summary>What one scan inside a transmitted line carries.</summary>
public enum SstvChannel { R, G, B, Y0, Y1, Cr, Cb }

/// <summary>One scan: <paramref name="StartMs"/> from the start of the
/// transmitted line, lasting width × pixel time.</summary>
public readonly record struct SstvScan(SstvChannel Channel, double StartMs);

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
    SstvScan[] Scans)
{
    /// <summary>Image rows carried by one transmitted line (PD: 2).</summary>
    public int RowsPerLine => Color == SstvColor.YCrCb ? 2 : 1;
    public int TxLines => Height / RowsPerLine;
    public double ScanMs => Width * PixelMs;
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

    public static readonly IReadOnlyList<SstvMode> All =
        [M1, M2, S1, S2, SDX, PD50, PD90, PD120, PD160, PD180, PD240, PD290];

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
