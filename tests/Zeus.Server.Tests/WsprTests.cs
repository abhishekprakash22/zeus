// SPDX-License-Identifier: GPL-2.0-or-later
//
// WSPR in-core tests. The loopback runs only where libzeus_wspr is staged
// beside the test binary (linux/mac CI legs, dev boxes after native/wspr
// build); absent that, the seam must degrade exactly as promised
// (Available=false, Encode=false, Decode=-1, nothing throws).

using Zeus.Server.Hosting.Digital;

namespace Zeus.Server.Tests;

public sealed class WsprTests
{
    [Fact]
    public void Native_Absent_Or_Present_NeverThrows()
    {
        // Forces the lazy bind through the public guards.
        Span<byte> sym = stackalloc byte[162];
        bool ok = WsprNative.Encode("K1ABC FN42 30", sym);
        Assert.Equal(WsprNative.Available, ok);
    }

    [Fact]
    public unsafe void Loopback_Encode_Mix_Decode_RoundTrips()
    {
        if (!WsprNative.Available) return; // native not staged on this leg

        Span<byte> sym = stackalloc byte[162];
        Assert.True(WsprNative.Encode("K1ABC FN42 30", sym));

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

        var spots = new ZeusWsprSpot[16];
        int n;
        fixed (float* pi = I) fixed (float* pq = Q) fixed (ZeusWsprSpot* ps = spots)
            n = WsprNative.Decode(pi, pq, I.Length, 14_095_600, ps, spots.Length);

        Assert.True(n >= 1, "clean loopback slot must decode");
        string msg;
        fixed (byte* pm = spots[0].Message)
            msg = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)pm) ?? "";
        Assert.Contains("K1ABC", msg);
        Assert.Contains("FN42", msg);
        // Absolute frequency lands at dial + ~1500 Hz (MHz units).
        Assert.InRange(spots[0].FreqHz, 14.0960, 14.0980);
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
