// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Protocol2;

namespace Zeus.Server;

internal sealed class WidebandSpectrumAnalyzer
{
    public const int DisplayWidth = 2048;
    public const double DisplaySpanHz = 60_000_000.0;
    public const long DisplayCenterHz = 30_000_000;
    public const float HzPerPixel = (float)(DisplaySpanHz / DisplayWidth);

    private const int FftSize = Protocol2Client.WidebandFrameSamples;
    private const double MinAmplitude = 1e-12;

    private readonly double[] _real = new double[FftSize];
    private readonly double[] _imag = new double[FftSize];
    private readonly double[] _window = new double[FftSize];
    private readonly int[] _bitReverse = new int[FftSize];
    private readonly double[] _stageCos;
    private readonly double[] _stageSin;
    private readonly int[] _pixelStartBin = new int[DisplayWidth];
    private readonly int[] _pixelEndBin = new int[DisplayWidth];
    private readonly double _windowSum;
    private int _sampleRateHz;

    public WidebandSpectrumAnalyzer()
    {
        double windowSum = 0.0;
        for (int i = 0; i < FftSize; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (FftSize - 1));
            _window[i] = w;
            windowSum += w;
        }
        _windowSum = windowSum;

        int bits = 0;
        for (int n = FftSize; n > 1; n >>= 1) bits++;
        for (int i = 0; i < FftSize; i++)
            _bitReverse[i] = ReverseBits(i, bits);

        _stageCos = new double[bits];
        _stageSin = new double[bits];
        for (int stage = 0, len = 2; len <= FftSize; stage++, len <<= 1)
        {
            double angle = -2.0 * Math.PI / len;
            _stageCos[stage] = Math.Cos(angle);
            _stageSin[stage] = Math.Sin(angle);
        }

        RebuildPixelMap(Protocol2Client.WidebandAdcSampleRateHz);
    }

    public void Analyze(ReadOnlySpan<short> samples, int sampleRateHz, Span<float> panDb, Span<float> wfDb)
    {
        if (panDb.Length < DisplayWidth || wfDb.Length < DisplayWidth)
            throw new ArgumentException("Output spans must be at least DisplayWidth samples long.");

        if (sampleRateHz <= 0) sampleRateHz = Protocol2Client.WidebandAdcSampleRateHz;
        if (sampleRateHz != _sampleRateHz) RebuildPixelMap(sampleRateHz);

        int copy = Math.Min(samples.Length, FftSize);
        for (int i = 0; i < copy; i++)
            _real[i] = samples[i] * _window[i];
        if (copy < FftSize) Array.Clear(_real, copy, FftSize - copy);
        Array.Clear(_imag, 0, _imag.Length);

        FftInPlace();

        double baseScale = 1.0 / (_windowSum * 32768.0);
        for (int pixel = 0; pixel < DisplayWidth; pixel++)
        {
            int start = _pixelStartBin[pixel];
            int end = _pixelEndBin[pixel];
            if (end <= start)
            {
                panDb[pixel] = -200f;
                wfDb[pixel] = -200f;
                continue;
            }

            double power = 0.0;
            int count = 0;
            for (int bin = start; bin < end; bin++)
            {
                double scale = bin == 0 ? baseScale : 2.0 * baseScale;
                double re = _real[bin] * scale;
                double im = _imag[bin] * scale;
                power += re * re + im * im;
                count++;
            }

            double amplitude = Math.Sqrt(power / Math.Max(1, count));
            float db = (float)(20.0 * Math.Log10(Math.Max(amplitude, MinAmplitude)));
            panDb[pixel] = db;
            wfDb[pixel] = db;
        }
    }

    private void RebuildPixelMap(int sampleRateHz)
    {
        _sampleRateHz = sampleRateHz;
        double binHz = sampleRateHz / (double)FftSize;
        int maxPositiveBin = FftSize / 2;
        for (int pixel = 0; pixel < DisplayWidth; pixel++)
        {
            double loHz = pixel * HzPerPixel;
            double hiHz = (pixel + 1) * HzPerPixel;
            int start = (int)Math.Floor(loHz / binHz);
            int end = (int)Math.Ceiling(hiHz / binHz);
            start = Math.Clamp(start, 0, maxPositiveBin);
            end = Math.Clamp(end, 0, maxPositiveBin);
            if (end <= start && start < maxPositiveBin) end = start + 1;
            _pixelStartBin[pixel] = start;
            _pixelEndBin[pixel] = end;
        }
    }

    private void FftInPlace()
    {
        for (int i = 0; i < FftSize; i++)
        {
            int j = _bitReverse[i];
            if (i >= j) continue;
            (_real[i], _real[j]) = (_real[j], _real[i]);
            (_imag[i], _imag[j]) = (_imag[j], _imag[i]);
        }

        for (int stage = 0, len = 2; len <= FftSize; stage++, len <<= 1)
        {
            int half = len >> 1;
            double wLenRe = _stageCos[stage];
            double wLenIm = _stageSin[stage];
            for (int i = 0; i < FftSize; i += len)
            {
                double wRe = 1.0;
                double wIm = 0.0;
                for (int j = 0; j < half; j++)
                {
                    int even = i + j;
                    int odd = even + half;
                    double oddRe = _real[odd] * wRe - _imag[odd] * wIm;
                    double oddIm = _real[odd] * wIm + _imag[odd] * wRe;
                    double evenRe = _real[even];
                    double evenIm = _imag[even];
                    _real[even] = evenRe + oddRe;
                    _imag[even] = evenIm + oddIm;
                    _real[odd] = evenRe - oddRe;
                    _imag[odd] = evenIm - oddIm;

                    double nextRe = wRe * wLenRe - wIm * wLenIm;
                    wIm = wRe * wLenIm + wIm * wLenRe;
                    wRe = nextRe;
                }
            }
        }
    }

    private static int ReverseBits(int value, int bits)
    {
        int result = 0;
        for (int i = 0; i < bits; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }
}
