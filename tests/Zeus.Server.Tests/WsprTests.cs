// SPDX-License-Identifier: GPL-2.0-or-later
//
// WSPR in-core tests: the whole receive path end to end — the managed
// encoder's symbols synthesized into a 12 kHz slot, MixAndDecimate32 down to
// the 375 Hz baseband, the managed decoder back to the message.

using Zeus.Server.Hosting.Digital;
using Zeus.Server.Hosting.Digital.Wspr;

namespace Zeus.Server.Tests;

public sealed class WsprTests
{
    [Fact]
    public void Loopback_Encode_Mix_Decode_RoundTrips()
    {
        var sym = new byte[162];
        Assert.True(WsprEncoder.TryEncode("K1ABC FN42 30", sym));

        // Synthesize one 120 s slot at 12 kHz: 1 s lead-in, then 162 symbols
        // of 8192 samples (110.6 s) at 1500 Hz + (sym−1.5)·1.4648 Hz.
        const int rate = 12_000, spSym = 8_192;
        var audio = new float[1_440_000];
        double phase = 0, spacing = 12_000.0 / 8_192.0;
        var rng = new Random(7);
        for (int s = 0; s < 162; s++)
        {
            double dp = 2 * Math.PI * (1500.0 + (sym[s] - 1.5) * spacing) / rate;
            for (int i = 0; i < spSym; i++)
            {
                phase += dp;
                audio[rate + s * spSym + i] = 0.5f * (float)Math.Sin(phase);
            }
        }
        for (int i = 0; i < audio.Length; i++)
            audio[i] += 0.01f * (float)(rng.NextDouble() - 0.5);

        var (I, Q) = WsprService.MixAndDecimate32(audio);
        Assert.Equal(45_000, I.Length);

        var spots = WsprDecoder.Decode(I, Q, 14_095_600);
        var spot = Assert.Single(spots);
        Assert.Equal("K1ABC FN42 30", spot.Message);
        // Absolute frequency lands at dial + ~1500 Hz (MHz units).
        Assert.InRange(spot.FreqMhz, 14.0960, 14.0980);
    }

    [Fact]
    public void MixAndDecimate32_RejectsOutOfWindowEnergy()
    {
        // A 3 kHz tone (far outside the 1400–1600 Hz WSPR window) must be
        // crushed by the 160 Hz lowpass after the 1500 Hz mixdown.
        var audio = new float[240_000]; // 20 s is plenty
        for (int i = 0; i < audio.Length; i++)
            audio[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 3000.0 * i / 12_000.0);
        var (I, Q) = WsprService.MixAndDecimate32(audio);
        double p = 0;
        for (int i = 500; i < I.Length; i++) p += I[i] * I[i] + Q[i] * Q[i];
        p /= Math.Max(1, I.Length - 500);
        Assert.True(p < 1e-5, $"stopband leakage too high: {p:E2}");
    }
}
