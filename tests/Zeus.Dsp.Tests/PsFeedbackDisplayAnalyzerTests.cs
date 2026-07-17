// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Dsp;
using Zeus.Dsp.Wdsp;

namespace Zeus.Dsp.Tests;

/// <summary>
/// Pins the PS-feedback display analyzer's independence from the TX display
/// analyzer (#960 G2E "panadapter frozen on TX" rework).
///
/// The failure chain in the field (ANAN-G2E / HermesC10, RX at 192 kHz):
/// the TX display analyzer never opens at RX rates above the TXA DSP rate
/// (96 kHz DSP cannot render a 192 kHz span), and OpenPsFeedbackAnalyzer used
/// to skip whenever the TX analyzer was dead — so during a keyed PS burst,
/// which diverts the single-ADC board's ONLY DDC to feedback, there was no
/// live display source at all: RX starved, TX absent, PS-FB never opened.
/// Every display frame went out stale and the panadapter/waterfall froze for
/// the whole transmission.
///
/// The fix: OpenPsFeedbackAnalyzer inherits the first RX channel's geometry
/// when the TX display analyzer is not alive. Its own source runs at the PS
/// feedback rate (192 kHz), so its rate rule passes exactly where the TX
/// analyzer's fails. These tests prove the analyzer opens and produces pixels
/// in that regime, and that disarm still tears it down.
/// </summary>
[Collection("Wdsp")]
public class PsFeedbackDisplayAnalyzerTests
{
    private const int PsBlock = 1024;   // WdspDspEngine.PsFeedbackBlockSize
    private const int Width = 2048;

    private static bool WdspAvailable()
    {
        try { return WdspNativeLoader.TryProbe(); }
        catch { return false; }
    }

    private static void FeedToneBlocks(WdspDspEngine engine, int blocks)
    {
        // 10 kHz tone at 192 kHz feedback rate, amplitude 0.5 — representative
        // coupler/reference content; enough energy for the analyzer FFT.
        var txI = new float[PsBlock];
        var txQ = new float[PsBlock];
        var rxI = new float[PsBlock];
        var rxQ = new float[PsBlock];
        double phase = 0;
        double step = 2 * Math.PI * 10_000 / 192_000.0;
        for (int b = 0; b < blocks; b++)
        {
            for (int i = 0; i < PsBlock; i++)
            {
                txI[i] = (float)(0.5 * Math.Cos(phase));
                txQ[i] = (float)(0.5 * Math.Sin(phase));
                rxI[i] = txI[i];
                rxQ[i] = txQ[i];
                phase += step;
            }
            engine.FeedPsFeedbackBlock(txI, txQ, rxI, rxQ);
        }
    }

    [SkippableFact]
    public void PsArm_At192kRx_OpensFeedbackAnalyzer_WithoutTxDisplayAnalyzer()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        using var engine = new WdspDspEngine();
        int rx = engine.OpenChannel(sampleRateHz: 192_000, pixelWidth: Width);
        try
        {
            // P2 profile at 192 kHz output: TXA DSP rate is 96 kHz, below the
            // 192 kHz RX span, so the TX display analyzer must NOT open. This
            // is the exact regime the G2E field freeze happened in — if this
            // assert ever starts failing (TX analyzer learns to render wider
            // spans), the PS-FB fallback below becomes belt-and-braces rather
            // than load-bearing, which is fine.
            engine.OpenTxChannel(outputRateHz: 192_000);
            var px = new float[Width];
            Assert.False(
                engine.TryGetTxDisplayPixels(DisplayPixout.Panadapter, px),
                "TX display analyzer unexpectedly alive at 192k RX — precondition changed");

            // Pre-fix: OpenPsFeedbackAnalyzer skipped here (txDisp dead) and
            // this returned false forever. Post-fix it inherits RX geometry.
            engine.SetPsEnabled(true);
            FeedToneBlocks(engine, 64);

            // WDSP's analyzer computes FFTs on its own worker thread, so pixel
            // freshness lags the Spectrum0 feed by wall-clock — poll with a
            // real sleep or a loaded CI runner finishes the loop before the
            // first FFT lands (observed: pass locally, fail on the macos-arm64
            // runner with a tight no-sleep loop).
            bool gotPan = false, gotWf = false;
            for (int attempt = 0; attempt < 100 && !(gotPan && gotWf); attempt++)
            {
                gotPan |= engine.TryGetPsFeedbackDisplayPixels(DisplayPixout.Panadapter, px);
                gotWf |= engine.TryGetPsFeedbackDisplayPixels(DisplayPixout.Waterfall, px);
                if (!(gotPan && gotWf))
                {
                    FeedToneBlocks(engine, 8);
                    Thread.Sleep(25);
                }
            }
            Assert.True(gotPan, "PS-feedback panadapter pixels never became fresh at 192k RX");
            Assert.True(gotWf, "PS-feedback waterfall pixels never became fresh at 192k RX");

            // Disarm must tear the analyzer down again (stale-slot safety).
            engine.SetPsEnabled(false);
            Assert.False(engine.TryGetPsFeedbackDisplayPixels(DisplayPixout.Panadapter, px));
        }
        finally
        {
            engine.CloseChannel(rx);
        }
    }

    [SkippableFact]
    public void PsArm_At48kRx_StillOpensFeedbackAnalyzer_ViaTxGeometry()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        using var engine = new WdspDspEngine();
        int rx = engine.OpenChannel(sampleRateHz: 48_000, pixelWidth: Width);
        try
        {
            // At 48 kHz RX the TX display analyzer DOES open (96k DSP ≥ 48k
            // span, integer ratio) — the historical path. The PS-FB analyzer
            // must keep working through the TX-geometry branch, byte-identical
            // to pre-fix behaviour.
            engine.OpenTxChannel(outputRateHz: 192_000);
            engine.SetPsEnabled(true);
            FeedToneBlocks(engine, 64);

            var px = new float[Width];
            // Same async-FFT polling contract as the 192k test above.
            bool got = false;
            for (int attempt = 0; attempt < 100 && !got; attempt++)
            {
                got = engine.TryGetPsFeedbackDisplayPixels(DisplayPixout.Panadapter, px);
                if (!got)
                {
                    FeedToneBlocks(engine, 8);
                    Thread.Sleep(25);
                }
            }
            Assert.True(got, "PS-feedback pixels never became fresh at 48k RX (TX-geometry path)");

            engine.SetPsEnabled(false);
        }
        finally
        {
            engine.CloseChannel(rx);
        }
    }
}
