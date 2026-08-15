// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// SaturnTxStream — PHASE 4c's cold rehearsal: the real TX engine's IQ,
// drained from the SAME egress every transmit source funnels through
// (DspPipelineService.ForwardTxIqToP2 — mic ingest, TUN driver, CW synth),
// packed exactly as the P2 client packs it (Int24 clamp, big-endian I then
// Q, six bytes per pair — FlushTxIqLocked's byte-for-byte mirror), and
// written into the DUC's h2c stream in p2app's 1440-byte frames.
//
// The probes established the physics this design rests on: the DUC
// consumes continuously with TX absent (92 KB through a 16 KB FIFO,
// zero stalls), so the blocking DMA write IS the pacing — the consumer
// sets the tempo, exactly as it does for p2app. A small bounded queue
// absorbs scheduler jitter; overflow drops whole frames and counts them.
//
// STILL KEYLESS BY ARCHITECTURE: this build contains no SetMox and no
// SetTxEnable. Every sample written here is consumed and discarded by a
// DUC whose RF end has never been enabled. Pressing TUN or sending CWX in
// a native session is a full software transmit — meters live, chain live,
// feeder counting — with the PA silent. The arming ceremony is the next
// and final commit of this kind.

using System.Collections.Concurrent;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class SaturnTxStream : IDisposable
{
    private const string DucDev = "/dev/xdma0_h2c_0";
    private const string UserDev = "/dev/xdma0_user";
    private const long FifoResetReg = 0x7000;
    private const uint DucFifoResetBit = 1u << 3;   // VBITDUCFIFORESET
    private const long FifoCfgTx = 0x9014;
    private const uint DucFifoDepth = 2048;
    private const int FrameBytes = 1440;            // 240 pairs × 6 bytes BE24
    private const int FramePairs = 240;
    private const int QueueCapFrames = 32;          // ~40 ms @ 192 kHz

    private readonly SaturnXdmaProbe _probe;
    private readonly ILogger<SaturnTxStream> _log;

    private readonly object _lock = new();
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly SemaphoreSlim _queued = new(0);
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private FileStream? _duc;
    private FileStream? _bar;

    // Staging: one frame being packed on the DSP TX thread.
    private byte[] _staging = new byte[FrameBytes];
    private int _stagingFill;

    private long _framesWritten;
    private long _bytesWritten;
    private long _ringDrops;
    private DateTimeOffset? _lastWrite;
    private string? _error;

    public SaturnTxStream(SaturnXdmaProbe probe, ILogger<SaturnTxStream> log)
    {
        _probe = probe;
        _log = log;
    }

    public bool Running => _cts is not null;

    public object Status()
    {
        return new
        {
            running = Running,
            txFramesWritten = Interlocked.Read(ref _framesWritten),
            txBytesWritten = Interlocked.Read(ref _bytesWritten),
            ringDrops = Interlocked.Read(ref _ringDrops),
            queuedFrames = _queue.Count,
            lastWrite = _lastWrite,
            error = _error,
            note = "cold rehearsal — the DUC consumes and discards; no MOX path exists in this build",
        };
    }

    /// <summary>Started/stopped by the native session (SaturnRxStream) so the
    /// feeder shares the session's byte-swap and register discipline.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (Running) return;
            if (!OperatingSystem.IsLinux() || _probe.Probe() is null || !File.Exists(DucDev))
                return;                             // silently absent off-radio
            try
            {
                _bar = new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                _duc = new FileStream(DucDev, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                var h = _bar.SafeFileHandle;
                uint reset = XdmaIo.Read32(h, FifoResetReg);
                XdmaIo.Write32(h, FifoResetReg, reset & ~DucFifoResetBit);
                XdmaIo.Write32(h, FifoResetReg, reset | DucFifoResetBit);
                XdmaIo.Write32(h, FifoCfgTx, DucFifoDepth);

                _framesWritten = 0; _bytesWritten = 0; _ringDrops = 0;
                _stagingFill = 0; _error = null;
                while (_queue.TryDequeue(out _)) { }

                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                _thread = new Thread(() => Writer(ct)) { IsBackground = true, Name = "saturn-duc-feed" };
                _thread.Start();
                _log.LogWarning("xdma.tx feeder START (cold — DUC discards)");
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                _log.LogError(ex, "xdma.tx feeder start failed");
                _duc?.Dispose(); _bar?.Dispose(); _duc = null; _bar = null;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _queued.Release();                      // wake the writer to observe cancel
            _cts = null;
            _thread = null;
        }
    }

    /// <summary>Called on the DSP TX egress thread from ForwardTxIqToP2 —
    /// interleaved floats at the TX engine rate. Packs P2's exact wire
    /// format; whole frames queue to the writer.</summary>
    public void OnTxIq(ReadOnlySpan<float> iqInterleaved)
    {
        if (!Running) return;
        var staging = _staging;
        for (int i = 0; i + 1 < iqInterleaved.Length; i += 2)
        {
            int vi = Int24Clamp(iqInterleaved[i]);
            int vq = Int24Clamp(iqInterleaved[i + 1]);
            int off = _stagingFill;
            staging[off + 0] = (byte)((vi >> 16) & 0xff);
            staging[off + 1] = (byte)((vi >> 8) & 0xff);
            staging[off + 2] = (byte)(vi & 0xff);
            staging[off + 3] = (byte)((vq >> 16) & 0xff);
            staging[off + 4] = (byte)((vq >> 8) & 0xff);
            staging[off + 5] = (byte)(vq & 0xff);
            _stagingFill += 6;
            if (_stagingFill == FrameBytes)
            {
                _stagingFill = 0;
                if (_queue.Count >= QueueCapFrames)
                {
                    Interlocked.Increment(ref _ringDrops);
                }
                else
                {
                    _queue.Enqueue(staging);
                    _queued.Release();
                    _staging = staging = new byte[FrameBytes];
                }
            }
        }
    }

    private static int Int24Clamp(float f)
    {
        int v = (int)MathF.Round(f * 8388607f);
        return Math.Clamp(v, -8388608, 8388607);
    }

    private void Writer(CancellationToken ct)
    {
        try
        {
            var duc = _duc!;
            while (!ct.IsCancellationRequested)
            {
                _queued.Wait(ct);
                if (!_queue.TryDequeue(out var frame)) continue;
                int n = XdmaIo.WriteBytes(duc.SafeFileHandle, frame, FrameBytes, 0);
                Interlocked.Add(ref _bytesWritten, n);
                Interlocked.Increment(ref _framesWritten);
                _lastWrite = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _error = ex.Message;
            _log.LogError(ex, "xdma.tx feeder writer failed");
        }
        finally
        {
            lock (_lock)
            {
                _duc?.Dispose(); _bar?.Dispose();
                _duc = null; _bar = null;
            }
            _log.LogWarning("xdma.tx feeder STOP after {Frames} frames", _framesWritten);
        }
    }

    public void Dispose() => Stop();
}
