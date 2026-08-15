// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// SaturnRxStream — PHASE 3 OPENS: the DMA data plane. A C# port of the
// DDC streaming mechanics from Laurence Barker G8NJJ's p2app
// (OutDDCIQ.c) and saturndrivers.c (GPL like us): configure DDC0, pulse
// the RX FIFO reset, raise the master stream enable, then read the
// sample stream from /dev/xdma0_c2h_0 with FIFO-depth-gated adaptive
// DMA transfers — exactly the discipline the C uses.
//
// PHASE 3a SCOPE (this file): prove the data plane. Samples flow from
// the FPGA through DMA into Zeus's memory and are ACCOUNTED — bytes,
// blocks, throughput, FIFO health, a hex peek, sample magnitudes. The
// acceptance criterion is arithmetic: measured throughput must equal
// sampleRate × 8 bytes/sample (48 kHz ⇒ 0.384 MB/s, 192 kHz ⇒ 1.536
// MB/s). Phase 3b routes these samples into the DSP rings the UDP path
// feeds today — that commit turns this throughput into audio.
//
// Register facts (ported, with their sources):
//   0x100C VADDRDDCRATES  — 3 bits/DDC: 0=disabled 1=48k 2=96k 3=192k
//                           4=384k 5=768k 6=1536k 7=interleave
//   0x1010 VADDRDDCINSEL  — input select; BIT 30 = master DDC stream
//                           enable (SetRXDDCEnabled). Write-only: the C
//                           keeps a shadow; we compose the whole value
//                           (low bits 0 = ADC1 for all DDCs).
//   0x7000 VADDRFIFORESET — bit 2 (VBITDDCFIFORESET): write 0 then 1
//                           to pulse the RX FIFO reset.
//   0x9000 VADDRFIFOMONBASE + 4·ch — depth[15:0] in 8-BYTE LOCATIONS;
//                           bit31 overflow, bit30 over-threshold, bit29
//                           underflow (flags clear on read). RX DDC is
//                           channel 0 (first in EDMAStreamSelect) —
//                           bench-falsifiable: a depth that never moves
//                           means the channel index is wrong.
//   c2h stream            — pread at offset 0 (VADDRDDCSTREAMREAD);
//                           adaptive 4/8/16/32 KB by depth, 500 µs waits
//                           below threshold, exactly as OutDDCIQ.c.
//
// Contention discipline unchanged: the stream owns registers p2app also
// owns — start refuses without confirm:'p2app-stopped', while TX is
// keyed, or without a validated Saturn. Stop restores rates=0, enable
// off.

