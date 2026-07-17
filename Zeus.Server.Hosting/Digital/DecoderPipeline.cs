// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — decoder pipeline.
//
// Owns the RX ring, the slot boundary detector, and the decode worker. The
// decode itself runs OFF the audio thread: Push() only copies into a ring and
// returns. Decoding inline would starve the radio audio path.
//
// SEAM: DecodeSlot() is where ft8_lib is wired in. Everything around it — slot
// alignment against the disciplined clock, resampling to 12 kHz, batching,
// latency measurement, SSE publishing — is complete and testable without it.

using System.Diagnostics;

namespace Zeus.Server.Hosting.Digital;

/// <summary>
/// Slot-aligned RX capture + decode dispatch.
/// </summary>
public sealed class DecoderPipeline : IDisposable
{
    /// <summary>ft8_lib works at 12 kHz; the ring holds SOURCE-rate audio and is
    /// resampled at decode time.</summary>
    public const int DecodeRate = 12_000;

    /// <summary>Max RX rate we size the ring for. The tap declares 48 kHz mono
    /// and AudioTapBridge delivers the pipeline's rate.</summary>
    private const int MaxSourceRate = 48_000;

    /// <summary>Ring span in seconds — must exceed one FT8 slot (15 s) with room
    /// for the worker to finish its snapshot before the producer laps it.</summary>
    private const int RingSeconds = 20;

    private readonly ClockService _clock;
    private readonly EventHub _events;
    private readonly object _sync = new();

    // 48 kHz * 20 s = 960k floats (~3.8 MB). Sized at the SOURCE rate: an
    // earlier draft sized it at DecodeRate (12 kHz) and so held only 5 s of
    // 48 kHz audio — silently truncating every 15 s slot.
    private readonly float[] _ring = new float[MaxSourceRate * RingSeconds];
    private int _write;
    private int _srcRate;
    private int _receiver;
    private long _currentSlot = -1;
    private bool _running;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public DecoderPipeline(ClockService clock, EventHub events)
    {
        _clock = clock;
        _events = events;
    }

    /// <summary>True once a real decoder backend is linked. Reported in /status
    /// and as Ft8TxStatus.nativeAvailable.</summary>
    public bool Available => Ft8Native.Available;

    /// <summary>Duration of the last decode pass. Surfaced in /status so the
    /// timing budget is visible rather than mysterious.</summary>
    public double? LastLatencyMs { get; private set; }

