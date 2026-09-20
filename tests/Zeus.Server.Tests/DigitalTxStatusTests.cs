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
/// Which message the TX status reports. The FT8 window's TX line was one
/// message ahead of the air: the frontend stages the NEXT message about a
/// second into the current transmission (decodes land, the sequencer steps),
/// and the status reported whatever was staged.
/// </summary>
public sealed class DigitalTxStatusTests
{
    private static TxStage Stage(string message, string slot = "odd") =>
        new(message, 1500, slot, DigitalMode.Ft8, StagedAtMs: 0);

    [Fact]
    public void While_transmitting_it_reports_what_is_on_the_air()
    {
        var keyed = Stage("CU3HN EA5IUE IM76HE");
        var stagedNext = Stage("CU3HN EA5IUE R+15");

        var reported = DigitalService.ReportedStage(transmitting: true, keyed, stagedNext);

        Assert.Same(keyed, reported);
    }

    [Fact]
    public void While_armed_and_idle_it_reports_the_staged_message()
    {
        var stagedNext = Stage("CU3HN EA5IUE R+15");

        var reported = DigitalService.ReportedStage(transmitting: false, keyed: null, stagedNext);

        Assert.Same(stagedNext, reported);
    }

    [Fact]
    public void A_stale_keyed_stage_is_ignored_once_the_transmission_ends()
    {
        // The keyer clears KeyedStage in its finally, but never trust that
        // ordering from the reader's side: not transmitting means the staged
        // message is what matters.
        var keyed = Stage("CU3HN EA5IUE IM76HE");
        var stagedNext = Stage("CU3HN EA5IUE R+15");

        var reported = DigitalService.ReportedStage(transmitting: false, keyed, stagedNext);

        Assert.Same(stagedNext, reported);
    }

    [Fact]
    public void With_nothing_keyed_yet_it_falls_back_to_the_staged_message()
    {
        // Transmitting flips true a moment before the keyed stage is published
        // in some orderings; the staged message is the honest answer then.
        var stagedNext = Stage("CQ EA5IUE IM98");

        var reported = DigitalService.ReportedStage(transmitting: true, keyed: null, stagedNext);

        Assert.Same(stagedNext, reported);
    }

    [Fact]
    public void With_nothing_staged_or_keyed_it_reports_nothing()
    {
        Assert.Null(DigitalService.ReportedStage(transmitting: false, keyed: null, staged: null));
    }
}
