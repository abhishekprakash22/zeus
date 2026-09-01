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
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

using System.Buffers;
using Zeus.Contracts;
using Xunit;

namespace Zeus.Contracts.Tests;

public class PsMetersFrameTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var frame = new PsMetersFrame(
            FeedbackLevel: 128.5f,
            CorrectionDb: -42.3f,
            CalState: 7,
            Correcting: true,
            MaxTxEnvelope: 0.7321f,
            Imd3Dbc: -31.5f,
            Imd5Dbc: -44.25f);

        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        Assert.Equal(PsMetersFrame.ByteLength, writer.WrittenCount);
        Assert.Equal(23, writer.WrittenCount);

        var decoded = PsMetersFrame.Deserialize(writer.WrittenSpan);
        Assert.Equal(frame.FeedbackLevel, decoded.FeedbackLevel);
        Assert.Equal(frame.CorrectionDb, decoded.CorrectionDb);
        Assert.Equal(frame.CalState, decoded.CalState);
        Assert.Equal(frame.Correcting, decoded.Correcting);
        Assert.Equal(frame.MaxTxEnvelope, decoded.MaxTxEnvelope);
        Assert.Equal(frame.Imd3Dbc, decoded.Imd3Dbc);
        Assert.Equal(frame.Imd5Dbc, decoded.Imd5Dbc);
    }

    [Fact]
    public void Deserialize_AcceptsLegacyLength_ImdAsNaN()
    {
        // A pre-IMD (15-byte) producer: IMD fields read as NaN, the rest intact.
        var full = new PsMetersFrame(100f, -1f, 6, true, 0.5f, -30f, -40f);
        var writer = new ArrayBufferWriter<byte>();
        full.Serialize(writer);
        var legacy = writer.WrittenSpan.Slice(0, PsMetersFrame.LegacyByteLength);

        var decoded = PsMetersFrame.Deserialize(legacy);
        Assert.Equal(100f, decoded.FeedbackLevel);
        Assert.Equal(6, decoded.CalState);
        Assert.True(float.IsNaN(decoded.Imd3Dbc));
        Assert.True(float.IsNaN(decoded.Imd5Dbc));
    }

    [Fact]
    public void Serialize_WritesMsgTypeFirst()
    {
        var frame = new PsMetersFrame(0f, 0f, 0, false, 0f);
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        Assert.Equal((byte)MsgType.PsMeters, writer.WrittenSpan[0]);
        Assert.Equal(0x18, writer.WrittenSpan[0]);
    }

    [Fact]
    public void Deserialize_RejectsWrongMsgType()
    {
        var bogus = new byte[PsMetersFrame.ByteLength];
        bogus[0] = (byte)MsgType.TxMetersV2;
        Assert.Throws<InvalidDataException>(() => PsMetersFrame.Deserialize(bogus));
    }

    [Fact]
    public void Deserialize_RejectsTruncated()
    {
        var buf = new byte[PsMetersFrame.LegacyByteLength - 1];
        buf[0] = (byte)MsgType.PsMeters;
        Assert.Throws<InvalidDataException>(() => PsMetersFrame.Deserialize(buf));
    }

    [Fact]
    public void Correcting_RoundTripsAsBool()
    {
        var on = new PsMetersFrame(1f, 1f, 1, true, 1f);
        var off = new PsMetersFrame(1f, 1f, 1, false, 1f);

        var w1 = new ArrayBufferWriter<byte>();
        on.Serialize(w1);
        var w2 = new ArrayBufferWriter<byte>();
        off.Serialize(w2);

        Assert.True(PsMetersFrame.Deserialize(w1.WrittenSpan).Correcting);
        Assert.False(PsMetersFrame.Deserialize(w2.WrittenSpan).Correcting);
    }
}
