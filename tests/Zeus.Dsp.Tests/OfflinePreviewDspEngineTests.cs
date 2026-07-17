// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Dsp.Wdsp;

namespace Zeus.Dsp.Tests;

[Collection("Wdsp")]
public sealed class OfflinePreviewDspEngineTests
{
    [SkippableFact]
    public void PreviewEngine_HostsTxChainWithoutPublishingSyntheticDisplay()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        using var engine = new OfflinePreviewDspEngine(NullLogger<WdspDspEngine>.Instance);
        int channel = engine.OpenChannel(sampleRateHz: 192_000, pixelWidth: 1024);
        engine.OpenTxChannel(outputRateHz: 192_000);

        Assert.Equal(512, engine.TxBlockSamples);
        Assert.Equal(2048, engine.TxOutputSamples);

        Span<float> pan = stackalloc float[1024];
        Assert.False(engine.TryGetDisplayPixels(channel, DisplayPixout.Panadapter, pan));

        bool pluginRan = false;
        engine.SetTxAudioPluginHandler((input, output, frames, channels, sampleRate) =>
        {
            pluginRan = true;
            input.CopyTo(output);
        });
        Assert.True(engine.HasTxAudioPluginHandler);

        engine.SetTxMonitorEnabled(true);
        Assert.True(engine.IsTxMonitorOn);

        float[] mic = BuildTone(engine.TxBlockSamples, 1_000.0f, 0.01f);
        float[] iq = new float[2 * engine.TxOutputSamples];
        int produced = 0;
        for (int i = 0; i < 16; i++)
            produced = engine.ProcessTxBlock(mic, iq);

        Assert.True(pluginRan);
        Assert.Equal(engine.TxOutputSamples, produced);
        Assert.Contains(iq, sample => Math.Abs(sample) > 1e-6f);
    }

    private static float[] BuildTone(int samples, float frequencyHz, float amplitude)
    {
        var tone = new float[samples];
        double phaseStep = 2.0 * Math.PI * frequencyHz / 48_000.0;
        for (int i = 0; i < tone.Length; i++)
            tone[i] = amplitude * (float)Math.Sin(i * phaseStep);
        return tone;
    }

    private static bool WdspAvailable()
    {
        try { return WdspNativeLoader.TryProbe(); }
        catch { return false; }
    }
}