using System.Diagnostics;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class SaturnRxStream : IDisposable
{
    private const string UserDev = "/dev/xdma0_user";
    private const string RxDev = "/dev/xdma0_c2h_0";

    private const long RatesReg = 0x100C;
    private const long InSelReg = 0x1010;
    private const long FifoResetReg = 0x7000;
    private const long FifoMonRx = 0x9000;          // base + 4·0 (RX = channel 0)
    private const uint InSelStreamEnable = 1u << 30;
    private const uint FifoResetBit = 1u << 2;      // VBITDDCFIFORESET

    private readonly SaturnXdmaProbe _probe;
    private readonly SaturnControl _control;
    private readonly TxService _tx;
    private readonly ILogger<SaturnRxStream> _log;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    // Stats (volatile snapshot; written by the reader thread only).
    private long _bytes;
    private long _blocks;
    private long _overflows;
    private long _overThreshold;
    private int _lastDepth;
    private double _mbPerSec;
    private long _startedAtMs;
    private int _rateKhz;
    private long _tunedHz;
    private string _peekHex = "";
    private int _sampleMin, _sampleMax;
    private string? _error;

    public SaturnRxStream(SaturnXdmaProbe probe, SaturnControl control, TxService tx, ILogger<SaturnRxStream> log)
    {
        _probe = probe;
        _control = control;
        _tx = tx;
        _log = log;
    }

    public bool Running => _cts is not null;

    public object Status()
    {
        lock (_lock)
        {
            return new
            {
                running = Running,
                rateKhz = _rateKhz,
                tunedHz = _tunedHz,
                bytes = _bytes,
                blocks = _blocks,
                mbPerSec = Math.Round(_mbPerSec, 3),
                expectedMbPerSec = Math.Round(_rateKhz * 1000.0 * 8 / 1_000_000.0, 3),
                lastFifoDepth = _lastDepth,
                overflows = _overflows,
                overThreshold = _overThreshold,
                uptimeSec = Running ? (Environment.TickCount64 - _startedAtMs) / 1000 : 0,
                firstBlockHex = _peekHex,
                sampleMin = _sampleMin,
                sampleMax = _sampleMax,
                error = _error,
            };
        }
    }

    public bool Start(int rateKhz, long tuneHz, out string? refusal)
    {
        refusal = null;
        if (!OperatingSystem.IsLinux() || _probe.Probe() is null)
        { refusal = "no Saturn on the local PCIe bus"; return false; }
        if (_tx.MoxOwner is not null)
        { refusal = "TX is keyed — no stream experiments under power"; return false; }
        if (!File.Exists(RxDev))
        { refusal = $"{RxDev} missing — xdma driver incomplete?"; return false; }
        uint rateCode = rateKhz switch { 48 => 1u, 96 => 2u, 192 => 3u, 384 => 4u, _ => 0u };
        if (rateCode == 0) { refusal = "rateKhz must be 48, 96, 192 or 384"; return false; }

        lock (_lock)
        {
            if (Running) { refusal = "stream already running"; return false; }
            _cts = new CancellationTokenSource();
            _bytes = 0; _blocks = 0; _overflows = 0; _overThreshold = 0;
            _mbPerSec = 0; _lastDepth = 0; _peekHex = ""; _error = null;
            _sampleMin = int.MaxValue; _sampleMax = int.MinValue;
            _rateKhz = rateKhz; _tunedHz = tuneHz;
            _startedAtMs = Environment.TickCount64;
            var ct = _cts.Token;
            _thread = new Thread(() => Pump(rateCode, tuneHz, ct))
            { IsBackground = true, Name = "saturn-rx-dma" };
            _thread.Start();
        }
        _log.LogWarning("xdma.rx stream START rate={Rate}kHz tune={Hz}Hz", rateKhz, tuneHz);
        return true;
    }

    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
            _thread = null;
        }
    }

    private void Pump(uint rateCode, long tuneHz, CancellationToken ct)
    {
        try
        {
            using var bar = new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var rx = new FileStream(RxDev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var h = bar.SafeFileHandle;

            // ---- init sequence, per p2app ----
            _control.SetDdcFrequency(0, tuneHz, out _);          // tune DDC0
            XdmaIo.Write32(h, RatesReg, rateCode);               // DDC0 rate, others disabled
            uint reset = XdmaIo.Read32(h, FifoResetReg);
            XdmaIo.Write32(h, FifoResetReg, reset & ~FifoResetBit);   // pulse RX FIFO reset
            XdmaIo.Write32(h, FifoResetReg, reset | FifoResetBit);
            XdmaIo.Write32(h, InSelReg, InSelStreamEnable);      // ADC1 all DDCs + master enable

            var buf = new byte[32768];
            int transfer = 4096;
            var sw = Stopwatch.StartNew();
            long windowBytes = 0;
            long windowStart = sw.ElapsedMilliseconds;

            while (!ct.IsCancellationRequested)
            {
                uint mon = XdmaIo.Read32(h, FifoMonRx);
                int depth = (int)(mon & 0xFFFF);                 // 8-byte locations
                if ((mon & 0x8000_0000) != 0) _overflows++;
                if ((mon & 0x4000_0000) != 0) _overThreshold++;
                _lastDepth = depth;

                // Adaptive transfer size, exactly the C's ladder.
                transfer = depth > 4096 ? 32768 : depth > 2048 ? 16384 : depth > 1024 ? 8192 : 4096;

                if (depth < transfer / 8)
                {
                    Thread.Sleep(1);                             // the C waits 500 µs; 1 ms is our floor
                    continue;
                }

                int got = XdmaIo.ReadBytes(rx.SafeFileHandle, buf, transfer, 0);
                if (got <= 0) { Thread.Sleep(1); continue; }

                _bytes += got;
                _blocks++;
                windowBytes += got;

                if (_blocks == 1)
                    _peekHex = Convert.ToHexString(buf.AsSpan(0, Math.Min(32, got)));

                // Sparse magnitude scan: int32 LE pairs (24 significant bits
                // live in the top; header words will spike occasionally —
                // stats, not framing).
                for (int i = 0; i + 8 <= got; i += 512)
                {
                    int v = BitConverter.ToInt32(buf, i);
                    if (v < _sampleMin) _sampleMin = v;
                    if (v > _sampleMax) _sampleMax = v;
                }

                long now = sw.ElapsedMilliseconds;
                if (now - windowStart >= 1000)
                {
                    _mbPerSec = windowBytes / 1_000_000.0 / ((now - windowStart) / 1000.0);
                    windowBytes = 0;
                    windowStart = now;
                }
            }

            // ---- teardown: leave the field as we found it ----
            XdmaIo.Write32(h, InSelReg, 0);                      // stream enable off
            XdmaIo.Write32(h, RatesReg, 0);                      // all DDCs disabled
            _log.LogWarning("xdma.rx stream STOP after {Bytes} bytes / {Blocks} blocks", _bytes, _blocks);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "xdma.rx stream failed");
            lock (_lock) { _error = ex.Message; _cts = null; _thread = null; }
        }
    }

    public void Dispose() => Stop();
}
