// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// SaturnTxProbe — PHASE 4c OPENS, cold. The transmit side begins the way
// the receive side should have: prove the data plane before anything can
// radiate. This prober writes frames of ZERO-valued samples into the DUC's
// h2c DMA stream (/dev/xdma0_h2c_0, pwrite at offset 0, 1440-byte
// transfers = 240 samples × 6 bytes of 24-bit BE I/Q — the identical
// cadence the RX path speaks) and watches the DUC FIFO monitor for depth
// movement. That is the entire ambition of this file.
//
// SAFETY, stated as architecture: this commit implements NO SetMox, NO
// SetTxEnable, and touches NEITHER bit. The binary that ships from this
// commit is INCAPABLE of keying the transmitter — not gated, not guarded:
// incapable. The MOX/TX-enable verbs arrive in a later commit behind an
// explicit arming ceremony, after the cold plane is field-proven. The PA
// stays silent through this entire phase opening; the dummy-load moment
// comes when the plumbing has earned it.
//
// Ported facts (InDUCIQ.c / saturnregisters.h, credited): DUC device
// /dev/xdma0_h2c_0; stream write address 0; p2app writes 1440 B per
// message; DUC FIFO monitor assumed channel 1 (eTXDUCDMA, second in the
// enum — the RX channel-0 assumption proved right; this one is equally
// bench-falsifiable: a depth that never moves under the probe means the
// index is wrong, and the status shows it plainly).

using Zeus.Contracts;

namespace Zeus.Server;

public sealed class SaturnTxProbe
{
    private const string DucDev = "/dev/xdma0_h2c_0";
    private const string UserDev = "/dev/xdma0_user";
    private const long FifoMonTx = 0x9004;          // base 0x9000 + 4 × channel 1
    private const int FrameBytes = 1440;            // p2app's VDMATRANSFERSIZE

    private readonly SaturnXdmaProbe _probe;
    private readonly TxService _tx;
    private readonly ILogger<SaturnTxProbe> _log;

    private readonly object _lock = new();
    private long _bytesWritten;
    private int _framesWritten;
    private int _depthBefore = -1;
    private int _depthAfter = -1;
    private int _depthSettled = -1;
    private string? _error;
    private DateTimeOffset? _lastRun;

    public SaturnTxProbe(SaturnXdmaProbe probe, TxService tx, ILogger<SaturnTxProbe> log)
    {
        _probe = probe;
        _tx = tx;
        _log = log;
    }

    public object Status()
    {
        lock (_lock)
        {
            return new
            {
                capability = "cold probe only — this build contains no MOX or TX-enable path",
                ducDevice = DucDev,
                frameBytes = FrameBytes,
                lastRun = _lastRun,
                framesWritten = _framesWritten,
                bytesWritten = _bytesWritten,
                depthBefore = _depthBefore,     // DUC FIFO locations (8-byte), pre-write
                depthAfter = _depthAfter,       // immediately post-write
                depthSettledMs500 = _depthSettled, // 500 ms later — drain behavior, informative
                error = _error,
            };
        }
    }

    /// <summary>Write <paramref name="frames"/> frames of zero samples into
    /// the DUC stream and report FIFO depth before/after/settled. Zero
    /// samples are literal silence even if some future path consumed them —
    /// but nothing consumes them here: MOX and TX-enable do not exist in
    /// this build.</summary>
    public object Probe(int frames, out string? refusal)
    {
        refusal = null;
        if (!OperatingSystem.IsLinux() || _probe.Probe() is null)
        { refusal = "no Saturn on the local PCIe bus"; return new { ok = false }; }
        if (_tx.MoxOwner is not null)
        { refusal = "TX is keyed via the network session — no probes under power"; return new { ok = false }; }
        if (!File.Exists(DucDev))
        { refusal = $"{DucDev} missing — xdma driver incomplete?"; return new { ok = false }; }
        frames = Math.Clamp(frames, 1, 64);

        lock (_lock)
        {
            _error = null;
            try
            {
                using var bar = new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                using var duc = new FileStream(DucDev, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                var h = bar.SafeFileHandle;

                _depthBefore = (int)(XdmaIo.Read32(h, FifoMonTx) & 0xFFFF);

                var zeros = new byte[FrameBytes];   // 240 samples of exact silence
                int written = 0;
                for (int i = 0; i < frames; i++)
                    written += XdmaIo.WriteBytes(duc.SafeFileHandle, zeros, FrameBytes, 0);

                _depthAfter = (int)(XdmaIo.Read32(h, FifoMonTx) & 0xFFFF);
                Thread.Sleep(500);
                _depthSettled = (int)(XdmaIo.Read32(h, FifoMonTx) & 0xFFFF);

                _framesWritten = frames;
                _bytesWritten = written;
                _lastRun = DateTimeOffset.UtcNow;
                _log.LogWarning(
                    "xdma.tx cold probe: {Frames} zero-frames ({Bytes} B) depth {Before}→{After}→{Settled}",
                    frames, written, _depthBefore, _depthAfter, _depthSettled);
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                _log.LogError(ex, "xdma.tx cold probe failed");
            }
        }
        return Status();
    }
}
