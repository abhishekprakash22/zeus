// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

using Zeus.Contracts;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// FreeDV owner model: the one shared modem decodes whichever enabled receiver
/// is in FreeDV — RX2 alone
/// now decodes (field report on 1.145: RX2 in FreeDV sounded like raw LSB).
/// TX encodes FreeDV only while RX1 (whose mode drives the TXA modulator) is in
/// FreeDV, so an RX2 owner can never turn an RX1 USB over into modem tones.
/// </summary>
public class DspPipelineFreeDvOwnerTests
{
    private static StateDto State(RxMode rx1, RxMode rx2, bool rx2Enabled = true) => new(
        Status: ConnectionStatus.Connected,
        Endpoint: "192.168.1.25:1024",
        VfoHz: 7_177_000,
        Mode: rx1,
        FilterLowHz: 300,
        FilterHighHz: 2700,
        SampleRate: 384_000,
        RadioLoHz: 7_177_000,
        Rx2Enabled: rx2Enabled,
        Receivers: new ReceiverDto[]
        {
            new(Index: 0, Enabled: true, AdcSource: 0, VfoHz: 7_177_000, Mode: rx1,
                FilterLowHz: 300, FilterHighHz: 2700, FilterPresetName: null, AfGainDb: 0,
                SampleRateHz: 384_000, Muted: false),
            new(Index: 1, Enabled: rx2Enabled, AdcSource: 0, VfoHz: 7_190_000, Mode: rx2,
                FilterLowHz: 300, FilterHighHz: 2700, FilterPresetName: null, AfGainDb: 0,
                SampleRateHz: 384_000, Muted: false),
        });

    private sealed class FakeModem : IAudioModemPlugin
    {
        public byte LastMode = 255;
        public int Flushes;
        public bool Active => LastMode == (byte)RxMode.FreeDv;
        public bool NativeAvailable => true;
        public void SyncMode(byte rxModeByte) => LastMode = rxModeByte;
        public void ProcessRx(Span<float> block48k) => block48k.Fill(0.5f);
        public void ProcessTx(Span<float> block48k) { }
        public void FlushRx() => Flushes++;
        public void FlushTx() { }
        public int FinishTx() => 0;
        public int DrainTx(Span<float> block48k) => 0;
    }

    [Theory]
    [InlineData(RxMode.USB, RxMode.USB, -1)]
    [InlineData(RxMode.FreeDv, RxMode.USB, 0)]
    [InlineData(RxMode.USB, RxMode.FreeDv, 1)]      // the 1.145 report: RX2 alone now owns it
    [InlineData(RxMode.FreeDv, RxMode.FreeDv, 0)]   // no current owner: lowest index
    public void The_owner_is_the_FreeDV_receiver(RxMode rx1, RxMode rx2, int expected)
    {
        Assert.Equal(expected, DspPipelineService.SelectFreeDvOwner(State(rx1, rx2), currentOwner: -1));
    }

    [Fact]
    public void A_receiver_entering_FreeDV_later_never_takes_the_decoder()
    {
        // RX2 owns it; RX1 then goes to FreeDV as well — RX2 keeps it.
        Assert.Equal(1, DspPipelineService.SelectFreeDvOwner(State(RxMode.FreeDv, RxMode.FreeDv), currentOwner: 1));
        // The owner leaves FreeDV — the other FreeDV receiver takes over.
        Assert.Equal(0, DspPipelineService.SelectFreeDvOwner(State(RxMode.FreeDv, RxMode.USB), currentOwner: 1));
    }

    [Fact]
    public void A_switched_off_RX2_never_owns_the_modem()
    {
        Assert.Equal(-1, DspPipelineService.SelectFreeDvOwner(State(RxMode.USB, RxMode.FreeDv, rx2Enabled: false), -1));
    }

    [Fact]
    public void Engaging_follows_the_owner_and_a_move_flushes_the_modem()
    {
        var modem = new FakeModem();
        int owner = DspPipelineService.EngageFreeDvOwner(modem, State(RxMode.USB, RxMode.FreeDv), -1);
        Assert.Equal(1, owner);
        Assert.True(modem.Active);
        Assert.Equal(0, modem.Flushes);       // first acquire: no flush needed

        // RX2 leaves FreeDV while RX1 enters it: ownership moves → flush once.
        owner = DspPipelineService.EngageFreeDvOwner(modem, State(RxMode.FreeDv, RxMode.USB), owner);
        Assert.Equal(0, owner);
        Assert.Equal(1, modem.Flushes);

        // Nobody in FreeDV: disengaged.
        owner = DspPipelineService.EngageFreeDvOwner(modem, State(RxMode.USB, RxMode.USB), owner);
        Assert.Equal(-1, owner);
        Assert.False(modem.Active);
    }

    [Fact]
    public void RX2s_own_block_is_decoded_before_the_mix_and_RX1_is_untouched()
    {
        var modem = new FakeModem();
        DspPipelineService.EngageFreeDvOwner(modem, State(RxMode.USB, RxMode.FreeDv), -1);
        var rx1 = Enumerable.Repeat(0.2f, 16).ToArray();   // RX1: an SSB station
        var rx2 = Enumerable.Repeat(0.1f, 16).ToArray();   // RX2: FreeDV modem tones

        Assert.True(DspPipelineService.DecodeFreeDvBlock(modem, rx2, publishingNormalRxAudio: true));
        int n = DspPipelineService.MixRxAudioN(rx1, 16, new[] { new DspPipelineService.RxAudioSlice(rx2, 16) });

        Assert.Equal(16, n);
        Assert.All(rx1, s => Assert.Equal((0.2f + 0.5f) / 2f, s, 5));   // RX1 audio + RX2 decoded speech
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]   // engaged for an RX2 owner, RX1 transmitting USB: NO FreeDV on air
    [InlineData(false, true, false)]
    public void TX_encodes_FreeDV_only_when_the_transmit_mode_is_FreeDV(bool modemActive, bool txIsFreeDv, bool expected)
    {
        Assert.Equal(expected, DspPipelineService.FreeDvTxEngaged(modemActive, txIsFreeDv));
    }
}
