// SPDX-License-Identifier: GPL-2.0-or-later
using Xunit;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class TwoToneImdAnalyzerTests
{
    // Synthetic TX display: 1024 bins, 96 kHz span (93.75 Hz/px), carrier at
    // the display centre, floor at −90, tones at 0 dB, IMD3 at −32, IMD5 at −48.
    private static float[] Spectrum(
        float hzPerPixel, long centerHz, long carrierHz, double f1, double f2, bool lsb,
        double tone = 0, double imd3 = -32, double imd5 = -48, double floor = -90)
    {
        var bins = new float[1024];
        Array.Fill(bins, (float)floor);
        double sign = lsb ? -1 : 1;
        void Put(double offHz, double db)
        {
            int px = (int)Math.Round(bins.Length / 2.0 + (carrierHz + sign * offHz - centerHz) / hzPerPixel);
            if (px >= 0 && px < bins.Length) bins[px] = (float)db;
        }
        Put(f1, tone); Put(f2, tone);
        Put(2 * f1 - f2, imd3); Put(2 * f2 - f1, imd3);
        Put(3 * f1 - 2 * f2, imd5); Put(3 * f2 - 2 * f1, imd5);
        return bins;
    }

    [Fact]
    public void Measures_Imd3_And_Imd5_Usb()
    {
        var bins = Spectrum(93.75f, 14_200_000, 14_200_000, 700, 1900, lsb: false);
        Assert.True(TwoToneImdAnalyzer.TryMeasure(bins, 93.75f, 14_200_000, 14_200_000, 700, 1900, false,
            out double imd3, out double imd5));
        Assert.InRange(imd3, -32.5, -31.5);
        Assert.InRange(imd5, -48.5, -47.5);
    }

    [Fact]
    public void Measures_Lsb_With_Mirrored_Offsets()
    {
        var bins = Spectrum(93.75f, 7_100_000, 7_100_000, 700, 1900, lsb: true, imd3: -28);
        Assert.True(TwoToneImdAnalyzer.TryMeasure(bins, 93.75f, 7_100_000, 7_100_000, 700, 1900, true,
            out double imd3, out _));
        Assert.InRange(imd3, -28.5, -27.5);
    }

    [Fact]
    public void Carrier_Offset_From_Display_Centre_Is_Honoured()
    {
        // CTUN: LO at 14.200, TX carrier 5 kHz up the display.
        var bins = Spectrum(93.75f, 14_200_000, 14_205_000, 700, 1900, lsb: false, imd3: -35);
        Assert.True(TwoToneImdAnalyzer.TryMeasure(bins, 93.75f, 14_200_000, 14_205_000, 700, 1900, false,
            out double imd3, out _));
        Assert.InRange(imd3, -35.5, -34.5);
    }

    [Fact]
    public void Carrier_Estimate_Error_Is_Cancelled_By_Tone_Relative_Placement()
    {
        // The display really has the carrier at 14.200000, but the caller's
        // estimate is 300 Hz high (LO/TX offset). Tones are still found in
        // the wide search; products are placed from the measured tone bins,
        // so the reading is unaffected.
        var bins = Spectrum(93.75f, 14_200_000, 14_200_000, 700, 1900, lsb: false, imd3: -33);
        Assert.True(TwoToneImdAnalyzer.TryMeasure(bins, 93.75f, 14_200_000, 14_200_300, 700, 1900, false,
            out double imd3, out _));
        Assert.InRange(imd3, -33.5, -32.5);
    }

    [Fact]
    public void Reports_Worse_Of_The_Two_Products()
    {
        var bins = Spectrum(93.75f, 14_200_000, 14_200_000, 700, 1900, lsb: false, imd3: -40);
        // Make the upper product worse than the lower one.
        int px = (int)Math.Round(512 + (2 * 1900 - 700) / 93.75);
        bins[px] = -26f;
        Assert.True(TwoToneImdAnalyzer.TryMeasure(bins, 93.75f, 14_200_000, 14_200_000, 700, 1900, false,
            out double imd3, out _));
        Assert.InRange(imd3, -26.5, -25.5);
    }

    [Fact]
    public void Refuses_When_Tones_Are_Not_Above_Floor()
    {
        var bins = Spectrum(93.75f, 14_200_000, 14_200_000, 700, 1900, lsb: false, tone: -80, imd3: -85);
        Assert.False(TwoToneImdAnalyzer.TryMeasure(bins, 93.75f, 14_200_000, 14_200_000, 700, 1900, false,
            out double imd3, out _));
        Assert.True(double.IsNaN(imd3));
    }

    [Fact]
    public void Refuses_When_Bins_Cannot_Separate_The_Tones()
    {
        // 1 kHz per bin cannot resolve 700/1900 Hz tones.
        var bins = Spectrum(1000f, 14_200_000, 14_200_000, 700, 1900, lsb: false);
        Assert.False(TwoToneImdAnalyzer.TryMeasure(bins, 1000f, 14_200_000, 14_200_000, 700, 1900, false,
            out _, out _));
    }
}
