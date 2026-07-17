// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — slot clock + TX stage.
//
// THE RACE FIX LIVES HERE.
//
// Upstream's keyer refused a stage that arrived later than a fixed cutoff into
// the slot (Ft8TxService.MaxLateStartSecondsFor: FT8 ≤2.5 s, FT4 ≤1.0 s), while
// the frontend runner only produces a stage ~2.0 s after the boundary (decoder
// settle). That leaves ~500 ms of slack. When a Pi's decode jitter eats it, the
// stage is rejected and the reply slips to the NEXT matching boundary — a full
// 30 s for a CQ caller. The operator sees "someone answered my CQ and Zeus never
// replied". Those constants were, per the runner's own comment, a "G2 bench-tune".
//
// There is no engineering reason for that cutoff. The modulator does not start
// until the boundary, so a stage is useful right up to the moment we key. We
// therefore accept a stage until (boundary - KeyLeadMs) and drop the cutoff
// entirely. Freshness is still bounded: a stage older than one cycle is stale
// and will not be keyed.

namespace Zeus.Server.Hosting.Digital;

public enum DigitalMode { Ft8, Ft4 }

public static class SlotClock
{
    public const int Ft8SlotMs = 15_000;
    public const int Ft4SlotMs = 7_500;

    /// <summary>We hand the modulator the slot this far before the boundary.</summary>
    public const int KeyLeadMs = 120;

    public static int SlotMs(DigitalMode m) => m == DigitalMode.Ft4 ? Ft4SlotMs : Ft8SlotMs;

    public static long SlotIndex(double utcMs, DigitalMode m)
        => (long)Math.Floor(utcMs / SlotMs(m));

    /// <summary>"even" | "odd" — parity of the slot index, matching the frontend.</summary>
    public static string Parity(long slotIndex) => (slotIndex % 2 == 0) ? "even" : "odd";

    public static double SlotStartMs(long slotIndex, DigitalMode m) => slotIndex * (double)SlotMs(m);
}

/// <summary>A message staged by the frontend sequencer, awaiting its boundary.</summary>
public sealed record TxStage(
    string Message,
    int AudioHz,
    string Slot,          // "even" | "odd"
    DigitalMode Mode,
    double StagedAtMs);

/// <summary>
/// Holds at most one pending stage and decides whether it may key a given slot.
/// The backend NEVER invents a message: if nothing is staged, nothing goes out.
/// </summary>
public sealed class TxStageBook
{
    private readonly object _sync = new();
    private TxStage? _stage;

    /// <summary>Replace the pending stage. Idempotent by design — the runner
    /// re-POSTs every slot while armed.</summary>
    public void Put(TxStage s) { lock (_sync) _stage = s; }

    public TxStage? Peek() { lock (_sync) return _stage; }

    public void Clear() { lock (_sync) _stage = null; }

    /// <summary>
    /// Is <paramref name="stage"/> eligible to key the slot beginning at
    /// <paramref name="slotStartMs"/>?
    ///
    /// Eligible when the parity matches and the stage is no older than one full
    /// cycle. NOTE the deliberate absence of a "late start" cutoff: a stage that
    /// lands 200 ms before the boundary is still perfectly good, because we have
    /// not keyed yet. This is the upstream bug.
    /// </summary>
    public static bool Eligible(TxStage stage, long slotIndex, double slotStartMs)
    {
        if (!string.Equals(stage.Slot, SlotClock.Parity(slotIndex), StringComparison.OrdinalIgnoreCase))
            return false;

        // Freshness: staged within the cycle immediately preceding this boundary.
        double age = slotStartMs - stage.StagedAtMs;
        double cycle = SlotClock.SlotMs(stage.Mode) * 2.0;
        return age >= 0 && age <= cycle;
    }
}
