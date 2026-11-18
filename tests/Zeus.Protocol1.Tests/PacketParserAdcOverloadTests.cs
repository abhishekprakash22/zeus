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

using Xunit;

namespace Zeus.Protocol1.Tests;

/// <summary>
/// The EP6 C&amp;C echo carries ADC-overload bits only in the C0=0x00 status
/// slot (C1[0] = ADC0) and the C0=0x20 slot (C1[0] = ADC0, C2[0] = ADC1);
/// Thetis networkproto1.c reads them nowhere else. The AIN slots (0x08 /
/// 0x10 / 0x18) carry 16-bit power / temperature words in C1..C4 whose low
/// bit toggles with the reading — treating it as an overload flag fed a
/// stream of phantom overloads to auto-ATT and railed it at 31 dB.
/// </summary>
public class PacketParserAdcOverloadTests
{
    public enum Layout { OneDdc, TwoDdc, Hl2Ps4Ddc }

    public static TheoryData<Layout> Layouts => new() { Layout.OneDdc, Layout.TwoDdc, Layout.Hl2Ps4Ddc };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void AinSlots_LowBitsSet_ReportNoOverload(Layout layout)
    {
        foreach (byte c0 in new byte[] { 0x08, 0x10, 0x18 })
        {
            byte[] packet = BuildPacket();
            SetEcho(packet, frame: 0, c0, c1: 0x0F, c2: 0x4B);
            SetEcho(packet, frame: 1, c0, c1: 0x01, c2: 0xFF);

            Assert.Equal(0, Parse(layout, packet));
        }
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void StatusSlot0_C1Bit0_ReportsAdc0Only(Layout layout)
    {
        byte[] packet = BuildPacket();
        // C2 in the 0x00 slot is firmware / status data, not an ADC1 flag.
        SetEcho(packet, frame: 1, c0: 0x00, c1: 0x01, c2: 0x4B);

        Assert.Equal(0b01, Parse(layout, packet));
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void StatusSlot0_MoxEchoBitSet_StillReportsAdc0(Layout layout)
    {
        byte[] packet = BuildPacket();
        SetEcho(packet, frame: 0, c0: 0x01, c1: 0x01, c2: 0x00);

        Assert.Equal(0b01, Parse(layout, packet));
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void OverloadSlot0x20_ReportsBothAdcs(Layout layout)
    {
        byte[] packet = BuildPacket();
        SetEcho(packet, frame: 0, c0: 0x20, c1: 0x01, c2: 0x00);
        SetEcho(packet, frame: 1, c0: 0x20, c1: 0x00, c2: 0x01);

        Assert.Equal(0b11, Parse(layout, packet));
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Hl2AckResponse_ReportsNoOverload(Layout layout)
    {
        // HL2 RQST/ACK replies set C0[7]; their C1..C4 are register data.
        byte[] packet = BuildPacket();
        SetEcho(packet, frame: 0, c0: 0x80, c1: 0x01, c2: 0x01);

        Assert.Equal(0, Parse(layout, packet));
    }

    private static byte Parse(Layout layout, byte[] packet)
    {
        bool ok;
        byte bits;
        switch (layout)
        {
            case Layout.OneDdc:
                ok = PacketParser.TryParsePacket(packet, new double[2 * PacketParser.ComplexSamplesPerPacket],
                    out _, out _, out _, out _, out bits);
                break;
            case Layout.TwoDdc:
            {
                int n = 2 * PacketParser.TwoDdcSamplesPerPacket;
                ok = PacketParser.TryParse2DdcPacket(packet, new double[n], new double[n],
                    out _, out _, out _, out _, out bits);
                break;
            }
            default:
            {
                int n = 2 * PacketParser.Hl2Ps4DdcSamplesPerPacket;
                ok = PacketParser.TryParseHl2Ps4DdcPacket(packet,
                    new double[n], new double[n], new double[n], new double[n],
                    out _, out _, out _, out _, out bits);
                break;
            }
        }
        Assert.True(ok);
        return bits;
    }

    private static byte[] BuildPacket()
    {
        var packet = new byte[PacketParser.PacketLength];
        packet[0] = 0xEF;
        packet[1] = 0xFE;
        packet[2] = 0x01;
        packet[3] = 0x06;
        for (int f = 0; f < 2; f++)
        {
            int frameStart = 8 + f * 512;
            packet[frameStart + 0] = 0x7F;
            packet[frameStart + 1] = 0x7F;
            packet[frameStart + 2] = 0x7F;
            // Default every frame to an AIN slot with zero data so a frame the
            // test doesn't touch can't contribute an overload by itself.
            packet[frameStart + 3] = 0x08;
        }
        return packet;
    }

    private static void SetEcho(byte[] packet, int frame, byte c0, byte c1, byte c2)
    {
        int frameStart = 8 + frame * 512;
        packet[frameStart + 3] = c0;
        packet[frameStart + 4] = c1;
        packet[frameStart + 5] = c2;
    }
}
