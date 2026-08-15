// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// SaturnControl — PHASE 2 of the XDMA roadmap opens here: the register
// plane. A C# port of the essential subset of Laurence Barker G8NJJ's
// saturnregisters.c (Saturn/sw_projects/common, GPL like us) — the
// complete P2-semantics-to-register mapping that p2app and piHPSDR use.
// Registers live at plain offsets in the /dev/xdma0_user BAR, accessed
// via XdmaIo's libc pread/pwrite (the commit-122 lesson).
//
// Two halves, two disciplines:
//
// READ PLANE (safe beside p2app — reads contend with nobody): the Saturn
// status register at 0x4000 carries the front-panel truth: PTT input
// (bit 0), keyer dot (bit 2, KEYINA), keyer dash (bit 3, KEYINB), PLL
// locked (bit 10), CW key-down ramp (bit 11). GET /api/xdma/status
// decodes it live — press the paddle in the key jack and watch the bits
// flip in a curl loop. This is Phase 2's first working circuit.
//
// WRITE PLANE (Phase 3's verbs, shipped early and GATED): DDC frequency
// (delta phase = 2^32 * f / 122.88 MHz, written only on change, exactly
// as the C does) and friends. Writes CONTEND with p2app — two masters on
// one register file is operator error — so the test endpoint refuses
// unless the caller states p2app is stopped, refuses while TX is keyed,
// and refuses without a validated local Saturn. When Phase 3 wires this
// into the transport seam, the gate becomes the transport selection
// itself.

using Zeus.Contracts;

namespace Zeus.Server;

public sealed class SaturnControl
{
    private const string UserDev = "/dev/xdma0_user";

    // ---- saturnregisters.h address map (ported subset) ----
    private static readonly long[] DdcFreqReg =
        { 0x0, 0x4, 0x8, 0xC, 0x10, 0x14, 0x18, 0x1C, 0x1000, 0x1004 };
    private const long StatusReg = 0x4000;          // VADDRSTATUSREG
    private const long TxDucReg = 0x200C;           // VADDRTXDUCREG (DUC delta phase)

    private const double SampleRateHz = 122_880_000.0;  // VSAMPLERATE
    private const double TwoExp32 = 4294967296.0;       // VTWOEXP32

    // Status register bits (saturnregisters.c)
    private const int BitPtt = 0;
    private const int BitDot = 2;                   // VKEYINA
    private const int BitDash = 3;                  // VKEYINB
    private const int BitPllLocked = 10;            // VPLLLOCKED
    private const int BitCwKeyDown = 11;            // VCWKEYDOWN (ramp output)

    private readonly SaturnXdmaProbe _probe;
    private readonly TxService _tx;
    private readonly ILogger<SaturnControl> _log;

    // Write-on-change cache, exactly the C's DDCDeltaPhase[] discipline.
    private readonly uint?[] _ddcDeltaPhase = new uint?[10];
    private uint? _ducDeltaPhase;
    private readonly object _lock = new();

    public SaturnControl(SaturnXdmaProbe probe, TxService tx, ILogger<SaturnControl> log)
    {
        _probe = probe;
        _tx = tx;
        _log = log;
    }

    public bool SaturnPresent => OperatingSystem.IsLinux() && _probe.Probe() is not null;

    /// <summary>Read plane: the status register, decoded. Safe to call at
    /// any time, including while p2app owns the radio — reads race nobody.
    /// </summary>
    public object ReadStatus()
    {
        if (!SaturnPresent)
            return new { ok = false, error = "no Saturn on the local PCIe bus" };
        using var fs = new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        uint status = XdmaIo.Read32(fs.SafeFileHandle, StatusReg);
        return new
        {
            ok = true,
            raw = $"0x{status:X8}",
            pttIn = ((status >> BitPtt) & 1) != 0,
            keyerDotIn = ((status >> BitDot) & 1) != 0,
            keyerDashIn = ((status >> BitDash) & 1) != 0,
            pllLocked = ((status >> BitPllLocked) & 1) != 0,
            cwKeyDown = ((status >> BitCwKeyDown) & 1) != 0,
        };
    }

