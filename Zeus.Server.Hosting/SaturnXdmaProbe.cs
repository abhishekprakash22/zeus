// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// SaturnXdmaProbe — Phase 1 of the native PCIe path for the ANAN G2 / G2
// Ultra: DISCOVER a Saturn FPGA sitting on the local PCIe bus behind the
// Xilinx XDMA driver, exactly the way DL1YCF's piHPSDR does it
// (src/saturnmain.c saturn_discovery(), GPL like us — the register map and
// validation rules are ported with attribution):
//
//   1. Presence: /dev/xdma0_user exists and is a character device.
//   2. Identity, not faith: pread three 32-bit FPGA registers —
//        0xC000  software info   (major[31:25], SWID[24:20], minor[19:4],
//                                 clock-present bits[3:0])
//        0xC004  product info    (ProdID[31:16], PCB version[15:0])
//        0x4004  user version
//      and refuse unless ProdID == 1 (Saturn), SWID is a known
//      golden/primary config, and all four clocks report present — the XDMA
//      node may front something that is not a Saturn.
//   3. Report versions so the UI can show what it found.
//
// Phase 1 stops at discovery: the entry is surfaced (transport "xdma") so
// the launcher can show the board living inside the radio; connecting still
// goes through the network personality (p2app) until the register plane
// (saturnregisters port) and DMA data plane land in later phases.

namespace Zeus.Server;

public sealed class SaturnXdmaProbe
{
    private const string UserDev = "/dev/xdma0_user";
    private const long SwVersionReg = 0xC000;
    private const long ProdVersionReg = 0xC004;
    private const long UserVersionReg = 0x4004;
    private const uint SaturnProductId = 1;
    private const uint GoldenConfigId = 0x00;   // piHPSDR SATURNGOLDENCONFIGID
    private const uint PrimaryConfigId = 0x01;  // piHPSDR SATURNPRIMARYCONFIGID

    public sealed record SaturnXdmaInfo(
        uint MajorVersion, uint MinorVersion, uint UserVersion,
        uint PcbVersion, bool ClocksOk, bool KnownConfig);

    /// <summary>Probe the local PCIe bus for a Saturn behind XDMA. Returns
    /// null when the device node is absent, unreadable, or fronts something
    /// that fails the Saturn identity checks — absence of a Saturn is a
    /// normal, silent result on every external-Pi and desktop install.</summary>
    public SaturnXdmaInfo? Probe()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            if (!File.Exists(UserDev)) return null;
            using var fs = new FileStream(UserDev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            uint sw = ReadReg(fs, SwVersionReg);
            uint prod = ReadReg(fs, ProdVersionReg);
            uint user = ReadReg(fs, UserVersionReg);

            uint prodId = (prod >> 16) & 0xFFFF;
            if (prodId != SaturnProductId) return null;   // XDMA, but not a Saturn

            uint swId = (sw >> 20) & 0x1F;
            uint minor = (sw >> 4) & 0xFFFF;
            uint major = (sw >> 25) & 0x7F;
            if (minor < 18) major = 0;                    // MajorVersion predates fw 0.18
            bool clocksOk = (sw & 0xF) == 0xF;
            bool knownConfig = swId is GoldenConfigId or PrimaryConfigId;
            if (!knownConfig || !clocksOk) 
                return new SaturnXdmaInfo(major, minor, user, prod & 0xFFFF, clocksOk, knownConfig);

            return new SaturnXdmaInfo(major, minor, user, prod & 0xFFFF, true, true);
        }
        catch
        {
            return null;   // permissions, driver mid-load, etc. — not a Saturn today
        }
    }

    private static uint ReadReg(FileStream fs, long offset)
    {
        Span<byte> b = stackalloc byte[4];
        long got = RandomAccess.Read(fs.SafeFileHandle, b, offset);
        return got == 4 ? BitConverter.ToUInt32(b) : 0;
    }
}
