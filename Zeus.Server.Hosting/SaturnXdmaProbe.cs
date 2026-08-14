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
    /// <summary>Field-debuggable probe: every step recorded, nothing
    /// silent. GET /api/system/xdma returns this — one curl replaces
    /// guesswork on a CM5 that "should" work (first field case: fresh CM5
    /// on a Saturn, no detection — the silent-null design hid whether the
    /// node, the permissions, or the registers were at fault).</summary>
    public object ProbeDiagnostics()
    {
        var steps = new List<string>();
        if (!OperatingSystem.IsLinux())
            return new { detected = false, steps = new[] { "not Linux — XDMA is Linux-only" } };
        steps.Add($"node {UserDev}: " + (File.Exists(UserDev) ? "EXISTS" : "MISSING — xdma driver not loaded? (Saturn repo: scripts/build-xdma.sh installs linuxdriver/xdma + udev rules)"));
        if (!File.Exists(UserDev))
        {
            string others = string.Join(", ", Directory.GetFiles("/dev").Where(f => f.Contains("xdma")).DefaultIfEmpty("none"));
            steps.Add($"other /dev/xdma* nodes: {others}");
            return new { detected = false, steps };
        }
        FileStream? fs = null;
        try
        {
            try
            {
                fs = new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                steps.Add("open: read-write OK (piHPSDR parity)");
            }
            catch (Exception exRw)
            {
                steps.Add($"open read-write FAILED: {exRw.Message} — udev rules installed? (sudo cp Saturn/etc/udev/rules.d/* /etc/udev/rules.d && reboot)");
                fs = new FileStream(UserDev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                steps.Add("open: read-only fallback OK");
            }
            uint sw = ReadReg(fs, SwVersionReg);
            uint prod = ReadReg(fs, ProdVersionReg);
            uint user = ReadReg(fs, UserVersionReg);
            steps.Add($"regs (libc pread): sw=0x{sw:X8} prod=0x{prod:X8} user=0x{user:X8}" + (sw == 0 && prod == 0 ? " — all zero: pread returned nothing (link down? FPGA unconfigured?)" : ""));
            uint prodId = (prod >> 16) & 0xFFFF;
            uint swId = (sw >> 20) & 0x1F;
            uint minor = (sw >> 4) & 0xFFFF;
            bool clocksOk = (sw & 0xF) == 0xF;
            steps.Add($"identity: ProdID={prodId} (need {SaturnProductId}), SWID={swId} (golden={GoldenConfigId}/primary={PrimaryConfigId}), fwMinor={minor}, clocks={(sw & 0xF):X} ({(clocksOk ? "all present" : "MISSING — check 122.88/10/125 MHz sources")})");
            bool detected = prodId == SaturnProductId && (swId is GoldenConfigId or PrimaryConfigId) && clocksOk;
            steps.Add(detected ? "VERDICT: Saturn detected" : "VERDICT: node present but identity checks failed — see above");
            return new { detected, steps };
        }
        catch (Exception ex)
        {
            steps.Add($"FAILED: {ex.GetType().Name}: {ex.Message}");
            return new { detected = false, steps };
        }
        finally { fs?.Dispose(); }
    }

    public SaturnXdmaInfo? Probe()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            if (!File.Exists(UserDev)) return null;
            using var fs = OpenUser();
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

    private static FileStream OpenUser()
    {
        // piHPSDR opens O_RDWR; some xdma builds refuse read-only opens.
        try { return new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); }
        catch { return new FileStream(UserDev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
    }

    private static uint ReadReg(FileStream fs, long offset)
        // libc pread — RandomAccess refuses char devices by file type
        // (the first field diagnosis; see XdmaIo).
        => XdmaIo.Read32(fs.SafeFileHandle, offset);
}