    /// <summary>Write plane (gated at the endpoint): DDC frequency, the C's
    /// exact math — delta phase = 2^32 · f / 122.88 MHz, written only on
    /// change.</summary>
    public object SetDdcFrequency(int ddc, long hz, out string? refusal)
    {
        refusal = null;
        if (!SaturnPresent) { refusal = "no Saturn on the local PCIe bus"; return new { ok = false }; }
        if (_tx.MoxOwner is not null) { refusal = "TX is keyed — no register experiments under power"; return new { ok = false }; }
        if (ddc is < 0 or > 9) { refusal = "ddc must be 0..9"; return new { ok = false }; }
        if (hz is < 0 or > 61_440_000) { refusal = "frequency out of Nyquist range"; return new { ok = false }; }

        uint delta = (uint)(TwoExp32 * hz / SampleRateHz);
        lock (_lock)
        {
            if (_ddcDeltaPhase[ddc] == delta)
                return new { ok = true, ddc, hz, deltaPhase = $"0x{delta:X8}", written = false };
            using var fs = new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            XdmaIo.Write32(fs.SafeFileHandle, DdcFreqReg[ddc], delta);
            _ddcDeltaPhase[ddc] = delta;
        }
        _log.LogInformation("xdma.control ddc{Ddc} freq={Hz} delta=0x{Delta:X8}", ddc, hz, delta);
        return new { ok = true, ddc, hz, deltaPhase = $"0x{delta:X8}", written = true };
    }

    // ADC control shadow (register 0x2018 is write-only; the C keeps
    // GRXADCCtrl for the same reason). Session-local: the native session
    // owns the register plane while it runs, and p2app rewrites everything
    // at its next start. ADC1 RX atten = bits [4:0], 0-31 dB
    // (SetADCAttenuator, saturnregisters.c). ADC2 (<<10) left for multi-RX.
    private const long AdcCtrlReg = 0x2018;         // VADDRADCCTRLREG
    private uint _adcCtrlShadow;
    private int? _rxAttenDb;

    /// <summary>PHASE 4b: ADC1 RX step attenuator, 0-31 dB. Driven by the
    /// native session from radio state; write-on-change like the DDC path.
    /// </summary>
    public object SetRxAtten(int db, out string? refusal)
    {
        refusal = null;
        if (!SaturnPresent) { refusal = "no Saturn on the local PCIe bus"; return new { ok = false }; }
        db = Math.Clamp(db, 0, 31);
        lock (_lock)
        {
            if (_rxAttenDb == db) return new { ok = true, db, written = false };
            _adcCtrlShadow = (_adcCtrlShadow & ~0x1Fu) | (uint)db;
            using var fs = new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            XdmaIo.Write32(fs.SafeFileHandle, AdcCtrlReg, _adcCtrlShadow);
            _rxAttenDb = db;
        }
        _log.LogInformation("xdma.control rx-atten={Db}dB", db);
        return new { ok = true, db, written = true };
    }

    /// <summary>DUC (TX) frequency — same math, TX side. Same gate.</summary>
    public object SetDucFrequency(long hz, out string? refusal)
    {
        refusal = null;
        if (!SaturnPresent) { refusal = "no Saturn on the local PCIe bus"; return new { ok = false }; }
        if (_tx.MoxOwner is not null) { refusal = "TX is keyed"; return new { ok = false }; }
        if (hz is < 0 or > 61_440_000) { refusal = "frequency out of Nyquist range"; return new { ok = false }; }
        uint delta = (uint)(TwoExp32 * hz / SampleRateHz);
        lock (_lock)
        {
            if (_ducDeltaPhase == delta) return new { ok = true, hz, deltaPhase = $"0x{delta:X8}", written = false };
            using var fs = new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            XdmaIo.Write32(fs.SafeFileHandle, TxDucReg, delta);
            _ducDeltaPhase = delta;
        }
        return new { ok = true, hz, deltaPhase = $"0x{delta:X8}", written = true };
    }
}
