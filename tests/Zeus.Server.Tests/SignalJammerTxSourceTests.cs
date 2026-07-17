// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class SignalJammerTxSourceTests
{
    [Fact]
    public void ContinuousQrm_WaitsForOperatorMox()
    {
        bool mox = false;
        var emitted = new List<float[]>();
        var source = new SignalJammerTxSource(
            payload => emitted.Add(Decode(payload)),
            () => mox,
            NullLogger<SignalJammerTxSource>.Instance);

        source.Configure(new SignalJammerSetRequest(
            Enabled: true,
            Preset: "hash",
            Level: 60,
            ToneHz: 980,
            DriftHz: 80,
            PulseRateHz: 3.5));

        Assert.False(source.TryPumpOnceForTests());
        Assert.Empty(emitted);

        mox = true;
        Assert.True(source.TryPumpOnceForTests());

        var block = Assert.Single(emitted);
        Assert.Equal(SignalJammerTxSource.BlockSamples, block.Length);
        Assert.Contains(block, sample => Math.Abs(sample) > 0.001f);
    }

    [Fact]
    public void TextAudio_QueuesAndEmitsWhenKeyed()
    {
        bool mox = true;
        var emitted = new List<float[]>();
        var source = new SignalJammerTxSource(
            payload => emitted.Add(Decode(payload)),
            () => mox,
            NullLogger<SignalJammerTxSource>.Instance);
        var samples = new float[SignalJammerTxSource.BlockSamples];
        Array.Fill(samples, 0.25f);

        var snapshot = source.EnqueueText(samples, SignalJammerTxSource.SampleRateHz);

        Assert.True(snapshot.TextQueued);
        Assert.True(source.TryPumpOnceForTests());

        var block = Assert.Single(emitted);
        Assert.All(block, sample => Assert.InRange(sample, 0.249f, 0.251f));
        Assert.False(source.TryPumpOnceForTests());
    }

    [Fact]
    public void TextAudio_AutoTransmit_ClaimsAndReleasesOwnMox()
    {
        bool mox = false;
        bool owned = false;
        int releaseCount = 0;
        var emitted = new List<float[]>();
        var source = new SignalJammerTxSource(
            payload => emitted.Add(Decode(payload)),
            () => mox,
            () =>
            {
                mox = true;
                owned = true;
                return null;
            },
            () =>
            {
                releaseCount++;
                if (!owned) return;
                owned = false;
                mox = false;
            },
            () => owned,
            NullLogger<SignalJammerTxSource>.Instance);
        var samples = new float[SignalJammerTxSource.BlockSamples];
        Array.Fill(samples, 0.25f);

        var snapshot = source.EnqueueText(samples, SignalJammerTxSource.SampleRateHz, autoTransmit: true);

        Assert.True(snapshot.TextQueued);
        Assert.True(mox);
        Assert.True(owned);
        Assert.True(source.TryPumpOnceForTests());
        Assert.False(mox);
        Assert.False(owned);
        Assert.Equal(1, releaseCount);
        Assert.Single(emitted);
    }

    [Fact]
    public void TextAudio_AutoTransmit_RidesExistingMoxWithoutDroppingIt()
    {
        bool mox = true;
        int requestCount = 0;
        int releaseCount = 0;
        var emitted = new List<float[]>();
        var source = new SignalJammerTxSource(
            payload => emitted.Add(Decode(payload)),
            () => mox,
            () =>
            {
                requestCount++;
                return null;
            },
            () => releaseCount++,
            () => false,
            NullLogger<SignalJammerTxSource>.Instance);
        var samples = new float[SignalJammerTxSource.BlockSamples];
        Array.Fill(samples, 0.25f);

        var snapshot = source.EnqueueText(samples, SignalJammerTxSource.SampleRateHz, autoTransmit: true);

        Assert.True(snapshot.TextQueued);
        Assert.True(source.TryPumpOnceForTests());
        Assert.True(mox);
        Assert.Equal(0, requestCount);
        Assert.Equal(0, releaseCount);
        Assert.Single(emitted);
    }

    [Fact]
    public void TextAudio_AutoTransmit_RejectsWhenMoxClaimFails()
    {
        bool mox = false;
        var source = new SignalJammerTxSource(
            _ => { },
            () => mox,
            () => "not connected",
            () => { },
            () => false,
            NullLogger<SignalJammerTxSource>.Instance);
        var samples = new float[SignalJammerTxSource.BlockSamples];
        Array.Fill(samples, 0.25f);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            source.EnqueueText(samples, SignalJammerTxSource.SampleRateHz, autoTransmit: true));

        Assert.Contains("not connected", ex.Message);
        Assert.False(source.Snapshot().TextQueued);
        Assert.False(mox);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("heterodyne")]
    [InlineData("pulse")]
    public void Presets_EmitDenseStressBlocks(string preset)
    {
        var emitted = new List<float[]>();
        var source = new SignalJammerTxSource(
            payload => emitted.Add(Decode(payload)),
            () => true,
            NullLogger<SignalJammerTxSource>.Instance);

        source.Configure(new SignalJammerSetRequest(
            Enabled: true,
            Preset: preset,
            Level: 100,
            ToneHz: 980,
            DriftHz: 160,
            PulseRateHz: 5.0));

        Assert.True(source.TryPumpOnceForTests());

        var block = Assert.Single(emitted);
        double rms = Math.Sqrt(block.Average(sample => sample * sample));
        int activeSamples = block.Count(sample => Math.Abs(sample) > 0.01f);
        int zeroCrossings = CountZeroCrossings(block);
        Assert.InRange(rms, 0.035, 0.80);
        Assert.True(activeSamples > 650, $"active sample count was {activeSamples}");
        Assert.True(zeroCrossings > 30, $"zero crossings were {zeroCrossings}");
    }

    private static float[] Decode(ReadOnlyMemory<byte> payload)
    {
        var bytes = payload.Span;
        var samples = new float[bytes.Length / sizeof(float)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * sizeof(float), sizeof(float)));
        return samples;
    }

    private static int CountZeroCrossings(ReadOnlySpan<float> samples)
    {
        int crossings = 0;
        float previous = samples[0];
        for (int i = 1; i < samples.Length; i++)
        {
            float current = samples[i];
            if ((previous < 0 && current >= 0) || (previous >= 0 && current < 0))
                crossings++;
            previous = current;
        }
        return crossings;
    }
}
