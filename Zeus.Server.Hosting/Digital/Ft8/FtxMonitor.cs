// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// The FT8/FT4 "monitor", ported from ft8_lib's common/monitor.c (Karlis Goba,
// MIT; see LICENSE.ft8_lib): a Hann-windowed STFT of the slot, one FFT per
// half symbol (time_osr = 2) at twice the tone resolution (freq_osr = 2), kept
// as a waterfall of magnitudes in 0.5 dB steps (a byte: 0..255 ↔ -120..+7.5
// dB). Candidate search and soft demodulation read only this waterfall.

namespace Zeus.Server.Hosting.Digital.Ft8;

/// <summary>ftx_waterfall_t: mag[block][time_sub][freq_sub][bin].</summary>
internal sealed class FtxWaterfall
{
    public required int MaxBlocks { get; init; }
    public int NumBlocks { get; set; }
    public required int NumBins { get; init; }
    public required int TimeOsr { get; init; }
    public required int FreqOsr { get; init; }
    public required byte[] Mag { get; init; }
    public int BlockStride => TimeOsr * FreqOsr * NumBins;
    public required bool IsFt4 { get; init; }
}

internal sealed class FtxMonitor
{
    public float SymbolPeriod { get; }
    public int MinBin { get; }
    public int MaxBin { get; }
    public int BlockSize { get; }
    public FtxWaterfall Wf { get; }

    private readonly int _subblockSize;
    private readonly int _nfft;
    private readonly float[] _window;
    private readonly float[] _lastFrame;
    private readonly float[] _timedata;
    private readonly Cpx[] _freqdata;
    private readonly KissFftr _fft;

    /// <summary>monitor_init() with Zeus's configuration: fMin..fMax Hz,
    /// time_osr = freq_osr = 2.</summary>
    public FtxMonitor(bool isFt4, int sampleRate, float fMin = 100, float fMax = 3000,
                      int timeOsr = 2, int freqOsr = 2)
    {
        float slotTime = isFt4 ? FtxConstants.Ft4SlotTime : FtxConstants.Ft8SlotTime;
        float symbolPeriod = isFt4 ? FtxConstants.Ft4SymbolPeriod : FtxConstants.Ft8SymbolPeriod;

        // Float arithmetic then truncation, as the C does it.
        BlockSize = (int)(sampleRate * symbolPeriod);
        _subblockSize = BlockSize / timeOsr;
        _nfft = BlockSize * freqOsr;
        float fftNorm = 2.0f / _nfft;

        _window = new float[_nfft];
        for (int i = 0; i < _nfft; ++i) _window[i] = fftNorm * HannI(i, _nfft);
        _lastFrame = new float[_nfft];
        _timedata = new float[_nfft];
        _freqdata = new Cpx[_nfft / 2 + 1];
        _fft = new KissFftr(_nfft);

        int maxBlocks = (int)(slotTime / symbolPeriod);
        MinBin = (int)(fMin * symbolPeriod);
        MaxBin = (int)(fMax * symbolPeriod) + 1;
        int numBins = MaxBin - MinBin;

        Wf = new FtxWaterfall
        {
            MaxBlocks = maxBlocks,
            NumBins = numBins,
            TimeOsr = timeOsr,
            FreqOsr = freqOsr,
            Mag = new byte[maxBlocks * timeOsr * freqOsr * numBins],
            IsFt4 = isFt4,
        };
        SymbolPeriod = symbolPeriod;
    }

    private static float HannI(int i, int n)
    {
        float x = MathF.Sin(MathF.PI * i / n);
        return x * x;
    }

    /// <summary>monitor_process(): one symbol's worth of samples (BlockSize).</summary>
    public void Process(ReadOnlySpan<float> frame)
    {
        if (Wf.NumBlocks >= Wf.MaxBlocks) return;

        int offset = Wf.NumBlocks * Wf.BlockStride;
        int framePos = 0;

        for (int timeSub = 0; timeSub < Wf.TimeOsr; ++timeSub)
        {
            // Shift the new sub-block into the analysis frame.
            Array.Copy(_lastFrame, _subblockSize, _lastFrame, 0, _nfft - _subblockSize);
            for (int pos = _nfft - _subblockSize; pos < _nfft; ++pos) _lastFrame[pos] = frame[framePos++];

            for (int pos = 0; pos < _nfft; ++pos) _timedata[pos] = _window[pos] * _lastFrame[pos];
            _fft.Transform(_timedata, _freqdata);

            for (int freqSub = 0; freqSub < Wf.FreqOsr; ++freqSub)
            {
                for (int bin = MinBin; bin < MaxBin; ++bin)
                {
                    int srcBin = bin * Wf.FreqOsr + freqSub;
                    float mag2 = _freqdata[srcBin].I * _freqdata[srcBin].I + _freqdata[srcBin].R * _freqdata[srcBin].R;
                    float db = 10.0f * MathF.Log10(1E-12f + mag2);
                    // 0..255 covers -120..+7.5 dB in 0.5 dB steps.
                    int scaled = (int)(2 * db + 240);
                    Wf.Mag[offset++] = (byte)(scaled < 0 ? 0 : scaled > 255 ? 255 : scaled);
                }
            }
        }
        ++Wf.NumBlocks;
    }
}
