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

    // ---- the keyer's own decision -------------------------------------
    //
    // EligibleLate being right is not enough: the keyer has to ASK at a moment
    // when the answer can still be yes. The first version asked only inside the
    // lead window before the NEXT boundary, where the running slot is already
    // ~14.9 s old, so the late path never ran and replies kept slipping a cycle
    // (seen on air with DD7EE and II0IHMG: three transmissions of the grid
    // message before the report went out).

    [Fact]
    public void AReplyStagedIntoItsSlot_IsKeyableRightThen()
    {
        long idx = 4;
        double start = SlotClock.SlotStartMs(idx, DigitalMode.Ft8);
        var stage = Stage(start + 1_300);              // decodes landed 1.3 s in

        var wants = Ft8KeyerService.WantsLateStart(
            stage, start + 1_350, lastTxSlotMs: null, out double slotStart, out int skipMs);

        Assert.True(wants);
        Assert.Equal(start, slotStart);
        Assert.Equal(850, skipMs);                     // 1350 - the 500 ms nominal start
    }

    [Fact]
    public void InsideTheNominalStartDelay_NothingIsSkipped()
    {
        long idx = 4;
        double start = SlotClock.SlotStartMs(idx, DigitalMode.Ft8);
        var stage = Stage(start + 200);

        Assert.True(Ft8KeyerService.WantsLateStart(
            stage, start + 300, null, out _, out int skipMs));
        Assert.Equal(0, skipMs);                       // still starts on time
    }

    [Fact]
    public void AtTheOldEvaluationPoint_TheAnswerIsAlwaysNo()
    {
        // Where the dead code used to ask: KeyLeadMs before the next boundary.
        // Pinning this down documents WHY the call must sit before the nap.
        long idx = 4;
        double start = SlotClock.SlotStartMs(idx, DigitalMode.Ft8);
        var stage = Stage(start + 1_300);

        double tooLate = start + SlotMs - SlotClock.KeyLeadMs;
        Assert.False(Ft8KeyerService.WantsLateStart(stage, tooLate, null, out _, out _));
    }

    [Fact]
    public void ASlotAlreadyKeyed_IsNotKeyedTwice()
    {
        long idx = 4;
        double start = SlotClock.SlotStartMs(idx, DigitalMode.Ft8);
        var stage = Stage(start + 300);

        Assert.False(Ft8KeyerService.WantsLateStart(
            stage, start + 900, lastTxSlotMs: start, out _, out _));
    }

    [Fact]
    public void TheWrongParity_IsNeverKeyedLate()
    {
        long idx = 4;                                  // even
        double start = SlotClock.SlotStartMs(idx, DigitalMode.Ft8);
        var stage = Stage(start + 300, slot: "odd");

        Assert.False(Ft8KeyerService.WantsLateStart(stage, start + 400, null, out _, out _));
    }

    [Fact]
    public void Ft4KeepsItsShorterWindow()
    {
        long idx = 4;
        double start = SlotClock.SlotStartMs(idx, DigitalMode.Ft4);
        var stage = new TxStage("CQ EA5IUE IM76", 1500, SlotClock.Parity(idx), DigitalMode.Ft4, start + 900);

        Assert.True(Ft8KeyerService.WantsLateStart(stage, start + 950, null, out _, out _));
        Assert.False(Ft8KeyerService.WantsLateStart(
            new TxStage("CQ EA5IUE IM76", 1500, SlotClock.Parity(idx), DigitalMode.Ft4, start + 1_100),
            start + 1_100, null, out _, out _));
    }
}
