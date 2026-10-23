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
// AF analysis — what the filter card's audio views draw (audio spectrum,
// scope, audio waterfall, CW pitch, levels). The audio is computed HERE, on
// the radio: on the G2 the audio plays from the server and never reaches the
// kiosk browser, so a browser-side analyser would have nothing to analyse.
//
// Sources: each receiver's FINAL audio (after demod, NR, notch, FreeDV decode
// and its own mute, before the mix) and, while keyed, the TX mic block.
// Each is decimated 48 kHz → 8 kHz (0..4 kHz is the whole SSB/CW audio band)
// into a 2048-sample ring; at most ~15 times a second a frame is built from
// it — a 1024-point Hann FFT (512 bins, 7.8 Hz each — fine enough to see CW
// pitch, carriers and voice harmonics) and the last 512 samples for the
// scope, NORMALISED to their own peak so quiet audio still uses the full
// 8-bit range (the true level travels as peak dBFS) — and broadcast as
// MsgType.AfAnalysis (0x3E).
//
// Cost control: nothing is computed or sent unless a client holds a lease for
// that source (POST /api/audio-analysis/subscribe, renewed every ~2 s while a
// card shows an audio view; a lease lapses after 6 s). An idle radio pays one
// dictionary lookup per audio block.

using System.Collections.Concurrent;

namespace Zeus.Server;

public sealed class AfAnalysisService
{
    public const byte TxSource = 0xFF;
    public const int OutputRateHz = 8000;
    public const int DecimationFactor = 6; // 48 kHz → 8 kHz
    public const int FftSize = 1024;
    public const int BinCount = FftSize / 2;
    public const int ScopeCount = 512;
    public const int RingSize = 2048;
    public const double LeaseSeconds = 6.0;
    public const double MinIntervalMs = 66.0; // ~15 frames/s per source
    public const int MaxSources = 16;

    private readonly StreamingHub? _hub;
    private readonly ConcurrentDictionary<byte, long> _leaseUntilTicks = new();
    private readonly ConcurrentDictionary<byte, SourceState> _sources = new();
    private readonly Func<long> _nowTicks;

    // Hann window and FFT tables, shared (read-only after construction).
    private static readonly float[] Window = BuildWindow();
    private static readonly int[] BitReverse = BuildBitReverse();
    private static readonly float[] CosTable = BuildTwiddles(true);
    private static readonly float[] SinTable = BuildTwiddles(false);

    public AfAnalysisService(StreamingHub hub) : this(hub, () => DateTime.UtcNow.Ticks) { }

    /// <summary>Test seam: no hub (frames go to <see cref="LastFrame"/>), injectable clock.</summary>
    internal AfAnalysisService(StreamingHub? hub, Func<long> nowTicks)
    {
        _hub = hub;
        _nowTicks = nowTicks;
    }

    /// <summary>The last frame built (tests read it; production broadcasts it).</summary>
    internal byte[]? LastFrame { get; private set; }

    /// <summary>Renew leases for these sources (rx indices, 0xFF = TX). Unknown values are ignored.</summary>
    public void Subscribe(IEnumerable<int> sources)
    {
        long until = _nowTicks() + TimeSpan.FromSeconds(LeaseSeconds).Ticks;
        foreach (var s in sources)
        {
            if (s == TxSource || (s >= 0 && s < MaxSources)) _leaseUntilTicks[(byte)s] = until;
        }
    }

    public bool IsLeased(byte source) =>
        _leaseUntilTicks.TryGetValue(source, out var until) && until > _nowTicks();

    /// <summary>A receiver's final audio block (48 kHz mono). Muted = analyse as silence.</summary>
    public void Feed(int rxIndex, ReadOnlySpan<float> block48k, bool muted = false)
    {
        if (rxIndex < 0 || rxIndex >= MaxSources) return;
        FeedCore((byte)rxIndex, block48k, muted);
    }

    /// <summary>The TX mic block (48 kHz mono) while keyed.</summary>
    public void FeedTx(ReadOnlySpan<float> block48k) => FeedCore(TxSource, block48k, muted: false);

    private void FeedCore(byte source, ReadOnlySpan<float> block, bool muted)
    {
        if (block.IsEmpty || !IsLeased(source)) return;
        var st = _sources.GetOrAdd(source, _ => new SourceState());
        byte[]? frame = null;
        lock (st)
        {
            for (int i = 0; i < block.Length; i++)
            {
                float v = muted ? 0f : block[i];
                if (!float.IsFinite(v)) v = 0f;
                st.Acc += v;
                if (++st.AccN == DecimationFactor)
                {
                    st.Ring[st.Write] = st.Acc / DecimationFactor;
                    st.Write = (st.Write + 1) % RingSize;
                    st.Acc = 0f;
                    st.AccN = 0;
                    if (st.Filled < RingSize) st.Filled++;
                }
            }
            long now = _nowTicks();
            if (st.Filled >= FftSize && now - st.LastEmitTicks >= TimeSpan.FromMilliseconds(MinIntervalMs).Ticks)
            {
                st.LastEmitTicks = now;
                frame = BuildFrame(source, st, muted);
            }
        }
        if (frame is null) return;
        LastFrame = frame;
        _hub?.BroadcastRaw(frame);
    }

    // Flags byte: bit0 muted; bit1 scope normalised to its peak (scale back by
    // peak dBFS); bit2 wide header (bin count is u16). A client that ignores
    // bit2 would misread the frame, so it is always set from this format on.
    public const byte FlagMuted = 0x01;
    public const byte FlagScopeNormalised = 0x02;
    public const byte FlagWideHeader = 0x04;

