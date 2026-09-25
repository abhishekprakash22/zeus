// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Xunit;
using Zeus.Contracts;

namespace Zeus.Protocol2.Tests;

/// <summary>
/// Diversity on Protocol 2 is the gateware-synchronised DDC0/DDC1 pair: byte
/// 1363 = 0x02 phase-locks DDC1 to DDC0 and the radio delivers ADC0 and ADC1
/// samples interleaved in DDC0's packets. These pin the two contracts that
/// make a null holdable — what we ask the radio for, and how we take the
/// pair apart when it arrives.
/// </summary>
public class DiversityPairTests
{
    [Theory]
    [InlineData(true,  false, false, HpsdrBoardKind.OrionMkII, 2, true)]   // on, dual ADC, PS off
    [InlineData(true,  true,  false, HpsdrBoardKind.OrionMkII, 2, true)]   // on, PS armed but NOT keyed → still ours
    [InlineData(true,  true,  true,  HpsdrBoardKind.OrionMkII, 2, false)]  // on, PS keyed → pair yields to feedback
    [InlineData(true,  false, false, HpsdrBoardKind.OrionMkII, 1, false)]  // single ADC → nothing to pair with
    [InlineData(false, false, false, HpsdrBoardKind.OrionMkII, 2, false)]  // off
    [InlineData(true,  false, false, HpsdrBoardKind.Hermes,    2, false)]  // board does not reserve DDC0/1
    public void UsesDiversityPair_TruthTable(bool enabled, bool ps, bool keyed, HpsdrBoardKind board, int numAdc, bool expect)
    {
        Assert.Equal(expect, Protocol2Client.UsesDiversityPair(enabled, ps, keyed, board, (byte)numAdc));
    }

    [Fact]
    public void Composer_WithPair_SyncsDdc1ToDdc0_AndSourcesAdc1()
    {
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 2, sampleRateKhz: 48, psEnabled: false,
            boardKind: HpsdrBoardKind.OrionMkII, rx2Enabled: true,
            diversitySourceEnabled: true, txKeyed: false, diversitySourceAdcSource: 1);

        Assert.Equal((byte)0x02, p[1363]);         // DDC1 → DDC0 sync
        Assert.Equal((byte)0x01, (byte)(p[7] & 0x01)); // DDC0 enabled
        Assert.Equal((byte)0x00, (byte)(p[7] & 0x02)); // DDC1 not a separate stream
        Assert.Equal((byte)0, p[17 + 0 * 6]);      // DDC0 ← ADC0
        Assert.Equal((byte)1, p[17 + 1 * 6]);      // DDC1 ← ADC1 (the second antenna)
    }

    [Fact]
    public void Composer_WithPair_ButPsKeyed_LeavesPairToFeedback()
    {
        var withPair = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 2, sampleRateKhz: 48, psEnabled: true,
            boardKind: HpsdrBoardKind.OrionMkII,
            diversitySourceEnabled: true, txKeyed: true, diversitySourceAdcSource: 1);
        var psOnly = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 2, sampleRateKhz: 48, psEnabled: true,
            boardKind: HpsdrBoardKind.OrionMkII,
            diversitySourceEnabled: false, txKeyed: true);
        // Keyed with PS, diversity must not touch what PS configured.
        Assert.Equal(psOnly, withPair);
    }

    [Fact]
    public void Composer_PairIsIdenticalOnBothComposers()
    {
        var legacy = Protocol2Client.ComposeCmdRxBuffer(
            seq: 5, numAdc: 2, sampleRateKhz: 48, psEnabled: false,
            boardKind: HpsdrBoardKind.OrionMkII, rx2Enabled: true,
            diversitySourceEnabled: true, diversitySourceAdcSource: 1);
        var generalized = Protocol2Client.ComposeCmdRxBufferForReceivers(
            seq: 5, numAdc: 2,
            receivers: new[] { new Protocol2Client.DdcReceiverSpec(0, 48), new Protocol2Client.DdcReceiverSpec(0, 48) },
            psEnabled: false, boardKind: HpsdrBoardKind.OrionMkII,
            diversitySourceEnabled: true, diversitySourceAdcSource: 1);
        Assert.Equal(legacy[1363], generalized[1363]);
        Assert.Equal(legacy[17 + 6], generalized[17 + 6]);   // DDC1 ADC byte
    }

    [Fact]
    public void HighPriority_PairActive_TunesDdc0AndDdc1ToRx1()
    {
        // The field fault: pair configured, DDC0/1 left at 0 Hz — RX1 vanished
        // and a carrier stood at the DDC centre. Both phase words must be RX1's.
        var hp = new byte[1444];
        uint rxPhase = 0x12345678;
        Protocol2Client.ApplyDiversityPairTuning(hp, pairActive: true, rxPhase);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, hp[9..13]);    // DDC0
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, hp[13..17]);   // DDC1
    }

    [Fact]
    public void HighPriority_PairInactive_LeavesDdc0AndDdc1Alone()
    {
        var hp = new byte[1444];
        hp[9] = 0xAA; hp[13] = 0xBB;   // e.g. PS feedback's TX phase
        Protocol2Client.ApplyDiversityPairTuning(hp, pairActive: false, 0x12345678);
        Assert.Equal(0xAA, hp[9]);
        Assert.Equal(0xBB, hp[13]);
    }

    [Fact]
    public void ConfigureSynchronizedDiversityPair_WritesExactBytes()
    {
        var p = new byte[1444];
        p[7] = 0x07;   // DDC0..2 previously enabled
        Protocol2Client.ConfigureSynchronizedDiversityPair(p, HpsdrBoardKind.OrionMkII, 48, sourceAdc: 1);
        Assert.Equal((byte)0x02, p[1363]);
        Assert.True((p[7] & 0x01) != 0);                          // DDC0 on
        Assert.True((p[7] & 0x02) == 0);                          // DDC1 off as a stream (sync owns it)
        Assert.True((p[7] & (1 << Protocol2Client.RxBaseDdc(HpsdrBoardKind.OrionMkII))) == 0); // base RX DDC off — redundant with the pair
        Assert.Equal((byte)1, p[17 + 6]);                         // DDC1 ← ADC1
        Assert.Equal((byte)0, p[17]);                             // DDC0 ← ADC0
    }
}
