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
using System.Text;

namespace Zeus.Contracts;

// Full-state push frame. Variable length:
//   [0x3C][StateDto JSON UTF-8…]
//
// The JSON body is produced with the app's configured web serializer options
// (same shape as GET /api/state), so the SPA parses it with the same code
// path as the 1 Hz poll response. Broadcast coalesced to ~10 Hz while state
// changes; silent when idle. See MsgType.StatePush for rationale.
public readonly record struct StatePushFrame(byte[] JsonUtf8)
{
    public int ByteLength => 1 + JsonUtf8.Length;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.StatePush;
        JsonUtf8.CopyTo(span.Slice(1));
        writer.Advance(ByteLength);
    }

    public static string DeserializeJson(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2)
            throw new InvalidDataException($"StatePushFrame requires ≥2 bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.StatePush)
            throw new InvalidDataException($"expected StatePush (0x{(byte)MsgType.StatePush:X2}), got 0x{bytes[0]:X2}");
        return Encoding.UTF8.GetString(bytes.Slice(1));
    }
}
