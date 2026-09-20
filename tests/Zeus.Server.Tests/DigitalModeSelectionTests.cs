// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR),
//                         Ramón Martínez (EA5IUE), and contributors.

using Zeus.Server.Hosting.Digital;

namespace Zeus.Server.Tests;

/// <summary>
/// FT4 is not a flavour of FT8: half the slot (7.5 s) and a different
/// waveform. The core decoder hard-wired FT8 into its slot maths, its decode
/// call and the protocol it published, so selecting FT4 produced a receiver
/// listening on the wrong boundaries with the wrong demodulator. It reported
/// no error — it simply never decoded anything.
/// </summary>
public sealed class DigitalModeSelectionTests
{
    [Theory]
    [InlineData("FT4", true)]
    [InlineData("ft4", true)]
    [InlineData("FT8", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ModeKind_ReadsTheModeString(string? mode, bool expectFt4)
    {
        Assert.Equal(expectFt4 ? DigitalMode.Ft4 : DigitalMode.Ft8,
                     DigitalService.ModeKindOf(mode));
    }

    [Fact]
    public void Ft4AndFt8_DoNotShareASlotGrid()
    {
        // The reason a mode mix-up is silent rather than noisy: both grids are
        // perfectly valid, they just disagree about where a transmission ends.
        Assert.Equal(15_000, SlotClock.SlotMs(DigitalMode.Ft8));
        Assert.Equal(7_500, SlotClock.SlotMs(DigitalMode.Ft4));

        static double Start(double t, DigitalMode m) =>
            SlotClock.SlotStartMs(SlotClock.SlotIndex(t, m), m);

        // Every OTHER FT4 boundary lands on an FT8 one, which is exactly what
        // makes the mix-up hard to spot: half the time the two agree.
        Assert.Equal(Start(46_000, DigitalMode.Ft8), Start(46_000, DigitalMode.Ft4));

        // And half the time they do not — 40 s belongs to the slot that opened
        // at 30 s under FT8, and at 37.5 s under FT4.
        Assert.Equal(30_000, Start(40_000, DigitalMode.Ft8));
        Assert.Equal(37_500, Start(40_000, DigitalMode.Ft4));
    }

    [Fact]
    public void TheDecoderFollowsTheModeItIsGiven()
    {
        // The pipeline reads the mode through a delegate so a switch mid-run is
        // picked up on the next cycle rather than at the next restart.
        var mode = DigitalMode.Ft8;
        using var clock = new ClockService();
        var pipeline = new DecoderPipeline(clock, new EventHub(), () => mode);

        Assert.Equal(DigitalMode.Ft8, pipeline.CurrentMode);
        mode = DigitalMode.Ft4;
        Assert.Equal(DigitalMode.Ft4, pipeline.CurrentMode);
    }
}
