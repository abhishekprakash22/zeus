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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers;
using System.Buffers.Binary;

namespace Zeus.Contracts;

// VFO state push frame. 17 bytes:
//   [0x3B][vfoAHz:i64 LE][vfoBHz:i64 LE]
//
// Broadcast (coalesced to ~30 Hz with a guaranteed trailing send) whenever
// either VFO changes from ANY source — the G2 front-panel knob above all, but
// also CAT, TCI, and remote clients. Rationale: the web UI otherwise learns
// non-self-originated state only from the 1 Hz App.tsx poll, so panel tuning
// showed the VFO numerals stepping once a second while the spectrum (which
// stamps its own CenterHz) scrolled live underneath. piHPSDR redraws the dial
// per step in-process; this frame is the wire equivalent. Same edge-push
// pattern as MoxStateFrame (0x1C).
public readonly record struct VfoStateFrame(long VfoAHz, long VfoBHz)
{
    public const int ByteLength = 17;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.VfoState;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(1), VfoAHz);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(9), VfoBHz);
        writer.Advance(ByteLength);
    }

    public static VfoStateFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
            throw new InvalidDataException($"VfoStateFrame requires {ByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.VfoState)
            throw new InvalidDataException($"expected VfoState (0x{(byte)MsgType.VfoState:X2}), got 0x{bytes[0]:X2}");
        return new VfoStateFrame(
            VfoAHz: BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(1)),
            VfoBHz: BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(9)));
    }
}
