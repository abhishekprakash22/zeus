// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// Hover to listen: where a preview sits in RX1's captured window, and the
// crossfade that lays it over the ducked main audio.

using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class DspPipelineHoverPreviewTests
{
    private static StateDto State(
        RxMode mode, long vfoHz = 14_200_000, long loHz = 14_200_000, int low = 300, int high = 2700) => new(
        Status: ConnectionStatus.Connected,
        Endpoint: "192.168.1.25:1024",
        VfoHz: vfoHz,
        Mode: mode,
        FilterLowHz: low,
        FilterHighHz: high,
        SampleRate: 192_000,
        RadioLoHz: loHz);

    [Fact]
    public void Plan_shifts_from_the_ddc_centre_not_the_dial()
    {
        // Dial parked 10 kHz above the DDC centre (CTUN); the preview is
        // placed relative to the centre the I/Q was captured at.
        var p = DspPipelineService.PlanHoverPreview(State(RxMode.USB, vfoHz: 14_210_000), 14_250_000, 192_000);
        Assert.True(p.Ok);
        Assert.Equal(50_000, p.ShiftHz);
        Assert.Equal(RxMode.USB, p.EngineMode);
        Assert.Equal(300, p.Low);
        Assert.Equal(2700, p.High);
    }

    [Fact]
    public void Plan_uses_the_mode_signed_filter()
    {
        var p = DspPipelineService.PlanHoverPreview(State(RxMode.LSB, 7_100_000, 7_100_000, -2700, -300), 7_080_000, 192_000);
        Assert.True(p.Ok);
        Assert.Equal(-20_000, p.ShiftHz);
        Assert.True(p.Low < 0 && p.High < 0);
    }

    [Fact]
    public void Plan_refuses_a_frequency_outside_the_captured_window()
    {
        // ±96 kHz captured; the outer few percent are the decimation skirts.
        var p = DspPipelineService.PlanHoverPreview(State(RxMode.USB), 14_295_000, 192_000);
        Assert.False(p.Ok);
        Assert.Equal("outside the receiver's window", p.Reason);
        // The passband edge counts: a preview whose filter would hang over the
        // edge is refused even though its carrier is inside.
        Assert.False(DspPipelineService.PlanHoverPreview(State(RxMode.USB), 14_289_000, 192_000).Ok);
        Assert.True(DspPipelineService.PlanHoverPreview(State(RxMode.USB), 14_285_000, 192_000).Ok);
    }

    [Fact]
    public void Plan_needs_a_running_receiver()
    {
        Assert.False(DspPipelineService.PlanHoverPreview(State(RxMode.USB), 14_210_000, 0).Ok);
    }

    [Fact]
    public void Mix_is_transparent_when_there_is_no_preview()
    {
        float[] main = { 0.5f, -0.5f, 0.25f };
        float mix = DspPipelineService.MixHoverPreview(main, ReadOnlySpan<float>.Empty, 0f, 0f, 0.1f, 0.178f);
        Assert.Equal(0f, mix);
        Assert.Equal(new[] { 0.5f, -0.5f, 0.25f }, main);
    }

    [Fact]
    public void Mix_fades_in_to_ducked_main_plus_full_preview()
    {
        float[] main = { 1f, 1f, 1f, 1f };
        float[] preview = { 0.5f, 0.5f, 0.5f, 0.5f };
        float mix = DspPipelineService.MixHoverPreview(main, preview, 0f, 1f, 0.5f, 0.2f);
        Assert.Equal(1f, mix);
        // Sample 0: mix 0.5 → main 1·(1 − 0.5·0.8) + 0.5·0.5
        Assert.Equal(0.6f + 0.25f, main[0], 5);
        // From sample 1: mix 1 → main at the duck level + preview at full level
        Assert.Equal(0.2f + 0.5f, main[1], 5);
        Assert.Equal(0.2f + 0.5f, main[3], 5);
    }

    [Fact]
    public void Mix_fades_back_out_to_the_untouched_main()
    {
        float[] main = { 1f, 1f, 1f };
        float[] preview = { 0.5f, 0.5f, 0.5f };
        float mix = DspPipelineService.MixHoverPreview(main, preview, 1f, 0f, 0.5f, 0.2f);
        Assert.Equal(0f, mix);
        Assert.Equal(1f, main[1], 5);
        Assert.Equal(1f, main[2], 5);
    }

    [Fact]
    public void Mix_treats_missing_preview_samples_as_silence()
    {
        float[] main = { 1f, 1f };
        float[] preview = { 0.5f };
        DspPipelineService.MixHoverPreview(main, preview, 1f, 1f, 0.5f, 0.2f);
        Assert.Equal(0.7f, main[0], 5);
        Assert.Equal(0.2f, main[1], 5);
    }

    [Fact]
    public void Duck_is_fifteen_decibels()
    {
        Assert.Equal(-15.0, 20.0 * Math.Log10(DspPipelineService.HoverDuckGain), 1);
    }
}
