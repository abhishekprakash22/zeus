// SPDX-License-Identifier: GPL-2.0-or-later
//
// Digital-mode natives. libzeus_ft8: synthesize an FT8 / FT4 transmission
// with the native encoder, drop it into a slot of audio with a little noise,
// and decode it back. Runs wherever the library is staged for this RID and
// is skipped elsewhere (the decoder degrades to "unavailable", never throws).
// (WSPR has no native library any more — see WsprManagedTests.)

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
        // A real SNR in the 2500 Hz reference, not ft8_lib's demo score/2 —
        // that one is always positive and read ~28 dB high against the network.
        Assert.InRange(hit.SnrDb, -30, 40);
    }

    [SkippableFact]
    public void Snr_FollowsTheSignalLevel_AndReadsBelowZeroInNoise()
    {
        Skip.IfNot(Ft8Native.Available, "libzeus_ft8 not staged for this platform");

        const int rate = 48_000;
        const string message = "CQ EA5IUE IM76";
        var tx = Ft8Native.Synth(message, isFt4: false, 1500f, rate, out var error);
        Assert.Null(error);

        int SnrAt(float amplitude)
        {
            var slot = new float[15 * rate];
            var rng = new Random(11);
            for (int i = 0; i < slot.Length; i++) slot[i] = (float)(rng.NextDouble() - 0.5) * 0.2f;
            int start = rate / 2;
            for (int i = 0; i < tx!.Length && start + i < slot.Length; i++)
                slot[start + i] += amplitude * tx[i];
            var d = Ft8Native.Decode(slot, rate, isFt4: false)
                .FirstOrDefault(x => x.Text.Trim() == message);
            Assert.NotNull(d);
            return d!.SnrDb;
        }

        int loud = SnrAt(0.5f);
        int weak = SnrAt(0.02f);

        Assert.True(loud > weak + 10, $"louder signal must report a higher SNR ({loud} vs {weak})");
        Assert.InRange(weak, -30, 5);       // buried in noise: at or below zero
        Assert.InRange(loud, 0, 40);
    }
}
