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
    private readonly DspPipelineService _dsp;
    private readonly RadioService _radio;
    private readonly ILogger<SaturnRxStream> _log;
    private int _attenDb = -1;

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

    // ---- demux state (the C's frame walker, ported) ----
    // Stream format per OutDDCIQ.c/AnalyseDDCHeader: frame = one RATE WORD
    // (marker: byte 7 == 0x80; low 30 bits = 10× 3-bit per-DDC rate codes)
    // followed by FrameLength 64-bit sample words, each carrying 48 bits =
    // one 24+24-bit IQ pair. Samples per frame per rate code:
    // {0,1,2,4,8,16,32} — so a single 48 kHz DDC rides 16 wire-bytes per
    // sample, which is exactly the 2× the first field run measured.
    private static readonly int[] SamplesPerCode = { 0, 1, 2, 4, 8, 16, 32, 0 };
    private bool _aligned;
    private readonly byte[] _carry = new byte[512];
    private int _carryLen;
    private uint _prevRateWord = 0xFFFFFFFF;
    private int _frameWords;
    private long _samples;
    private long _markers;
    private long _resyncs;
    private double _ksps;
    private bool _thresholdPrev;

    // ---- PHASE 3b: forwarding into the DSP's front door ----
    // The demuxed samples are delivered to DspPipelineService through the
    // SAME interface the network client uses — Zeus.Protocol2.IRxPacketSink
    // .OnIqFrame — as interleaved doubles at the P2 client's exact scale
    // (1/2^23; board gain correction deliberately omitted here and noted).
    // The FPGA emits each sample word's low 6 bytes ALREADY IN P2 WIRE
    // ORDER: 24-bit BIG-endian I then Q (p2app copies them verbatim into
    // the UDP packet) — which also corrects the previous commit's LE stats
    // decode. Frames are 240 complex samples (5 ms @ 48 kHz), one reused
    // buffer (the sink contract copies synchronously). If no session is
    // active the sink drops frames internally — fedFrames still counts
    // deliveries, and the audible bench arrives with the Phase-4 native
    // session (the badge's Connect).
    private const int FramePairs = 240;
    private readonly double[] _iqOut = new double[FramePairs * 2];
    private int _iqFill;
    private uint _iqSeq;
    private long _fedFrames;

    public SaturnRxStream(
        SaturnXdmaProbe probe, SaturnControl control, TxService tx,
        DspPipelineService dsp, RadioService radio, ILogger<SaturnRxStream> log)
    {
        _probe = probe;
        _control = control;
        _tx = tx;
        _dsp = dsp;
        _radio = radio;
        _log = log;
        // PHASE 4b: the native session follows the operator. RadioService is
        // the single source of tuning truth (frozen-NCO model — the hardware
        // sits at RadioLoHz with CW offset pre-baked; the P2 path pushes the
        // SAME field from OnRadioStateChanged). While the stream runs, VFO
        // and attenuator changes route to the register plane; while it
        // doesn't, this handler is a no-op.
        _radio.StateChanged += OnRadioState;
    }

    private void OnRadioState(StateDto s)
    {
        if (!Running) return;
        try
        {
            _control.SetDdcFrequency(0, s.RadioLoHz, out _);
            lock (_lock) { _tunedHz = s.RadioLoHz; }
            // EffectiveAttenDb lives on RadioService, not StateDto — the same
            // property ConnectP2Async seeds the P2 client from.
            int atten = Math.Clamp(_radio.EffectiveAttenDb, 0, 31);
            if (atten != _attenDb)
            {
                _control.SetRxAtten(atten, out _);
                _attenDb = atten;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "xdma.rx state-follow failed");
        }
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
                samplesDemuxed = _samples,
                effectiveKsps = Math.Round(_ksps, 2),   // THE criterion: ≈ rateKhz
                frameWords = _frameWords,
                markerWords = _markers,
                resyncs = _resyncs,
                fedFrames = _fedFrames,          // 3b: IqFrames delivered to the DSP sink
                engine = _dsp.CurrentEngineName,  // 4a diag: WdspDspEngine vs Synthetic fallback
                attenDb = _attenDb,               // 4b: ADC1 RX step attenuator, radio-state-followed
                aligned = _aligned,
                sampleMin = _sampleMin,                  // 24-bit I, demuxed
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
            // PHASE 4a: bring the engine up BEFORE the pump — from the first
            // delivered frame there is a room behind the door.
            try { _dsp.ConnectNativeRx(rateKhz * 1000); }
            catch (Exception ex) { refusal = ex.Message; return false; }
            // 4b.2: announce the session so the workspace unlocks its
            // controls (AGC, atten, AF, mode — everything gated on
            // IsConnected). TX controls remain inert until 4c.
            _radio.MarkNativeSessionConnected(rateKhz * 1000);
            _cts = new CancellationTokenSource();
            _bytes = 0; _blocks = 0; _overflows = 0; _overThreshold = 0;
            _mbPerSec = 0; _lastDepth = 0; _peekHex = ""; _error = null;
            _sampleMin = int.MaxValue; _sampleMax = int.MinValue;
            _aligned = false; _carryLen = 0; _prevRateWord = 0xFFFFFFFF;
            _frameWords = 0; _samples = 0; _markers = 0; _resyncs = 0;
            _ksps = 0; _thresholdPrev = false;
            _iqFill = 0; _iqSeq = 0; _fedFrames = 0;
            _rateKhz = rateKhz; _tunedHz = tuneHz;
            _startedAtMs = Environment.TickCount64;
            // PHASE 4b: seed tuning + atten from the operator's live state —
            // the fixed request default only matters on a virgin database.
            var snap = _radio.Snapshot();
            if (snap.RadioLoHz > 0) { tuneHz = snap.RadioLoHz; _tunedHz = tuneHz; }
            _attenDb = -1;   // force one atten push via the seed below
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
        bool wasRunning;
        lock (_lock)
        {
            wasRunning = _cts is not null;
            _cts?.Cancel();
            _cts = null;
            _thread = null;
        }
        if (wasRunning)
        {
            _radio.MarkNativeSessionDisconnected();
            _dsp.DisconnectNativeRx();
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
            _control.SetRxAtten(Math.Clamp(_radio.EffectiveAttenDb, 0, 31), out _);
            _control.SetAdcOptions(dither: true, random: true, out _);   // 4b.4: condition the ADCs
            XdmaIo.Write32(h, RatesReg, rateCode);               // DDC0 rate, others disabled
            uint reset = XdmaIo.Read32(h, FifoResetReg);
            XdmaIo.Write32(h, FifoResetReg, reset & ~FifoResetBit);   // pulse RX FIFO reset
            XdmaIo.Write32(h, FifoResetReg, reset | FifoResetBit);
            XdmaIo.Write32(h, InSelReg, InSelStreamEnable);      // ADC1 all DDCs + master enable

            var buf = new byte[32768];
            int transfer = 4096;
            var sw = Stopwatch.StartNew();
            long windowBytes = 0;
            long windowSamples = 0;
            long windowStart = sw.ElapsedMilliseconds;

            while (!ct.IsCancellationRequested)
            {
                uint mon = XdmaIo.Read32(h, FifoMonRx);
                int depth = (int)(mon & 0xFFFF);                 // 8-byte locations
                if ((mon & 0x8000_0000) != 0) _overflows++;
                bool thr = (mon & 0x4000_0000) != 0;
                if (thr && !_thresholdPrev) _overThreshold++;    // rising edges only
                _thresholdPrev = thr;
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

                long before = _samples;
                Demux(buf, got);
                windowSamples += _samples - before;

                long now = sw.ElapsedMilliseconds;
                if (now - windowStart >= 1000)
                {
                    double secs = (now - windowStart) / 1000.0;
                    _mbPerSec = windowBytes / 1_000_000.0 / secs;
                    _ksps = windowSamples / 1000.0 / secs;
                    windowBytes = 0;
                    windowSamples = 0;
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
            try { _radio.MarkNativeSessionDisconnected(); } catch { /* best-effort */ }
            try { _dsp.DisconnectNativeRx(); } catch { /* teardown is best-effort on the failure path */ }
        }
    }

    /// <summary>The C's frame walker: align once on a rate word (byte 7 ==
    /// 0x80, searched at 8-byte stride past the first word), then consume
    /// [rate word][FrameLength sample words] frames; partial frames carry
    /// across DMA block boundaries. Sample words hold one 24+24-bit IQ pair
    /// in their low 48 bits — DDC0's I is min/max-tracked, sign-extended.
    /// A marker miss mid-walk realigns and counts a resync.</summary>
    private void Demux(byte[] block, int got)
    {
        // Prepend carry.
        byte[] data;
        int len;
        if (_carryLen > 0)
        {
            data = new byte[_carryLen + got];
            Buffer.BlockCopy(_carry, 0, data, 0, _carryLen);
            Buffer.BlockCopy(block, 0, data, _carryLen, got);
            len = _carryLen + got;
            _carryLen = 0;
        }
        else { data = block; len = got; }

        int off = 0;
        if (!_aligned)
        {
            for (int i = 8; i + 8 <= len; i += 8)
                if (data[i + 7] == 0x80) { off = i; _aligned = true; break; }
            if (!_aligned) return;                       // keep searching next block
        }

        while (len - off >= 8)
        {
            if (data[off + 7] != 0x80)                   // lost the frame — realign
            {
                _resyncs++;
                _aligned = false;
                for (int i = off + 8; i + 8 <= len; i += 8)
                    if (data[i + 7] == 0x80) { off = i; _aligned = true; break; }
                if (!_aligned) return;
            }

            uint rateWord = BitConverter.ToUInt32(data, off);
            if (rateWord != _prevRateWord)
            {
                _prevRateWord = rateWord;
                int total = 0;
                uint hdr = rateWord;
                for (int ddc = 0; ddc < 10; ddc++)
                {
                    uint code = hdr & 7;
                    if (code == 7) { hdr >>= 3; total += 2 * SamplesPerCode[hdr & 7]; ddc++; }
                    else total += SamplesPerCode[code];
                    hdr >>= 3;
                }
                _frameWords = total;
            }

            int frameBytes = 8 + _frameWords * 8;
            if (len - off < frameBytes) break;           // partial frame → carry

            _markers++;
            _samples += _frameWords;
            for (int w = 0; w < _frameWords; w++)
            {
                int b = off + 8 + w * 8;
                // FPGA DMA sample word = TWO 32-bit LITTLE-endian fields,
                // 24 significant bits each: I at +0, Q at +4. Convicted by
                // the bench, twice over: captured words decode to noise-
                // floor values this way (Q = 0xFFFFFF = -1; I = tens of
                // thousands) and to exact ±full-scale rails when read as
                // packed 24-bit BE — which is precisely what the meter
                // showed with NOTHING on the antenna port. The BE order is
                // p2app's UDP output format, produced by its copy loop;
                // it is not the FPGA's memory format.
                int i24 = BitConverter.ToInt32(data, b) & 0xFFFFFF;
                int q24 = BitConverter.ToInt32(data, b + 4) & 0xFFFFFF;
                if ((i24 & 0x800000) != 0) i24 |= unchecked((int)0xFF000000);
                if ((q24 & 0x800000) != 0) q24 |= unchecked((int)0xFF000000);
                if (i24 < _sampleMin) _sampleMin = i24;
                if (i24 > _sampleMax) _sampleMax = i24;

                const double Scale = 1.0 / 8388608.0;   // the P2 client's 1/2^23
                _iqOut[_iqFill++] = i24 * Scale;
                _iqOut[_iqFill++] = q24 * Scale;
                if (_iqFill == _iqOut.Length)
                {
                    _iqFill = 0;
                    var f = new Zeus.Protocol2.IqFrame(
                        _iqOut,
                        FramePairs,
                        _rateKhz * 1000,
                        _iqSeq++,
                        Environment.TickCount64 * 1_000_000L,
                        0);
                    ((Zeus.Protocol2.IRxPacketSink)_dsp).OnIqFrame(in f);
                    _fedFrames++;
                }
            }
            off += frameBytes;
        }

        int rest = len - off;
        if (rest > 0 && rest <= _carry.Length)
        {
            Buffer.BlockCopy(data, off, _carry, 0, rest);
            _carryLen = rest;
        }
    }

    public void Dispose() => Stop();
}
