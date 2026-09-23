// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Buffers;
using Zeus.Contracts;
using Xunit;

namespace Zeus.Contracts.Tests;

public class RxMetersRxFrameTests
{
    [Fact]
    public void RoundTrips_AllFields_AndReceiverIndex()
    {
        var f = new RxMetersRxFrame(
            ReceiverIndex: 1,
            SignalPk: -73.5f, SignalAv: -80.25f,
            AdcPk: -12.0f, AdcAv: -18.5f,
            AgcGain: 42.0f,
            AgcEnvPk: -60.0f, AgcEnvAv: -66.0f);

        var w = new ArrayBufferWriter<byte>();
        f.Serialize(w);
        Assert.Equal(RxMetersRxFrame.ByteLength, w.WrittenCount);
        Assert.Equal((byte)MsgType.RxMetersRx, w.WrittenSpan[0]);
        Assert.Equal(1, w.WrittenSpan[1]);

        var back = RxMetersRxFrame.Deserialize(w.WrittenSpan);
        Assert.Equal(f, back);
    }

    [Fact]
    public void WireLayout_IsV2PayloadPrefixedByReceiverIndex()
    {
        // The whole point of the frame: same numbers as 0x19 for the same
        // receiver, one byte further along. A client that already decodes
        // 0x19 decodes 0x27 by adding 1 to every offset.
        var v2 = new RxMetersV2Frame(-70f, -75f, -10f, -15f, 30f, -55f, -58f);
        var rx = RxMetersRxFrame.From(2, v2);

        var wv2 = new ArrayBufferWriter<byte>();
        v2.Serialize(wv2);
        var wrx = new ArrayBufferWriter<byte>();
        rx.Serialize(wrx);

        Assert.Equal(RxMetersV2Frame.ByteLength + 1, RxMetersRxFrame.ByteLength);
        Assert.Equal(2, wrx.WrittenSpan[1]);
        Assert.True(wv2.WrittenSpan.Slice(1).SequenceEqual(wrx.WrittenSpan.Slice(2)));
    }

    [Fact]
    public void Deserialize_RejectsShortOrWrongType()
    {
        Assert.Throws<InvalidDataException>(() => RxMetersRxFrame.Deserialize(new byte[10]));
        var bad = new byte[RxMetersRxFrame.ByteLength];
        bad[0] = (byte)MsgType.RxMetersV2;
        Assert.Throws<InvalidDataException>(() => RxMetersRxFrame.Deserialize(bad));
    }

    [Fact]
    public void TypeByte_IsUnused_ByAnyOtherMessage()
    {
        // 0x38-0x3A are reserved, 0x3B/0x3C are taken, 0x40 is the remote
        // tunnel's digital-event byte. 0x27 is upstream's byte for this frame
        // and must stay unique here too.
        Assert.Equal(0x27, (int)MsgType.RxMetersRx);
        var all = Enum.GetValues<MsgType>().Select(m => (byte)m).ToList();
        Assert.Equal(1, all.Count(b => b == 0x27));
    }
}
