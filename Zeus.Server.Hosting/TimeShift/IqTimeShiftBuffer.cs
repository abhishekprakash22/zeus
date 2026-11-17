// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// The last stretch of RX1's I/Q, kept in memory so it can be listened to
// again. A ring of interleaved float32 I/Q at the DDC rate. Every sample is
// tagged — by segment — with the hardware centre frequency it was captured
// at, so a replay can place its passband correctly even after the radio has
// re-centred since.
//
// Threading: Append / Read / Ensure / Release run on the RX loop thread only.
// The counters are read from other threads for status (volatile reads).

namespace Zeus.Server;

internal sealed class IqTimeShiftBuffer
{
    /// <summary>Hard ceiling on the ring, whatever length is asked for: a
    /// minute at 192 kHz is ~92 MB; the high DDC rates would otherwise take
    /// most of a gigabyte.</summary>
    internal const long MaxBytes = 128L * 1024 * 1024;
    private const int BytesPerSample = 2 * sizeof(float);

    private float[] _ring = Array.Empty<float>();
    private int _capacity;          // complex samples
    private int _rateHz;
    private long _written;          // complex samples appended since the last reset
    private readonly List<(long Start, long LoHz)> _segments = new();

    public int RateHz => Volatile.Read(ref _rateHz);
    public int CapacitySamples => Volatile.Read(ref _capacity);
    /// <summary>Total samples appended since the last reset; the newest sample is Written − 1.</summary>
    public long Written => Volatile.Read(ref _written);
    /// <summary>Index of the oldest sample still held.</summary>
    public long Oldest => Math.Max(0, Written - CapacitySamples);
    public long MemoryBytes => (long)CapacitySamples * BytesPerSample;

    /// <summary>Samples a ring of <paramref name="seconds"/> at
    /// <paramref name="rateHz"/> holds, after the memory ceiling.</summary>
    internal static int CapacityFor(int rateHz, int seconds)
    {
        if (rateHz <= 0 || seconds <= 0) return 0;
        return (int)Math.Min((long)rateHz * seconds, MaxBytes / BytesPerSample);
    }

    /// <summary>Size the ring for this rate and length. A change of either
    /// drops what was held (old samples are at the wrong rate) and returns true.</summary>
    public bool Ensure(int rateHz, int seconds)
    {
        int want = CapacityFor(rateHz, seconds);
        if (rateHz == _rateHz && want == _capacity) return false;
        _ring = want > 0 ? new float[want * 2] : Array.Empty<float>();
        _segments.Clear();
        Volatile.Write(ref _written, 0);
        Volatile.Write(ref _rateHz, rateHz);
        Volatile.Write(ref _capacity, want);
        return true;
    }

    /// <summary>Give the memory back (rewind switched off).</summary>
    public void Release()
    {
        if (_capacity == 0) return;
        _ring = Array.Empty<float>();
        _segments.Clear();
        Volatile.Write(ref _written, 0);
        Volatile.Write(ref _rateHz, 0);
        Volatile.Write(ref _capacity, 0);
    }

    /// <summary>Append interleaved I/Q captured with the hardware centred on
    /// <paramref name="loHz"/>.</summary>
    public void Append(ReadOnlySpan<double> iq, long loHz)
    {
        int cap = _capacity;
        int n = iq.Length / 2;
        if (cap == 0 || n == 0) return;
        if (n > cap)
        {
            // A single block longer than the ring: only its tail can be kept.
            iq = iq.Slice((n - cap) * 2);
            _written += n - cap;
            n = cap;
        }
        if (_segments.Count == 0 || _segments[^1].LoHz != loHz)
            _segments.Add((_written, loHz));

        var ring = _ring;
        int pos = (int)(_written % cap);
        for (int i = 0; i < n; i++)
        {
            ring[2 * pos] = (float)iq[2 * i];
            ring[2 * pos + 1] = (float)iq[2 * i + 1];
            if (++pos == cap) pos = 0;
        }
        long written = _written + n;
        Volatile.Write(ref _written, written);

        // Forget segments that have fallen entirely out of the ring.
        long oldest = Math.Max(0, written - cap);
        while (_segments.Count > 1 && _segments[1].Start <= oldest)
            _segments.RemoveAt(0);
    }

    /// <summary>
    /// Read samples starting at index <paramref name="from"/> into
    /// <paramref name="dest"/> (interleaved). Stops at the end of the data, at
    /// the size of <paramref name="dest"/>, or at the next change of hardware
    /// centre — so every read has exactly one <paramref name="loHz"/>. A
    /// position older than the ring holds is read from the oldest sample.
    /// Returns the number of complex samples read.
    /// </summary>
    public int Read(long from, Span<double> dest, out long loHz)
    {
        loHz = 0;
        int cap = _capacity;
        long written = _written;
        if (cap == 0 || _segments.Count == 0) return 0;
        long oldest = Math.Max(0, written - cap);
        if (from < oldest) from = oldest;
        if (from >= written) return 0;

        long end = written;
        int seg = _segments.Count - 1;
        while (seg > 0 && _segments[seg].Start > from) seg--;
        loHz = _segments[seg].LoHz;
        if (seg + 1 < _segments.Count) end = Math.Min(end, _segments[seg + 1].Start);

        int n = (int)Math.Min(Math.Min(end - from, dest.Length / 2), int.MaxValue);
        var ring = _ring;
        int pos = (int)(from % cap);
        for (int i = 0; i < n; i++)
        {
            dest[2 * i] = ring[2 * pos];
            dest[2 * i + 1] = ring[2 * pos + 1];
            if (++pos == cap) pos = 0;
        }
        return n;
    }
}
