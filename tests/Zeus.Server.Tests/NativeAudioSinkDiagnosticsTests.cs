// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// issue #733/#742: the native-output cushion stops intermittent RX crackle
/// (output-ring underflow splicing silence under DSP-tick jitter). It was
/// raised 20 ms -> 60 ms (#733) and then to a 120 ms floor that is also made
/// adaptive to the device's real callback period (#742, the dominant cause: a
/// bursty ~33 ms producer vs a steady device drain, a thin cushion, and a
/// possibly-large negotiated period). The underrun/overrun/rebuffer counters
/// are surfaced via <see cref="NativeAudioSink.GetDiagnostics"/> (and
/// /api/audio/native) so the condition is measurable rather than log-only.
/// </summary>
public sealed class NativeAudioSinkDiagnosticsTests
{
    [Fact]
    public void GetDiagnostics_FreshSink_ReportsCushionAndZeroCounters()
    {
        // Constructing the sink does NOT open an audio device (that happens in
        // StartAsync), so this is safe in a headless test.
        using var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);

        var d = sink.GetDiagnostics();

        Assert.Equal(5760, d.PrebufferSamples);   // ~120 ms @ 48 kHz floor (#742)
        Assert.Equal(48_000, d.SampleRateHz);
        Assert.Equal(65_536, d.RingCapacitySamples);
        Assert.Equal(0L, d.UnderrunSamplesTotal);
        Assert.Equal(0L, d.OverrunSamplesTotal);
        Assert.Equal(0L, d.RebufferEvents);
        Assert.Equal(0, d.RingDepthSamples);
        Assert.False(d.OutputOpen);
        Assert.Null(d.ConfiguredOutputDeviceId);
        Assert.Null(d.ActiveOutputDeviceId);
        Assert.Equal(0, d.OutputSampleRateHz);
        Assert.Equal(0, d.OutputChannels);
        Assert.Equal(0L, d.TotalSamplesIn);
        Assert.Equal(0L, d.TotalSamplesOut);
        Assert.False(d.PreviewEnabled);
        Assert.Equal(0, d.PreviewRingDepthSamples);
        Assert.Equal(16_384, d.PreviewRingCapacitySamples);
        Assert.Equal(0L, d.PreviewSamplesIn);
        Assert.Equal(0L, d.PreviewSamplesOut);
        Assert.Equal(0L, d.DroppedFormatSamplesTotal);
        Assert.Equal(0L, d.DroppedMutedSamplesTotal);
        Assert.Equal(0, d.LastInputSampleRateHz);
        Assert.Equal(0, d.LastInputChannels);
        // A fresh sink starts in the rebuffering state — it holds silence until
        // the prebuffer cushion fills before (re)starting playback.
        Assert.True(d.Rebuffering);
    }

    [Theory]
    // Small/typical device period (10 ms @ 48 kHz): the 120 ms floor dominates.
    [InlineData(480, 5760)]
    [InlineData(960, 5760)]
    [InlineData(1024, 5760)]
    // Large negotiated period: cushion grows to 4× the callback so a big-period
    // default device can't outrun a thin fixed cushion (#742 hypothesis #2).
    [InlineData(2048, 8192)]
    [InlineData(4096, 16384)]
    // Degenerate inputs stay safe (never negative, never >= ring capacity).
    [InlineData(0, 5760)]
    public void ComputePrebufferTarget_IsFloorOrFourCallbacks_Clamped(int totalFrames, int expected)
    {
        Assert.Equal(expected, NativeAudioSink.ComputePrebufferTarget(totalFrames));
    }

    [Fact]
    public void ComputePrebufferTarget_NeverMeetsOrExceedsRingCapacity()
    {
        // A target >= capacity would wedge the sink in permanent rebuffering.
        Assert.True(NativeAudioSink.ComputePrebufferTarget(40_000) <= 65_536 - 40_000);
        Assert.True(NativeAudioSink.ComputePrebufferTarget(60_000) < 65_536);
    }

    [Fact]
    public void GetDiagnostics_ReportsPreviewSideChannelDepthAndThroughput()
    {
        using var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);
        var samples = Enumerable.Repeat(0.25f, 480).ToArray();

        sink.SetEnabled(true);
        sink.PublishPreview(samples, 48_000);

        var queued = sink.GetDiagnostics();
        Assert.True(queued.PreviewEnabled);
        Assert.Equal(480, queued.PreviewRingDepthSamples);
        Assert.Equal(480L, queued.PreviewSamplesIn);
        Assert.Equal(0L, queued.PreviewSamplesOut);

        Span<float> output = stackalloc float[480];
        sink.RenderPlaybackForTest(output, frameCount: 480, channels: 1);

        var drained = sink.GetDiagnostics();
        Assert.Equal(0, drained.PreviewRingDepthSamples);
        Assert.Equal(480L, drained.PreviewSamplesOut);
        Assert.Contains(output.ToArray(), sample => sample > 0f);
    }
}
