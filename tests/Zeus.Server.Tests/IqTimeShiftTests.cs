// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// Rewind: the I/Q ring (wrap, hardware-centre segments, memory ceiling) and
// the replay placement helpers.

using Zeus.Server;

namespace Zeus.Server.Tests;

public class IqTimeShiftTests
{
    /// <summary>n complex samples whose I value is their absolute index.</summary>
    private static double[] Block(long first, int n)
    {
        var b = new double[n * 2];
        for (int i = 0; i < n; i++)
        {
            b[2 * i] = first + i;
            b[2 * i + 1] = -(first + i);
        }
        return b;
    }

    [Fact]
    public void Reads_back_what_was_appended()
    {
        var buf = new IqTimeShiftBuffer();
        buf.Ensure(1000, 10); // 10 000 samples
        buf.Append(Block(0, 500), 14_200_000);
        var dest = new double[200 * 2];
        int got = buf.Read(100, dest, out long lo);
        Assert.Equal(200, got);
        Assert.Equal(14_200_000, lo);
        Assert.Equal(100.0, dest[0]);
        Assert.Equal(-100.0, dest[1]);
        Assert.Equal(299.0, dest[398]);
    }

    [Fact]
    public void Wraps_and_forgets_the_oldest()
    {
        var buf = new IqTimeShiftBuffer();
        buf.Ensure(1000, 10);
        for (int k = 0; k < 25; k++) buf.Append(Block(k * 1000L, 1000), 7_100_000);
        Assert.Equal(25_000, buf.Written);
        Assert.Equal(15_000, buf.Oldest);
        var dest = new double[4];
        // A position that has fallen out of the ring reads from the oldest.
        Assert.Equal(2, buf.Read(3_000, dest, out _));
        Assert.Equal(15_000.0, dest[0]);
        // The newest samples are intact across the wrap.
        Assert.Equal(2, buf.Read(24_998, dest, out _));
        Assert.Equal(24_998.0, dest[0]);
        Assert.Equal(24_999.0, dest[2]);
        // Nothing past the end.
        Assert.Equal(0, buf.Read(25_000, dest, out _));
    }

    [Fact]
    public void A_read_never_crosses_a_change_of_hardware_centre()
    {
        var buf = new IqTimeShiftBuffer();
        buf.Ensure(1000, 10);
        buf.Append(Block(0, 300), 14_200_000);
        buf.Append(Block(300, 300), 14_250_000); // radio re-centred
        var dest = new double[1000 * 2];
        Assert.Equal(200, buf.Read(100, dest, out long lo1));
        Assert.Equal(14_200_000, lo1);
        Assert.Equal(300, buf.Read(300, dest, out long lo2));
        Assert.Equal(14_250_000, lo2);
        Assert.Equal(300.0, dest[0]);
    }

    [Fact]
    public void Old_segments_are_dropped_with_their_samples()
    {
        var buf = new IqTimeShiftBuffer();
        buf.Ensure(1000, 1); // 1000 samples
        buf.Append(Block(0, 600), 1_000_000);
        buf.Append(Block(600, 600), 2_000_000);
        buf.Append(Block(1200, 600), 3_000_000);
        // Oldest is 800: the first segment is gone, the second starts the ring.
        var dest = new double[2000];
        Assert.Equal(400, buf.Read(0, dest, out long lo));
        Assert.Equal(2_000_000, lo);
        Assert.Equal(800.0, dest[0]);
    }

    [Fact]
    public void A_block_longer_than_the_ring_keeps_its_tail()
    {
        var buf = new IqTimeShiftBuffer();
        buf.Ensure(100, 1); // 100 samples
        buf.Append(Block(0, 250), 5_000_000);
        Assert.Equal(250, buf.Written);
        var dest = new double[2];
        Assert.Equal(1, buf.Read(249, dest, out _));
        Assert.Equal(249.0, dest[0]);
        Assert.Equal(1, buf.Read(150, dest, out _));
        Assert.Equal(150.0, dest[0]);
    }

    [Fact]
    public void Capacity_respects_the_memory_ceiling()
    {
        Assert.Equal(192_000 * 60, IqTimeShiftBuffer.CapacityFor(192_000, 60));
        // 1.536 MHz for 60 s would be ~737 MB; the ring is capped at 128 MB.
        int capped = IqTimeShiftBuffer.CapacityFor(1_536_000, 60);
        Assert.Equal(IqTimeShiftBuffer.MaxBytes / 8, capped);
        Assert.True(capped / 1_536_000.0 > 10.0);
        Assert.Equal(0, IqTimeShiftBuffer.CapacityFor(0, 60));
    }

    [Fact]
    public void Changing_rate_or_length_starts_the_ring_over()
    {
        var buf = new IqTimeShiftBuffer();
        Assert.True(buf.Ensure(1000, 10));
        buf.Append(Block(0, 100), 1);
        Assert.False(buf.Ensure(1000, 10));
        Assert.Equal(100, buf.Written);
        Assert.True(buf.Ensure(2000, 10));
        Assert.Equal(0, buf.Written);
        buf.Release();
        Assert.Equal(0, buf.CapacitySamples);
        Assert.Equal(0, buf.MemoryBytes);
    }

    [Fact]
    public void Cursor_lands_the_requested_delay_behind_live_inside_the_ring()
    {
        // 192 kHz, 100 s written, ring holds the last 60 s.
        long written = 192_000L * 100, oldest = 192_000L * 40;
        Assert.Equal(written - 192_000L * 10,
            DspPipelineService.TimeShiftCursorFor(written, oldest, 192_000, 10_000));
        // Asking for more than is held gives the oldest sample.
        Assert.Equal(oldest, DspPipelineService.TimeShiftCursorFor(written, oldest, 192_000, 500_000));
        // Zero delay is live.
        Assert.Equal(written, DspPipelineService.TimeShiftCursorFor(written, oldest, 192_000, 0));
    }

    [Fact]
    public void Replay_passband_is_placed_from_the_blocks_own_centre()
    {
        // Dial now at 14.230; the block was captured centred on 14.200.
        var p = DspPipelineService.PlanTimeShiftBlock(14_230_000, 14_200_000, 2_700, 192_000);
        Assert.True(p.InWindow);
        Assert.Equal(30_000, p.ShiftHz);
        // The same dial against a block captured centred 150 kHz away: not there.
        Assert.False(DspPipelineService.PlanTimeShiftBlock(14_230_000, 14_380_000, 2_700, 192_000).InWindow);
        // Passband edge counts, as for hover-to-listen.
        Assert.False(DspPipelineService.PlanTimeShiftBlock(14_289_000, 14_200_000, 2_700, 192_000).InWindow);
    }

    [Theory]
    [InlineData(5, 10)]
    [InlineData(60, 60)]
    [InlineData(100_000, 300)]
    public void Seconds_are_clamped(int asked, int expected) =>
        Assert.Equal(expected, DspPipelineService.ClampTimeShiftSeconds(asked));
}
