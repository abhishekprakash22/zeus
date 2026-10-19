// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

using Zeus.Contracts;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Field report: with RX1 and RX2 both on, putting RX1 in FreeDV silenced /
/// garbled RX2. The decoder replaces its block with decoded speech, and it was
/// running on the MIXED bus (RX1 + RX2). It now decodes RX1's own samples
/// before the mix, so RX2 is mixed in afterwards untouched.
/// </summary>
public class DspPipelineFreeDvRx1Tests
{
    private sealed class FakeModem : IAudioModemPlugin
    {
        public const float Decoded = 0.5f;
        public byte LastMode = 255;
        public readonly List<float[]> Inputs = new();
        public bool Active => LastMode == (byte)RxMode.FreeDv;
        public bool NativeAvailable => true;
        public void SyncMode(byte rxModeByte) => LastMode = rxModeByte;

        public void ProcessRx(Span<float> block48k)
        {
            Inputs.Add(block48k.ToArray());
            block48k.Fill(Decoded);
        }

        public void ProcessTx(Span<float> block48k) { }
        public void FlushRx() { }
        public void FlushTx() { }
        public int FinishTx() => 0;
        public int DrainTx(Span<float> block48k) => 0;
    }

    [Fact]
    public void FreeDV_on_RX1_leaves_RX2_audible_in_the_mix()
    {
        var modem = new FakeModem();
        var rx1 = Enumerable.Repeat(0.1f, 64).ToArray();   // RX1: FreeDV modem tones
        var rx2 = Enumerable.Repeat(0.8f, 64).ToArray();   // RX2: an SSB station

        // Pipeline order: decode RX1's own block, then mix the secondaries in.
        bool decoded = DspPipelineService.DecodeFreeDvOnRx1(modem, RxMode.FreeDv, rx1, publishingNormalRxAudio: true);
        int n = DspPipelineService.MixRxAudioN(rx1, 64, new[] { new DspPipelineService.RxAudioSlice(rx2, 64) });

        Assert.True(decoded);
        Assert.Equal(64, n);
        // RX2 is present at its normal mix level alongside the decoded speech…
        Assert.All(rx1, s => Assert.Equal((FakeModem.Decoded + 0.8f) / 2f, s, 5));
        // …and the FreeDV demodulator never saw RX2's audio.
        Assert.Single(modem.Inputs);
        Assert.All(modem.Inputs[0], s => Assert.Equal(0.1f, s, 5));
    }

    [Fact]
    public void Not_in_FreeDV_the_block_passes_through_untouched()
    {
        var modem = new FakeModem();
        var rx1 = Enumerable.Repeat(0.3f, 32).ToArray();
        Assert.False(DspPipelineService.DecodeFreeDvOnRx1(modem, RxMode.USB, rx1, publishingNormalRxAudio: true));
        Assert.Equal((byte)RxMode.USB, modem.LastMode);
        Assert.Empty(modem.Inputs);
        Assert.All(rx1, s => Assert.Equal(0.3f, s, 5));
    }

    [Fact]
    public void While_not_publishing_RX_audio_the_mode_still_syncs_but_nothing_is_decoded()
    {
        var modem = new FakeModem();
        var rx1 = Enumerable.Repeat(0.2f, 16).ToArray();
        Assert.False(DspPipelineService.DecodeFreeDvOnRx1(modem, RxMode.FreeDv, rx1, publishingNormalRxAudio: false));
        Assert.Equal((byte)RxMode.FreeDv, modem.LastMode);
        Assert.Empty(modem.Inputs);
    }

    [Fact]
    public void No_modem_and_empty_blocks_are_no_ops()
    {
        Assert.False(DspPipelineService.DecodeFreeDvOnRx1(null, RxMode.FreeDv, new float[8], true));
        var modem = new FakeModem();
        Assert.False(DspPipelineService.DecodeFreeDvOnRx1(modem, RxMode.FreeDv, Span<float>.Empty, true));
        Assert.Empty(modem.Inputs);
    }
}
