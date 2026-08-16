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
    // Audit fix: the register also carries the TX-path attenuator fields
    // (SetADCAttenuator: ADC1 RX [4:0] / TX [9:5]; ADC2 shifted <<10 →
    // RX [14:10] / TX [19:15]). The shadow used to start at zero, so the
    // first RX-atten write silently programmed BOTH TX attenuators to
    // 0 dB — harmless today (no native TX path exists) but exactly the
    // wrong default to inherit into 4c. TX fields now seed at maximum
    // attenuation until the TX increment owns them deliberately.
    private uint _adcCtrlShadow = (31u << 5) | (31u << 15);
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

    // RF GPIO register (0x2014, VADDRRFGPIOREG) — shadow-composed like the
    // C's GPIORegValue. Holds the ADC front-end conditioning bits
    // (SetADCOptions): ADC1 random/PGA/dither = bits 8/9/10, ADC2 = 11/12/13.
    // Never written natively before this — the raised no-antenna floor and
    // spur comb were the ADCs running at power-up defaults. Antenna/preamp
    // relay bits in this register remain zero (their power-up state) until
    // the relay increment lands.
    private const long RfGpioReg = 0x2014;          // VADDRRFGPIOREG
    private uint _rfGpioShadow;

    // ================== THE ARMING CEREMONY (Phase 4c) ==================
    // Until this commit, no build could key: SetMox and SetTxEnable did not
    // exist. They exist now — behind a ceremony designed so that radiating
    // requires three deliberate acts in order: (1) POST /api/xdma/tx/arm
    // with the confirm phrase, which starts an auto-disarm countdown;
    // (2) a software MOX claim (TUN, CWX, MOX) inside the armed window;
    // (3) the operator's own dummy load, which no software can verify and
    // this commit's bench notes therefore insist on. Disarm fires
    // automatically at expiry, on session stop, on the failure path, and
    // on demand — and disarming always forces MOX and TX-enable off.
    private const long TxConfigReg = 0x2008;        // VADDRTXCONFIGREG
    private const int VMoxBit = 24;                 // RF GPIO
    private const int VTxEnableBit = 25;            // RF GPIO
    private uint _txConfigShadow;
    private long _armedUntilUtcTicks;
    private CancellationTokenSource? _armCts;
    private bool _hwMox;

    public bool TxArmed => DateTime.UtcNow.Ticks < Interlocked.Read(ref _armedUntilUtcTicks);
    public int TxArmedSecondsLeft
    {
        get
        {
            long left = Interlocked.Read(ref _armedUntilUtcTicks) - DateTime.UtcNow.Ticks;
            return left > 0 ? (int)TimeSpan.FromTicks(left).TotalSeconds : 0;
        }
    }

    /// <summary>TX config parity (p2app boot lines 460-500): modulation
    /// source = eIQData (0), protocol = P2/192k (bit 3), amplitude scale =
    /// 0x2000 (1/32 full scale — the FW≥13 constant, also the conservative
    /// one; FW<13 radios would use 0x1FFFF). EER stays at its power-up off.
    /// Keyer ramp + DAC atten ROMs are deferred with reason: the smoke test
    /// keys a steady TUN carrier, not the FPGA CW keyer, and drive is
    /// governed low via the DRV slider's IQ scaling.</summary>
    public object SetTxParity(out string? refusal)
    {
        refusal = null;
        if (!SaturnPresent) { refusal = "no Saturn on the local PCIe bus"; return new { ok = false }; }
        lock (_lock)
        {
            uint reg = 0;
            reg |= 1u << 3;                          // VTXCONFIGPROTOCOLBIT: P2 (192 kHz)
            reg |= 0x2000u << 4;                     // VTXCONFIGSCALEBIT: 18-bit scale, 1/32
            // modulation source bits [1:0] = eIQData = 0
            if (reg == _txConfigShadow) return new { ok = true, written = false };
            using var fs = new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            XdmaIo.Write32(fs.SafeFileHandle, TxConfigReg, reg);
            _txConfigShadow = reg;
        }
        _log.LogWarning("xdma.control TX parity: eIQData, P2 rate, scale 1/32");
        return new { ok = true, written = true };
    }

    public object TxArm(int seconds, out string? refusal)
    {
        refusal = null;
        if (!SaturnPresent) { refusal = "no Saturn on the local PCIe bus"; return new { ok = false }; }
        seconds = Math.Clamp(seconds, 10, 120);
        _armCts?.Cancel();
        var cts = new CancellationTokenSource();
        _armCts = cts;
        Interlocked.Exchange(ref _armedUntilUtcTicks, DateTime.UtcNow.AddSeconds(seconds).Ticks);
        _log.LogWarning("xdma.control ⚡ TX ARMED for {S}s — auto-disarm scheduled", seconds);
        _ = Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token)
                .ContinueWith(_ => { if (!cts.IsCancellationRequested) TxDisarm(); },
                              TaskScheduler.Default);
        return new { ok = true, armedSeconds = seconds };
    }

    /// <summary>Always legal, always total: kills MOX, kills TX enable,
    /// zeroes the window.</summary>
    public object TxDisarm()
    {
        _armCts?.Cancel();
        Interlocked.Exchange(ref _armedUntilUtcTicks, 0);
        SetMoxBit(false);
        SetTxEnableBit(false);
        _log.LogWarning("xdma.control TX DISARMED — MOX and TX-enable forced off");
        return new { ok = true };
    }

    /// <summary>Keying while not armed is refused; unkeying is always
    /// honored. The RF GPIO shadow carries the bit.</summary>
    public object SetMox(bool on, out string? refusal)
    {
        refusal = null;
        if (on && !TxArmed) { refusal = "TX is not armed — POST /api/xdma/tx/arm first"; return new { ok = false }; }
        if (on && !SaturnPresent) { refusal = "no Saturn on the local PCIe bus"; return new { ok = false }; }
        SetTxEnableBit(on);
        SetMoxBit(on);
        return new { ok = true, mox = on };
    }

    private void SetMoxBit(bool on)
    {
        lock (_lock)
        {
            if (!SaturnPresent) return;
            uint reg = _rfGpioShadow;
            if (on) reg |= 1u << VMoxBit; else reg &= ~(1u << VMoxBit);
            if (reg == _rfGpioShadow) return;
            using var fs = new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            XdmaIo.Write32(fs.SafeFileHandle, RfGpioReg, reg);
            _rfGpioShadow = reg;
            _hwMox = on;
        }
        _log.LogWarning("xdma.control MOX={On}", on);
    }

    private void SetTxEnableBit(bool on)
    {
        lock (_lock)
        {
            if (!SaturnPresent) return;
            uint reg = _rfGpioShadow;
            if (on) reg |= 1u << VTxEnableBit; else reg &= ~(1u << VTxEnableBit);
            if (reg == _rfGpioShadow) return;
            using var fs = new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            XdmaIo.Write32(fs.SafeFileHandle, RfGpioReg, reg);
            _rfGpioShadow = reg;
        }
    }

    public bool HwMox { get { lock (_lock) return _hwMox; } }

    /// <summary>THE missing init write, found by systematic diff against
    /// p2app's boot sequence (p2app.c line 484): the FPGA has a hardware
    /// byte-swap engine on the DMA data path — bit 26 (VDATAENDIAN) of the
    /// RF GPIO register. Set = words emerge in NETWORK byte order (the
    /// 24-bit BE I/Q both downstream authorities parse); clear (power-up) =
    /// 'raspberry pi local order'. p2app and piHPSDR both set it at boot,
    /// which is why their verbatim copies work — and why every decode this
    /// port shipped was fighting a configurable switch left at its default.
    /// </summary>
    public object SetByteSwapping(bool swapped, out string? refusal)
    {
        refusal = null;
        if (!SaturnPresent) { refusal = "no Saturn on the local PCIe bus"; return new { ok = false }; }
        uint reg;
        lock (_lock)
        {
            reg = _rfGpioShadow;
            if (swapped) reg |= 1u << 26; else reg &= ~(1u << 26);
            if (reg == _rfGpioShadow) return new { ok = true, swapped, written = false };
            using var fs = new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            XdmaIo.Write32(fs.SafeFileHandle, RfGpioReg, reg);
            _rfGpioShadow = reg;
        }
        _log.LogWarning("xdma.control byte-swap={S} (network order)", swapped);
        return new { ok = true, swapped, written = true };
    }

    /// <summary>PHASE 4b.4: ADC conditioning — dither + randomizer for both
    /// ADCs (PGA left off, matching p2app's SetADCOptions(…, false, D, R)
    /// calls). Write-on-change via the shadow.</summary>
    public object SetAdcOptions(bool dither, bool random, out string? refusal)
    {
        refusal = null;
        if (!SaturnPresent) { refusal = "no Saturn on the local PCIe bus"; return new { ok = false }; }
        uint reg;
        lock (_lock)
        {
            reg = _rfGpioShadow;
            reg &= ~((1u << 8) | (1u << 10) | (1u << 11) | (1u << 13));   // random/dither, both ADCs
            reg &= ~((1u << 9) | (1u << 12));                             // PGA off, both ADCs
            if (random) reg |= (1u << 8) | (1u << 11);
            if (dither) reg |= (1u << 10) | (1u << 13);
            if (reg == _rfGpioShadow) return new { ok = true, dither, random, written = false };
            using var fs = new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            XdmaIo.Write32(fs.SafeFileHandle, RfGpioReg, reg);
            _rfGpioShadow = reg;
        }
        _log.LogInformation("xdma.control adc-options dither={D} random={R}", dither, random);
        return new { ok = true, dither, random, written = true };
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
