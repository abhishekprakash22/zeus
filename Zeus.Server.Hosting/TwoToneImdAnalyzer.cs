// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Live two-tone intermodulation readout from the TX panadapter bins.
///
/// With PureSignal armed the TX panadapter is the post-PA feedback spectrum,
/// so the products it shows are the amplifier's — measured before, during
/// and after the predistorter converges. Tones sit at the carrier ± f1 / f2
/// (sign by sideband); the odd-order products at RF are
///   IMD3: 2f1−f2 and 2f2−f1      IMD5: 3f1−2f2 and 3f2−2f1
/// each relative to the carrier. Every value is a peak search in a small
/// window around its expected bin (the WDSP display bins are already
/// peak-held per pixel), and the readout is the WORSE of the two products
/// relative to the MEAN of the two tones, in dBc.
///
/// Returns false (NaN) whenever a number would be a guess: two-tone off, bins
/// too coarse to separate the tones, a tone that isn't clearly above the
/// floor, or a product window that falls off the display.
/// </summary>
internal static class TwoToneImdAnalyzer
{
    // A tone must clear the display's median floor by this much to count as
    // found — below it we're measuring noise, not the PA.
    private const double MinToneAboveFloorDb = 25.0;
    // A product must clear the floor by this much to be a measurement rather
    // than floor noise (post-convergence products sink below the display).
    private const double MinProductAboveFloorDb = 3.0;
    // Tone search half-window around the carrier-derived position. Generous:
    // the carrier estimate on the TX display can be a few hundred Hz off.
    private const double ToneSearchHalfWidthHz = 400.0;

    public static bool TryMeasure(
        ReadOnlySpan<float> bins, float hzPerPixel, long centerHz, long carrierHz,
        double f1Hz, double f2Hz, bool lowerSideband,
        out double imd3Dbc, out double imd5Dbc)
    {
        imd3Dbc = double.NaN;
        imd5Dbc = double.NaN;
        int n = bins.Length;
        if (n < 16 || !(hzPerPixel > 0f) || !double.IsFinite(f1Hz) || !double.IsFinite(f2Hz))
            return false;
        double lo = Math.Min(f1Hz, f2Hz), hi = Math.Max(f1Hz, f2Hz);
        double spacing = hi - lo;
        if (spacing < 50.0) return false;
        // Need at least ~3 bins between the tones to tell them (and the
        // products, which sit one spacing outboard) apart.
        if (hzPerPixel > spacing / 3.0) return false;

        double sign = lowerSideband ? -1.0 : 1.0;
        double floorDb = MedianFloor(bins);

        // 1) Find the two tones near where the carrier says they should be.
        //    Wide-ish window: the carrier estimate on the TX display can be
        //    off by a few hundred Hz (LO/TX offset, CTUN, frame geometry).
        int toneHalf = Math.Max(2, (int)Math.Ceiling(ToneSearchHalfWidthHz / hzPerPixel));
        // …but never so wide that one tone's window reaches the other tone.
        int spacingPxTheory = (int)(spacing / hzPerPixel);
        toneHalf = Math.Min(toneHalf, Math.Max(1, spacingPxTheory / 2 - 1));
        if (!ArgMaxNear(bins, hzPerPixel, centerHz, carrierHz + sign * lo, toneHalf, out int pxA, out double toneA)) return false;
        if (!ArgMaxNear(bins, hzPerPixel, centerHz, carrierHz + sign * hi, toneHalf, out int pxB, out double toneB)) return false;
        if (toneA - floorDb < MinToneAboveFloorDb || toneB - floorDb < MinToneAboveFloorDb) return false;
        int spacingPx = Math.Abs(pxB - pxA);
        if (spacingPx < 3) return false;
        double toneMean = 0.5 * (toneA + toneB);

        // 2) Products are placed from the MEASURED tone bins, not the carrier:
        //    2·A−B and 2·B−A sit exactly one tone-spacing outboard of each
        //    tone in pixel space, so any carrier error cancels. Window a
        //    quarter of the spacing either side (never less than one bin).
        int prodHalf = Math.Max(1, spacingPx / 4);
        if (!PeakAt(bins, 2 * pxA - pxB, prodHalf, out double p3a)) return false;
        if (!PeakAt(bins, 2 * pxB - pxA, prodHalf, out double p3b)) return false;
        // Floor guard: when both products are within a few dB of the display
        // floor, the true IMD is below what this display can measure — a
        // number here would just track floor noise. Refuse rather than jitter.
        if (Math.Max(p3a, p3b) - floorDb < MinProductAboveFloorDb) return false;
        imd3Dbc = Math.Max(p3a, p3b) - toneMean;

        // 3) 5th order two spacings outboard. Optional — off-display is fine.
        if (PeakAt(bins, 3 * pxA - 2 * pxB, prodHalf, out double p5a)
            && PeakAt(bins, 3 * pxB - 2 * pxA, prodHalf, out double p5b))
            imd5Dbc = Math.Max(p5a, p5b) - toneMean;
        return true;
    }

    private static bool ArgMaxNear(
        ReadOnlySpan<float> bins, float hzPerPixel, long centerHz, double targetHz, int half,
        out int peakPx, out double peakDb)
    {
        peakPx = -1;
        peakDb = double.NegativeInfinity;
        int n = bins.Length;
        int centerPx = (int)Math.Round(n / 2.0 + (targetHz - centerHz) / hzPerPixel);
        int from = centerPx - half, to = centerPx + half;
        if (from < 0 || to >= n) return false;
        for (int i = from; i <= to; i++)
        {
            float v = bins[i];
            if (!float.IsFinite(v)) continue;
            if (v > peakDb) { peakDb = v; peakPx = i; }
        }
        return peakPx >= 0;
    }

    private static bool PeakAt(ReadOnlySpan<float> bins, int centerPx, int half, out double peakDb)
    {
        peakDb = double.NegativeInfinity;
        int from = centerPx - half, to = centerPx + half;
        if (from < 0 || to >= bins.Length) return false;
        bool any = false;
        for (int i = from; i <= to; i++)
        {
            float v = bins[i];
            if (!float.IsFinite(v)) continue;
            any = true;
            if (v > peakDb) peakDb = v;
        }
        return any;
    }

    private static double MedianFloor(ReadOnlySpan<float> bins)
    {
        // Median of the finite bins — robust to the tones and products, which
        // occupy a handful of pixels out of the whole display.
        Span<float> tmp = bins.Length <= 4096 ? stackalloc float[bins.Length] : new float[bins.Length];
        int k = 0;
        foreach (float v in bins) if (float.IsFinite(v)) tmp[k++] = v;
        if (k == 0) return double.NegativeInfinity;
        var s = tmp.Slice(0, k);
        s.Sort();
        return s[k / 2];
    }
}
