// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Operator-requested local monitor audio must stay audible when the operator
/// presses the desktop RX master Mute (<see cref="RxAudioMuteState"/>). The WAV
/// Recorder playback lane and TX Monitor preview route through
/// <c>NativeAudioSink.PublishExempt</c>, which — unlike <c>Publish</c> — is NOT
/// gated by the mute; real RX audio stays on <c>Publish</c> and is still dropped
/// while muted. These tests pin the sink-level contract: the exempt lane bypasses
/// the mute, the normal lane honours it, and the exempt lane keeps the same format
/// defence-in-depth guard.
/// </summary>
public sealed class NativeAudioSinkMuteExemptTests
{
    private static AudioFrame Frame(int n, float value = 0.25f)
    {
        var samples = new float[n];
        Array.Fill(samples, value);
        return new AudioFrame(
            Seq: 1, TsUnixMs: 0, RxId: 0, Channels: 1,
            SampleRateHz: 48_000, SampleCount: (ushort)n, Samples: samples);
    }

    [Fact]
    public void Publish_WhenMuted_DropsFrame()
    {
        var mute = new RxAudioMuteState();
        using var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance, muteState: mute);
        mute.SetMuted(true);

        sink.Publish(Frame(480));

        // Real RX audio (and CW sidetone, which shares this frame) stays muted.
        Assert.Equal(0, sink.CurrentRingDepth);
    }

    [Fact]
    public void PublishExempt_WhenMuted_StillEnqueues()
    {
        var mute = new RxAudioMuteState();
        using var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance, muteState: mute);
        mute.SetMuted(true);

        sink.PublishExempt(Frame(480));

        // Recorder playback and TX Monitor preview bypass the master mute.
        Assert.Equal(480, sink.CurrentRingDepth);
    }

    [Fact]
    public void Publish_WhenNotMuted_Enqueues()
    {
        using var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);

        sink.Publish(Frame(480));

        // Baseline: unmuted, the normal RX lane accepts audio (recorder rides this
        // path when unmuted, so nothing regresses there).
        Assert.Equal(480, sink.CurrentRingDepth);
    }

    [Fact]
    public void PublishExempt_WrongFormat_Dropped()
    {
        using var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);

        // Stereo and wrong-sample-rate frames are rejected by the same
        // defence-in-depth guard Publish uses, so a malformed exempt frame can
        // never corrupt the mono/48 kHz playback ring.
        var stereo = new AudioFrame(1, 0, 0, Channels: 2, 48_000, 240, new float[480]);
        var wrongRate = new AudioFrame(1, 0, 0, 1, SampleRateHz: 44_100, 480, new float[480]);

        sink.PublishExempt(stereo);
        sink.PublishExempt(wrongRate);

        Assert.Equal(0, sink.CurrentRingDepth);
    }

    [Fact]
    public void ExemptAndNormalLanes_ShareTheRing()
    {
        // Unmuted, both lanes feed the same playback ring (the exempt lane is a
        // byte-for-byte copy of Publish minus the mute gate) — so diagnostics /
        // throughput accounting stay coherent regardless of which lane was used.
        using var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);

        sink.Publish(Frame(240));
        sink.PublishExempt(Frame(240));

        Assert.Equal(480, sink.CurrentRingDepth);
    }
}