    public void Start()
    {
        lock (_sync)
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => LoopAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_running) return;
            _running = false;
            _cts?.Cancel();
        }
    }

    /// <summary>
    /// AUDIO THREAD — realtime contract: no alloc, no lock, no IO, no throw.
    ///
    /// Deliberately LOCK-FREE. An earlier draft took a mutex that the decode
    /// worker also held while snapshotting the ring; that is the classic
    /// priority-inversion shape (a long consumer hold starving the audio
    /// producer) and it has bitten this codebase before. Single producer, single
    /// consumer: we write samples then publish the index with a release store.
    /// The consumer takes the last slot's worth of samples ending at that index —
    /// data it reads is ~15 s old and cannot be overwritten before it finishes,
    /// because the ring holds 20 s.
    /// </summary>
    public void Push(ReadOnlySpan<float> samples, int sampleRate, int receiver)
    {
        var ring = _ring;
        int w = _write;                       // only this thread writes _write
        for (int i = 0; i < samples.Length; i++)
        {
            ring[w] = samples[i];
            if (++w == ring.Length) w = 0;
        }
        Volatile.Write(ref _srcRate, sampleRate);
        Volatile.Write(ref _receiver, receiver);
        Volatile.Write(ref _write, w);        // release: publish after the samples
    }

    /// <summary>
    /// Slot watcher. Wakes shortly after each boundary, snapshots the slot that
    /// just ended, and dispatches a decode.
    /// </summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);

                double now = _clock.UtcNowMs;
                long slot = SlotClock.SlotIndex(now, DigitalMode.Ft8);
                if (slot == _currentSlot) continue;

                long ended = slot - 1;
                _currentSlot = slot;
                if (ended < 0) continue;

                float[] audio;
                int rate, rx;
                // Lock-free snapshot: acquire the published index, then copy.
                // We never block the audio thread to read.
                int w = Volatile.Read(ref _write);
                rate = Volatile.Read(ref _srcRate);
                rx = Volatile.Read(ref _receiver);
                if (rate <= 0) continue;             // no RX audio yet

                // Hand the decoder EXACTLY the slot that just ended, aligned to
                // its boundary.
                //
                // An earlier draft passed the whole 20 s ring. ft8_lib's monitor
                // starts at sample 0 and fills one slot of blocks, so it analysed
                // -5 s..+10 s relative to the slot start — a 5 s misalignment
                // against a protocol that tolerates roughly +/-2.5 s of dt. The
                // result was frames arriving on time with decodes:[] forever, on
                // any band. Slot maths that is "close enough" is not close enough.
                //
                // `w` is the write head, i.e. now. We woke up to 100 ms after the
                // boundary, so step back that far to find the boundary in the
                // ring, then take the preceding slot.
                double msSinceBoundary = now - SlotClock.SlotStartMs(slot, DigitalMode.Ft8);
                int samplesSinceBoundary = (int)(msSinceBoundary * rate / 1000.0);
                int slotSamples = SlotClock.SlotMs(DigitalMode.Ft8) * rate / 1000;

                int end = w - samplesSinceBoundary;   // ring index of the boundary
                audio = Snapshot(end, slotSamples);
                if (audio.Length == 0) continue;

                var sw = Stopwatch.StartNew();
                IReadOnlyList<Ft8DecodeDto> decodes;
                try
                {
                    decodes = DecodeSlot(audio, rate);
                }
                catch
                {
                    decodes = Array.Empty<Ft8DecodeDto>();
                }
                sw.Stop();
                LastLatencyMs = sw.Elapsed.TotalMilliseconds;

                // Publish even when empty: the store keys off slot boundaries and
                // the UI's "listening" state depends on seeing slots tick by.
                _events.PublishFt8Decode(new Ft8DecodeBatch
                {
                    Receiver = rx,
                    SlotStartUnixMs = (long)SlotClock.SlotStartMs(ended, DigitalMode.Ft8),
                    Protocol = "FT8",
                    Decodes = decodes,
                });
            }
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    // ---- SEAM ---------------------------------------------------------------

    /// <summary>
    /// Copy <paramref name="count"/> samples ending at ring index
    /// <paramref name="end"/>, oldest-first. Runs on the worker, never the audio
    /// thread. Returns empty if the window is larger than the ring.
    /// </summary>
    private float[] Snapshot(int end, int count)
    {
        var ring = _ring;
        if (count <= 0 || count > ring.Length) return Array.Empty<float>();

        // Normalise `end` into [0, ring.Length) — it can go negative when the
        // boundary sits before the current write head's wrap point.
        end %= ring.Length;
        if (end < 0) end += ring.Length;

        int start = end - count;
        if (start < 0) start += ring.Length;

        var outBuf = new float[count];
        if (start + count <= ring.Length)
        {
            Array.Copy(ring, start, outBuf, 0, count);
        }
        else
        {
            int first = ring.Length - start;
            Array.Copy(ring, start, outBuf, 0, first);
            Array.Copy(ring, 0, outBuf, first, count - first);
        }
        return outBuf;
    }

    /// <summary></summary>
    /// Decode one slot. Wire ft8_lib here.
    ///
    /// Steps: resample <paramref name="rate"/> → 12 kHz, hand the slot's samples
    /// to ft8_lib's monitor/decode, map each candidate to Ft8DecodeDto (snrDb,
    /// dtSec measured against the DISCIPLINED clock, freqHz, text), and enrich
    /// `country` from the callsign prefix.
    ///
    /// dtSec MUST be measured against ClockService.UtcNowMs, not DateTime.UtcNow —
    /// otherwise every decode inherits the host clock's error and slot parity can
    /// flip under the sequencer.
    /// </summary>
    private IReadOnlyList<Ft8DecodeDto> DecodeSlot(float[] audio, int rate)
        => Ft8Native.Decode(audio, rate, isFt4: false);

    public void Dispose()
    {
        Stop();
        try { _worker?.Wait(2000); } catch { }
        _cts?.Dispose();
    }
}
