// SPDX-License-Identifier: GPL-2.0-or-later
//
// FreeDV transmit gain staging (field: 'TX audio very low, mic slider has no
// effect'). The mic gain now scales the SPEECH before the vocoder, WDSP's panel
// gain is unity in FreeDV, and the leveler ceiling lands OFDM peaks near -1 dBFS.

using Xunit;

namespace Zeus.Server.Tests;

public class FreeDvTxGainTests
{
    [Fact]
    public void PanelGain_IsUnityInFreeDv_AndTheMicGainOtherwise()
    {
        Assert.Equal(1.0, DspPipelineService.EffectiveTxPanelGainLinear(freeDvMode: true, micGainDb: -20));
        Assert.Equal(1.0, DspPipelineService.EffectiveTxPanelGainLinear(freeDvMode: true, micGainDb: 10));
        Assert.Equal(Math.Pow(10, -6 / 20.0), DspPipelineService.EffectiveTxPanelGainLinear(false, -6), 9);
    }

    [Fact]
    public void SpeechGain_ScalesTheSpeech_AndClampsToFullScale()
    {
        float[] block = [0.1f, -0.1f, 0.5f, -0.5f];
        TxAudioIngest.ApplySpeechGain(block, 2f);
        Assert.Equal(new[] { 0.2f, -0.2f, 1f, -1f }, block);
    }

    [Fact]
    public void SpeechGain_UnityOrInvalid_LeavesTheBlockUntouched()
    {
        float[] block = [0.3f, -0.7f];
        TxAudioIngest.ApplySpeechGain(block, 1f);
        TxAudioIngest.ApplySpeechGain(block, float.NaN);
        Assert.Equal(new[] { 0.3f, -0.7f }, block);
    }

    [Fact]
    public void LevelerCeiling_LandsModemPeaksNearFullScale()
    {
        // ~-20 dBFS codec2 modem peaks + the ceiling: within a dB or two of full
        // scale, never above it (ALC handles strays).
        double peakDbfs = -20.0 + DspPipelineService.FreeDvLevelerMaxGainDb;
        Assert.InRange(peakDbfs, -2.0, 0.0);
    }
}