    /// <summary>Header bytes before the bins: type, source, flags, rate(2), bins(2), scope(2).</summary>
    public const int HeaderBytes = 1 + 1 + 1 + 2 + 2 + 2;
    public const int FrameBytes = HeaderBytes + BinCount + ScopeCount + 2;

    private static byte[] BuildFrame(byte source, SourceState st, bool muted)
    {
        var f = new byte[FrameBytes];
        f[0] = (byte)Zeus.Contracts.MsgType.AfAnalysis;
        f[1] = source;
        f[2] = (byte)((muted ? FlagMuted : 0) | FlagScopeNormalised | FlagWideHeader);
        f[3] = OutputRateHz & 0xFF;
        f[4] = OutputRateHz >> 8;
        f[5] = BinCount & 0xFF;
        f[6] = BinCount >> 8;
        f[7] = ScopeCount & 0xFF;
        f[8] = ScopeCount >> 8;

        // Oldest-first copy of the newest ScopeCount samples.
        var recent = new float[ScopeCount];
        int start = (st.Write - ScopeCount + RingSize) % RingSize;
        for (int i = 0; i < ScopeCount; i++) recent[i] = st.Ring[(start + i) % RingSize];

        // Spectrum over the newest FftSize samples.
        var re = new float[FftSize];
        var im = new float[FftSize];
        for (int i = 0; i < FftSize; i++) re[i] = recent[ScopeCount - FftSize + i] * Window[i];
        Fft(re, im);
        for (int k = 0; k < BinCount; k++)
        {
            // Amplitude normalised to a full-scale sine ≈ 0 dBFS (Hann coherent gain 0.5).
            double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * 4.0 / FftSize;
            double db = 20.0 * Math.Log10(mag + 1e-9);
            f[HeaderBytes + k] = DbToByte(db);
        }

        double sumSq = 0, peak = 0;
        for (int i = 0; i < ScopeCount; i++)
        {
            double a = Math.Abs(recent[i]);
            sumSq += recent[i] * recent[i];
            if (a > peak) peak = a;
        }
        // Scope normalised to its own peak: −60 dBFS audio still fills ±127
        // instead of rounding to a couple of counts. Silence stays silence.
        double norm = peak > 1e-6 ? 127.0 / peak : 0.0;
        int scopeAt = HeaderBytes + BinCount;
        for (int i = 0; i < ScopeCount; i++)
        {
            f[scopeAt + i] = unchecked((byte)(sbyte)Math.Clamp((int)Math.Round(recent[i] * norm), -127, 127));
        }
        f[scopeAt + ScopeCount] = unchecked((byte)DbToSbyte(20.0 * Math.Log10(Math.Sqrt(sumSq / ScopeCount) + 1e-9)));
        f[scopeAt + ScopeCount + 1] = unchecked((byte)DbToSbyte(20.0 * Math.Log10(peak + 1e-9)));
        return f;
    }

    /// <summary>−120..0 dBFS → 0..255 (clamped).</summary>
    internal static byte DbToByte(double db) =>
        (byte)Math.Clamp((int)Math.Round((db + 120.0) / 120.0 * 255.0), 0, 255);

    private static sbyte DbToSbyte(double db) => (sbyte)Math.Clamp((int)Math.Round(db), -127, 0);

    // In-place iterative radix-2 FFT (size FftSize).
    internal static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 0; i < n; i++)
        {
            int j = BitReverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size >> 1;
            int step = n / size;
            for (int startIdx = 0; startIdx < n; startIdx += size)
            {
                for (int k = 0; k < half; k++)
                {
                    float c = CosTable[k * step];
                    float s = SinTable[k * step];
                    int a = startIdx + k;
                    int b = a + half;
                    float tr = re[b] * c - im[b] * s;
                    float ti = re[b] * s + im[b] * c;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }

    private static float[] BuildWindow()
    {
        var w = new float[FftSize];
        for (int i = 0; i < FftSize; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));
        return w;
    }

    private static int[] BuildBitReverse()
    {
        var r = new int[FftSize];
        int bits = (int)Math.Round(Math.Log2(FftSize));
        for (int i = 0; i < FftSize; i++)
        {
            int x = i, y = 0;
            for (int b = 0; b < bits; b++)
            {
                y = (y << 1) | (x & 1);
                x >>= 1;
            }
            r[i] = y;
        }
        return r;
    }

    private static float[] BuildTwiddles(bool cos)
    {
        var t = new float[FftSize / 2];
        for (int k = 0; k < t.Length; k++)
        {
            double a = -2 * Math.PI * k / FftSize;
            t[k] = (float)(cos ? Math.Cos(a) : Math.Sin(a));
        }
        return t;
    }

    private sealed class SourceState
    {
        public readonly float[] Ring = new float[RingSize];
        public int Write;
        public int Filled;
        public float Acc;
        public int AccN;
        public long LastEmitTicks = long.MinValue / 2;
    }
}

public sealed record AfAnalysisSubscribeRequest(IReadOnlyList<int>? Sources);

public static class AfAnalysisEndpoints
{
    public static IEndpointRouteBuilder MapAfAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        // Lease the AF analysis feed for these sources (rx indices, 255 = TX).
        // Clients renew every ~2 s while a filter card shows an audio view.
        app.MapPost("/api/audio-analysis/subscribe", (AfAnalysisSubscribeRequest req, AfAnalysisService svc) =>
        {
            svc.Subscribe(req.Sources ?? Array.Empty<int>());
            return Results.Ok(new { leaseSeconds = AfAnalysisService.LeaseSeconds });
        });
        return app;
    }
}
