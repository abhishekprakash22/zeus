// SPDX-License-Identifier: GPL-2.0-or-later
//
// A reply to a decode is staged AFTER its own slot has started: the DX stops
// transmitting exactly when our slot opens, and only then can the decoder run.
// Judging that stage by the pre-boundary rule alone pushed every answer a full
// cycle late — the operator's reply went out only after the DX had repeated.

using Zeus.Server.Hosting.Digital;

namespace Zeus.Server.Tests;

public sealed class Ft8LateStartTests
{
    private const int SlotMs = SlotClock.Ft8SlotMs;

    private static TxStage Stage(double stagedAtMs, string slot = "even") =>
        new("CQ EA5IUE IM76", 1500, slot, DigitalMode.Ft8, stagedAtMs);

    [Fact]
    public void AReplyStagedJustAfterTheBoundary_KeysThatSlot()
    {
        long idx = 4;                                  // even slot
        double start = SlotClock.SlotStartMs(idx, DigitalMode.Ft8);

        // 400 ms in: the frontend's settle. Nothing of the waveform is lost —
        // the nominal signal start is 500 ms into the slot.
        var stage = Stage(start + 400);

        Assert.False(TxStageBook.Eligible(stage, idx, start));      // the old rule refused it
        Assert.True(TxStageBook.EligibleLate(stage, idx, start, 2_500));
    }

    [Fact]
    public void PastTheLateWindow_TheSlotIsLeftAlone()
    {
        long idx = 4;
        double start = SlotClock.SlotStartMs(idx, DigitalMode.Ft8);

        Assert.False(TxStageBook.EligibleLate(Stage(start + 2_600), idx, start, 2_500));
    }

    [Fact]
    public void ParityAndFreshnessStillApply()
    {
        long idx = 4;                                  // even
        double start = SlotClock.SlotStartMs(idx, DigitalMode.Ft8);

        // Wrong parity: an odd-slot message never keys an even slot.
        Assert.False(TxStageBook.EligibleLate(Stage(start + 100, "odd"), idx, start, 2_500));

        // Stale: older than one full cycle (two slots).
        Assert.False(TxStageBook.EligibleLate(Stage(start - 2 * SlotMs - 1), idx, start, 2_500));

        // A stage from the previous cycle is still fresh enough.
        Assert.True(TxStageBook.EligibleLate(Stage(start - SlotMs), idx, start, 2_500));
    }
}
