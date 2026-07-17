// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

using Zeus.Protocol2;
using Xunit;

namespace Zeus.Server.Tests;

public class WidebandSpectrumAnalyzerTests
{
    [Fact]
    public void Analyze_MapsRfToneIntoZeroToSixtyMhzDisplay()
    {
        const double toneHz = 9_997_500.0; // exact FFT bin at 122.88 MSPS / 16k
        var samples = new short[Protocol2Client.WidebandFrameSamples];
        for (int i = 0; i < samples.Length; i++)
        {
            double phase = 2.0 * Math.PI * toneHz * i / Protocol2Client.WidebandAdcSampleRateHz;
            samples[i] = (short)Math.Round(Math.Sin(phase) * 20_000.0);
        }

        var analyzer = new WidebandSpectrumAnalyzer();
        var pan = new float[WidebandSpectrumAnalyzer.DisplayWidth];
        var wf = new float[WidebandSpectrumAnalyzer.DisplayWidth];
        analyzer.Analyze(samples, Protocol2Client.WidebandAdcSampleRateHz, pan, wf);

        int peak = 0;
        for (int i = 1; i < pan.Length; i++)
        {
            if (pan[i] > pan[peak]) peak = i;
        }

        double peakHz = (peak + 0.5) * WidebandSpectrumAnalyzer.HzPerPixel;
        Assert.InRange(peakHz, toneHz - 75_000.0, toneHz + 75_000.0);
        Assert.Equal(pan[peak], wf[peak]);
    }
}
