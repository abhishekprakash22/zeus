// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Streaming FM demodulator for SSTV at 12 kHz: mix the real audio down by
// 1900 Hz (the middle of the 1100–2300 Hz SSTV band), low-pass the complex
// baseband, and read the instantaneous frequency off the phase step between
// consecutive samples. One output (Hz) per input sample.
//
// The FIR is linear-phase, so the whole frequency track lags the audio by a
// constant (Taps−1)/2 samples. The decoder measures EVERYTHING (VIS edge, sync
// pulses, pixels) on this same track, so that lag cancels and is never
// compensated anywhere.

namespace Zeus.Server.Hosting.Digital.Sstv;

internal sealed class FmDemodulator
{
    public const int SampleRate = 12_000;
    public const double CenterHz = 1900;
    private const int Taps = 63;
    private const double CutoffHz = 950;
    // 1900 / 12000 = 19/120 → the mixer repeats exactly every 120 samples.
    private const int NcoPeriod = 120;

    private static readonly float[] H = BuildTaps();
    private static readonly float[] Cos = new float[NcoPeriod];
    private static readonly float[] Sin = new float[NcoPeriod];

    static FmDemodulator()
    {
        for (int i = 0; i < NcoPeriod; i++)
        {
            double ph = 2 * Math.PI * CenterHz * i / SampleRate;
            Cos[i] = (float)Math.Cos(ph);
            Sin[i] = (float)Math.Sin(ph);
        }
    }

    // Doubled delay lines: write at pos and pos+Taps so the FIR reads one
    // contiguous window without modulo.
    private readonly float[] _re = new float[2 * Taps];
    private readonly float[] _im = new float[2 * Taps];
    private int _pos, _nco;
    private float _prevRe, _prevIm;

    public void Reset()
    {
        Array.Clear(_re); Array.Clear(_im);
        _pos = 0; _nco = 0; _prevRe = 0; _prevIm = 0;
    }

    public float Process(float x)
    {
        float mr = x * Cos[_nco], mi = -x * Sin[_nco];
        if (++_nco == NcoPeriod) _nco = 0;

        _re[_pos] = _re[_pos + Taps] = mr;
        _im[_pos] = _im[_pos + Taps] = mi;
        if (++_pos == Taps) _pos = 0;

        float zr = 0, zi = 0;
        // Window starts at the oldest sample (_pos) and runs Taps long.
        for (int t = 0; t < Taps; t++)
        {
            float h = H[t];
            zr += _re[_pos + t] * h;
            zi += _im[_pos + t] * h;
        }

        // z · conj(prev) → phase step → frequency.
        float dr = zr * _prevRe + zi * _prevIm;
        float di = zi * _prevRe - zr * _prevIm;
        _prevRe = zr; _prevIm = zi;
        return (float)(CenterHz + Math.Atan2(di, dr) * SampleRate / (2 * Math.PI));
    }

    private static float[] BuildTaps()
    {
        var h = new double[Taps];
        double fc = CutoffHz / SampleRate, sum = 0;
        for (int i = 0; i < Taps; i++)
        {
            double m = i - (Taps - 1) / 2.0;
            double sinc = m == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * m) / (Math.PI * m);
            double win = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (Taps - 1));
            h[i] = sinc * win; sum += h[i];
        }
        var f = new float[Taps];
        for (int i = 0; i < Taps; i++) f[i] = (float)(h[i] / sum);
        return f;
    }
}
