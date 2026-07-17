// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Server;
using Xunit;

namespace Zeus.Server.Tests;

/// <summary>
/// PsAutoAttenuateService.ComputeAttnStepDb — the Thetis PSForm.cs:745-761
/// attenuator step: ddB = 20·log10(feedback / 152.293) with NaN → +10 and
/// out-of-range rails → ±10. Extracted from the (duplicated) HL2/P2 Monitor
/// branches; these tests pin the formula against the Thetis reference points
/// and the G2E cold-fit walk-down rail (#1248 deaf-at-31 escape).
/// </summary>
public class PsAttnStepFormulaTests
{
    [Theory]
    // In-window / near-target: 152 is the ideal (152.293) → 0 step.
    [InlineData(152, 0)]
    // Stig's known-good Thetis feedback level on the G2E: 157 → |ddB| < 0.5,
    // rounds to 0 — a converged calibration takes no further steps.
    [InlineData(157, 0)]
    // Window edges: 181 → +1.4997 → +1 (hot side); 128 → −1.5096 → −2
    // (quiet side — AwayFromZero rounding makes the edges asymmetric).
    [InlineData(181, 1)]
    [InlineData(128, -2)]
    // Strongly hot / quiet: 305 ≈ +6 dB; 76 ≈ −6 dB (20·log10(2) ≈ 6.02).
    [InlineData(305, 6)]
    [InlineData(76, -6)]
    public void ComputeAttnStepDb_MatchesThetisFormula(int feedback, int expected)
        => Assert.Equal(expected, PsAutoAttenuateService.ComputeAttnStepDb(feedback));

    [Fact]
    public void ComputeAttnStepDb_ColdFit_RidesTheMinusTenRail()
    {
        // feedback = 0: a completed too-quiet "cold" fit (calcc.c:368-369
        // integer truncation) → log10(0) = −∞ → −10 dB rail. This is how the
        // G2E escapes the deaf state: a 31 dB-seeded arm on a padded tap
        // walks 31 → 21 → 11 → 1 → 0 across successive cold fits instead of
        // deadlocking (the #1248 field failure).
        Assert.Equal(-10, PsAutoAttenuateService.ComputeAttnStepDb(0));

        int attn = 31;
        var walk = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            attn = Math.Clamp(attn + PsAutoAttenuateService.ComputeAttnStepDb(0), 0, 31);
            walk.Add(attn);
        }
        Assert.Equal(new[] { 21, 11, 1, 0 }, walk);
    }

    [Fact]
    public void ComputeAttnStepDb_HotRail_ClampsToPlusTen()
    {
        // Absurdly hot readings must never step more than +10 per dance —
        // Thetis's ±100 dB sanity rails (PSForm.cs:745-761).
        // 20·log10(fb/152.293) > 100 requires fb > 152.293·10^5.
        Assert.Equal(10, PsAutoAttenuateService.ComputeAttnStepDb(16_000_000));
    }
}
