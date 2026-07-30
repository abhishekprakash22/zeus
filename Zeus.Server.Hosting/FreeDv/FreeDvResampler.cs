// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Fixed 6:1 / 1:6 polyphase resamplers between the Zeus audio bus (48 kHz)
// and the codec2 700D/700E rates (8 kHz speech AND 8 kHz modem — both sides
// of freedv_api run at 8 kHz for the classic modes Zeus targets).
//
// Hot-path discipline: both classes allocate ONLY in the constructor; Process
// is O(N), lock-free, and exception-free, so it may run inside ProcessRx /
// ProcessTx under the IAudioModemPlugin realtime contract. The FIR is a
// 96-tap Hamming-windowed sinc (16 taps per phase × 6 phases), cutoff 3.4 kHz
// at 48 kHz — comfortably inside the 4 kHz Nyquist of the 8 kHz side and flat
// across the 300–2700 Hz FreeDV passband. Group delay ≈ 1 ms per direction,
// negligible against the modem's own frame latency.

namespace Zeus.Server.Hosting.FreeDv;

/// <summary>48 kHz float → 8 kHz float, streaming, persistent phase.</summary>
internal sealed class Decimator48To8
{
    private readonly float[] _taps;   // full 96-tap prototype
    private readonly float[] _delay;  // circular delay line, length = taps
    private int _pos;                 // next write slot in _delay
    private int _phase;               // 0..5, output on phase 0

    public Decimator48To8()
    {
        _taps = FreeDvFir.Prototype();
        _delay = new float[_taps.Length];
    }

    /// <summary>
    /// Push <paramref name="in48k"/>; write decimated samples to
    /// <paramref name="out8k"/>. Returns the number of 8 kHz samples produced
    /// (≤ out8k.Length; extra outputs are dropped, which never happens when
    /// the caller sizes out8k ≥ ceil(in48k.Length / 6) + 1).
    /// </summary>
    public int Process(ReadOnlySpan<float> in48k, Span<float> out8k)
    {
        int produced = 0;
        var taps = _taps;
        var delay = _delay;
        int len = delay.Length;
        for (int i = 0; i < in48k.Length; i++)
        {
            delay[_pos] = in48k[i];
            _pos = _pos + 1 == len ? 0 : _pos + 1;
            if (++_phase == 6)
            {
                _phase = 0;
                if (produced < out8k.Length)
                {
                    float acc = 0f;
                    int idx = _pos;               // oldest sample
                    for (int t = len - 1; t >= 0; t--)
                    {
                        acc += delay[idx] * taps[t];
                        idx = idx + 1 == len ? 0 : idx + 1;
                    }
                    out8k[produced++] = acc;
                }
            }
        }
        return produced;
    }

    public void Reset()
    {
        Array.Clear(_delay);
        _pos = 0;
        _phase = 0;
    }
}

/// <summary>8 kHz float → 48 kHz float, streaming polyphase (×6 gain baked in).</summary>
internal sealed class Interpolator8To48
{
    private const int TapsPerPhase = 16;
    private readonly float[][] _phases; // [6][16], gain ×6 folded in
    private readonly float[] _delay;    // last 16 input samples, circular
    private int _pos;

    public Interpolator8To48()
    {
        var proto = FreeDvFir.Prototype();
        _phases = new float[6][];
        for (int p = 0; p < 6; p++)
        {
            _phases[p] = new float[TapsPerPhase];
            for (int t = 0; t < TapsPerPhase; t++)
                _phases[p][t] = proto[t * 6 + p] * 6f;
        }
        _delay = new float[TapsPerPhase];
    }

    /// <summary>
    /// Push <paramref name="in8k"/>; write 6× samples to
    /// <paramref name="out48k"/>. Returns samples produced
    /// (= 6 × in8k.Length when out48k is large enough).
    /// </summary>
    public int Process(ReadOnlySpan<float> in8k, Span<float> out48k)
    {
        int produced = 0;
        var delay = _delay;
        for (int i = 0; i < in8k.Length; i++)
        {
            delay[_pos] = in8k[i];
            _pos = _pos + 1 == TapsPerPhase ? 0 : _pos + 1;
            for (int p = 0; p < 6 && produced < out48k.Length; p++)
            {
                var taps = _phases[p];
                float acc = 0f;
                int idx = _pos;                  // oldest sample
                for (int t = TapsPerPhase - 1; t >= 0; t--)
                {
                    acc += delay[idx] * taps[t];
                    idx = idx + 1 == TapsPerPhase ? 0 : idx + 1;
                }
                out48k[produced++] = acc;
            }
        }
        return produced;
    }

    public void Reset()
    {
        Array.Clear(_delay);
        _pos = 0;
    }
}

internal static class FreeDvFir
{
    /// <summary>96-tap Hamming-windowed sinc, fc = 3.4 kHz @ 48 kHz, unity DC gain.</summary>
    public static float[] Prototype()
    {
        const int n = 96;
        const double fc = 3400.0 / 48000.0; // normalized cutoff
        var taps = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            double m = i - (n - 1) / 2.0;
            double sinc = m == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * m) / (Math.PI * m);
            double w = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (n - 1));
            taps[i] = sinc * w;
            sum += taps[i];
        }
        var f = new float[n];
        for (int i = 0; i < n; i++) f[i] = (float)(taps[i] / sum);
        return f;
    }
}
