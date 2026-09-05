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
using System.Buffers.Binary;

namespace Zeus.Contracts;

// PureSignal stage meters. 31 bytes total:
//
//   [0x18] [feedbackLevel:f32] [correctionDb:f32]
//          [calState:u8] [correcting:u8]
//          [maxTxEnvelope:f32]
//          [imd3Dbc:f32] [imd5Dbc:f32]
//          [calFits:i32] [calAttempts:i32]
//
// Imd3Dbc / Imd5Dbc — two-tone intermodulation measured LIVE from the TX
//                     panadapter bins (the post-PA feedback spectrum when
//                     PureSignal is armed): worst of the 2f1−f2 / 2f2−f1
//                     (resp. 3f1−2f2 / 3f2−2f1) products relative to the mean
//                     tone level, in dBc (negative; −30 means products 30 dB
//                     below the tones). NaN when not measurable (two-tone
//                     off, tones not found, bins too coarse). Readers that
//                     receive the older 19-byte frame treat both as NaN.
//
// FeedbackLevel — WDSP GetPSInfo info[4], 0..256 raw (UI normalises to 0..1).
// CorrectionDb — derived correction-depth in dB (RMS of the recent calcc
//                output curve). Zero when not correcting.
// CalState — info[15] enum: 0 RESET, 1 WAIT, 2 MOXDELAY, 3 SETUP, 4 COLLECT,
//            5 MOXCHECK, 6 CALC, 7 DELAY, 8 STAYON, 9 TURNON.
// Correcting — info[14] != 0; non-zero means the iqc stage has a curve loaded
//              and is actively predistorting.
// MaxTxEnvelope — GetPSMaxTX(out double maxtx); the highest TX envelope
//                 magnitude seen since last PS reset. Used by the auto-attenuate
//                 control loop.
//
// Bare-payload like TxMetersV2Frame (0x16) — no 16-byte WireFormat header.
// Server only emits this when PsEnabled is true so idle wire stays quiet.
public readonly record struct PsMetersFrame(
    float FeedbackLevel,
    float CorrectionDb,
    byte CalState,
    bool Correcting,
    float MaxTxEnvelope,
    float Imd3Dbc = float.NaN,
    float Imd5Dbc = float.NaN,
    int CalFits = 0,
    int CalAttempts = 0)
{
    // CalFits — GetPSInfo info[5]: ACCEPTED calibration fits (scheck passed;
    //           the count Thetis gates auto-attenuate on).
    // CalAttempts — info[7] (new in PS3): fits STARTED. attempts − fits =
    //           rejections; a widening gap with fits frozen means calcc keeps
    //           refusing this chain. Appended fields — readers of the older
    //           23-byte frame see zeros.
    public const int ByteLength = 1 + 4 + 4 + 1 + 1 + 4 + 4 + 4 + 4 + 4;
    /// <summary>Pre-IMD layout; still accepted on read.</summary>
    public const int LegacyByteLength = 1 + 4 + 4 + 1 + 1 + 4;
    /// <summary>IMD layout without the fit counters; still accepted on read.</summary>
    public const int PreCountersByteLength = 1 + 4 + 4 + 1 + 1 + 4 + 4 + 4;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.PsMeters;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(1, 4), FeedbackLevel);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(5, 4), CorrectionDb);
        span[9] = CalState;
        span[10] = Correcting ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(11, 4), MaxTxEnvelope);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(15, 4), Imd3Dbc);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(19, 4), Imd5Dbc);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(23, 4), CalFits);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(27, 4), CalAttempts);
        writer.Advance(ByteLength);
    }

    public static PsMetersFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < LegacyByteLength)
            throw new InvalidDataException($"PsMetersFrame requires {LegacyByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.PsMeters)
            throw new InvalidDataException($"expected PsMeters (0x{(byte)MsgType.PsMeters:X2}), got 0x{bytes[0]:X2}");
        return new PsMetersFrame(
            FeedbackLevel: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(1, 4)),
            CorrectionDb: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(5, 4)),
            CalState: bytes[9],
            Correcting: bytes[10] != 0,
            MaxTxEnvelope: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(11, 4)),
            Imd3Dbc: bytes.Length >= PreCountersByteLength ? BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(15, 4)) : float.NaN,
            Imd5Dbc: bytes.Length >= PreCountersByteLength ? BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(19, 4)) : float.NaN,
            CalFits: bytes.Length >= ByteLength ? BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(23, 4)) : 0,
            CalAttempts: bytes.Length >= ByteLength ? BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(27, 4)) : 0);
    }
}
