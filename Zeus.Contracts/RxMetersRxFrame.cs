// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Buffers;
using System.Buffers.Binary;

namespace Zeus.Contracts;

/// <summary>
/// Per-receiver RX meters (MsgType 0x27). The <see cref="RxMetersV2Frame"/>
/// payload with a receiver index in front of it, so a secondary receiver's
/// S-meter can come from WDSP's own calibrated meter rather than from an
/// estimate off the panadapter bins. Same field meanings and the same
/// calibration rule as 0x19: Signal* and AgcEnv* are calibrated dBm, Adc* is
/// dBFS, AgcGain is dB of insertion gain.
/// </summary>
public readonly record struct RxMetersRxFrame(
    byte ReceiverIndex,
    float SignalPk,
    float SignalAv,
    float AdcPk,
    float AdcAv,
    float AgcGain,
    float AgcEnvPk,
    float AgcEnvAv)
{
    public const int ByteLength = 1 + 1 + 4 * 7;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.RxMetersRx;
        span[1] = ReceiverIndex;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(2, 4), SignalPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(6, 4), SignalAv);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(10, 4), AdcPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(14, 4), AdcAv);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(18, 4), AgcGain);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(22, 4), AgcEnvPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(26, 4), AgcEnvAv);
        writer.Advance(ByteLength);
    }

    public static RxMetersRxFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
            throw new InvalidDataException($"RxMetersRxFrame requires {ByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.RxMetersRx)
            throw new InvalidDataException($"expected RxMetersRx (0x{(byte)MsgType.RxMetersRx:X2}), got 0x{bytes[0]:X2}");
        return new RxMetersRxFrame(
            ReceiverIndex: bytes[1],
            SignalPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(2, 4)),
            SignalAv: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(6, 4)),
            AdcPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(10, 4)),
            AdcAv: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(14, 4)),
            AgcGain: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(18, 4)),
            AgcEnvPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(22, 4)),
            AgcEnvAv: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(26, 4)));
    }

    /// <summary>Build from a V2 frame that already carries calibration.</summary>
    public static RxMetersRxFrame From(byte receiverIndex, in RxMetersV2Frame v2) => new(
        receiverIndex, v2.SignalPk, v2.SignalAv, v2.AdcPk, v2.AdcAv, v2.AgcGain, v2.AgcEnvPk, v2.AgcEnvAv);
}
