// SPDX-License-Identifier: GPL-2.0-or-later
//
// Digital-mode natives. libzeus_ft8: synthesize an FT8 / FT4 transmission
// with the native encoder, drop it into a slot of audio with a little noise,
// and decode it back. libzeus_wspr: binds and encodes a message. Each runs
// wherever its library is staged for this RID and is skipped elsewhere (the
// decoders degrade to "unavailable", never throw).

using Zeus.Server.Hosting.Digital;

namespace Zeus.Server.Tests;

public sealed class Ft8NativeTests
{
    [SkippableTheory]
    [InlineData(false, 15.0)]   // FT8: 15 s slot
    [InlineData(true, 7.5)]     // FT4: 7.5 s slot
    public void SynthThenDecode_RecoversTheMessage(bool isFt4, double slotSec)
    {
        Skip.IfNot(Ft8Native.Available, "libzeus_ft8 not staged for this platform");

        const int rate = 48_000;
        const string message = "CQ EA5IUE IM76";
        var tx = Ft8Native.Synth(message, isFt4, 1500f, rate, out var error);
        Assert.Null(error);
        Assert.NotNull(tx);

        // Slot of audio: the signal starts 0.5 s in, over light white noise.
        var slot = new float[(int)(slotSec * rate)];
        var rng = new Random(7);
        for (int i = 0; i < slot.Length; i++) slot[i] = (float)(rng.NextDouble() - 0.5) * 0.02f;
        int start = rate / 2;
        for (int i = 0; i < tx!.Length && start + i < slot.Length; i++) slot[start + i] += 0.3f * tx[i];

        var decodes = Ft8Native.Decode(slot, rate, isFt4);

        var hit = Assert.Single(decodes, d => d.Text.Trim() == message);
        Assert.InRange(hit.FreqHz, 1480, 1520);
    }

    [SkippableFact]
    public void Wspr_Binds_AndEncodesAMessage()
    {
        Skip.IfNot(WsprNative.Available, "libzeus_wspr not staged for this platform");

        var symbols = new byte[WsprNative.SymbolCount];
        Assert.True(WsprNative.Encode("EA5IUE IM76 30", symbols));
        Assert.All(symbols, s => Assert.InRange(s, (byte)0, (byte)3));
        Assert.Contains(symbols, s => s != 0);
    }

    [SkippableFact]
    public unsafe void Wspr_DecodesASlot_OnTheServiceThreadStack()
    {
        Skip.IfNot(WsprNative.Available, "libzeus_wspr not staged for this platform");

        // One 120 s slot of noise at the decoder's 375 Hz complex input rate.
        // wsprd keeps ~0.8 MB on the stack: on a default macOS thread (512 KB)
        // this call overflowed and killed the process. It must run on a thread
        // sized like WsprService's decode thread.
        const int samples = 375 * 120;
        var i = new float[samples];
        var q = new float[samples];
        var rng = new Random(3);
        for (int k = 0; k < samples; k++) { i[k] = (float)(rng.NextDouble() - 0.5); q[k] = (float)(rng.NextDouble() - 0.5); }
        var spots = new ZeusWsprSpot[64];

        int n = int.MinValue;
        var t = new Thread(() =>
        {
            fixed (float* pi = i)
            fixed (float* pq = q)
            fixed (ZeusWsprSpot* ps = spots)
                n = WsprNative.Decode(pi, pq, samples, 14_095_600, ps, spots.Length);
        }, WsprService.WsprDecodeStackBytes);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), "WSPR decode did not finish");
        Assert.InRange(n, 0, spots.Length);   // noise: no spots, but a clean return
    }
}
