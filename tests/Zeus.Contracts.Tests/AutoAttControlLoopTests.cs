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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Protocol1;
using Zeus.Protocol2;
using Zeus.Server;

namespace Zeus.Contracts.Tests;

/// <summary>
/// Unit tests for the auto-ATT control loop in <see cref="RadioService"/>.
/// Drives <c>HandleAdcOverload(status, nowMs)</c> directly with synthetic
/// timestamps so we can verify throttling, ramp-up, decay, and the red-lamp
/// counter without spinning up a Protocol1Client.
/// </summary>
public class AutoAttControlLoopTests : IDisposable
{
    // Per-fixture temp DBs — see ZoomValidationTests for the rationale.
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-autoatt-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    private RadioService MakeService()
    {
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        return new(NullLoggerFactory.Instance, dspStore, paStore);
    }

    private static readonly AdcOverloadStatus Overload = new(Adc0: true, Adc1: false);
    private static readonly AdcOverloadStatus Clean = new(Adc0: false, Adc1: false);

    [Fact]
    public void Defaults_AutoAttOn_ZeroBaselineAndOffset_NoWarning()
    {
        var r = MakeService();
        var s = r.Snapshot();

        Assert.True(s.AutoAttEnabled);
        Assert.Equal(0, s.AttenDb);
        Assert.Equal(0, s.AttOffsetDb);
        Assert.False(s.AdcOverloadWarning);
    }

    [Fact]
    public void FirstOverloadEvent_EstablishesTickBaseline_NoImmediateStep()
    {
        var r = MakeService();
        r.HandleAdcOverload(Overload, nowMs: 1_000);
        Assert.Equal(0, r.Snapshot().AttOffsetDb);
    }

    [Fact]
    public void OverloadRepeated_RampsOneStepPerTickWindow()
    {
        var r = MakeService();
        // Disable the sustained-overload gate so this exercises pure ramp
        // mechanics (gate behaviour has its own test below).
        r.SetAdcProtection(new AdcProtectionSetRequest(WarningThreshold: 0));

        // First event is a free "baseline" — no step applied (throttle rule).
        r.HandleAdcOverload(Overload, nowMs: 0);
        r.HandleAdcOverload(Overload, nowMs: 50);  // inside same window — ignored
        Assert.Equal(0, r.Snapshot().AttOffsetDb);

        // Cross the 100 ms boundary: one step applied.
        r.HandleAdcOverload(Overload, nowMs: 105);
        Assert.Equal(1, r.Snapshot().AttOffsetDb);

        // Another window crossing.
        r.HandleAdcOverload(Overload, nowMs: 210);
        Assert.Equal(2, r.Snapshot().AttOffsetDb);
    }

    [Fact]
    public void SustainedOverloadGate_NoRampUntilCounterExceedsThreshold()
    {
        var r = MakeService();  // default WarningThreshold = 3

        // Establish the tick baseline, then ramp the counter one step per
        // window. Thetis only touches the attenuator once the counter > 3, so
        // the first three sustained windows must leave the offset at zero.
        r.HandleAdcOverload(Overload, nowMs: 0);    // baseline
        r.HandleAdcOverload(Overload, nowMs: 105);  // level 1
        r.HandleAdcOverload(Overload, nowMs: 210);  // level 2
        r.HandleAdcOverload(Overload, nowMs: 315);  // level 3
        Assert.Equal(0, r.Snapshot().AttOffsetDb);

        // Fourth sustained window crosses the gate (level 4 > 3) → first step.
        r.HandleAdcOverload(Overload, nowMs: 420);
        Assert.Equal(1, r.Snapshot().AttOffsetDb);
        Assert.True(r.Snapshot().AdcOverloadWarning);
    }

    [Fact]
    public void OverloadSustained_SaturatesAt31dB()
    {
        var r = MakeService();
        // Start tick baseline.
        r.HandleAdcOverload(Overload, nowMs: 0);
        for (int i = 1; i <= 50; i++)
        {
            r.HandleAdcOverload(Overload, nowMs: i * 100 + 5);
        }
        Assert.Equal(31, r.Snapshot().AttOffsetDb);
    }

    [Fact]
    public void ClearAfterRamp_DecaysToZero()
    {
        var r = MakeService();
        // No gate, no release hold — pure decay mechanics.
        r.SetAdcProtection(new AdcProtectionSetRequest(WarningThreshold: 0, ReleaseHoldMs: 0));

        r.HandleAdcOverload(Overload, nowMs: 0);
        // Ramp to 5 dB offset.
        for (int i = 1; i <= 5; i++)
            r.HandleAdcOverload(Overload, nowMs: i * 100 + 5);
        Assert.Equal(5, r.Snapshot().AttOffsetDb);

        // Decay by feeding clean events.
        for (int i = 1; i <= 5; i++)
            r.HandleAdcOverload(Clean, nowMs: 500 + i * 100 + 5);
        Assert.Equal(0, r.Snapshot().AttOffsetDb);

        // Further clean events don't go negative.
        r.HandleAdcOverload(Clean, nowMs: 2000);
        Assert.Equal(0, r.Snapshot().AttOffsetDb);
    }

    [Fact]
    public void ReleaseHoldOff_HoldsOffsetThenUnwinds()
    {
        var r = MakeService();
        // No gate so the ramp is simple; 1 s release hold so we can watch it.
        r.SetAdcProtection(new AdcProtectionSetRequest(WarningThreshold: 0, ReleaseHoldMs: 1_000));

        r.HandleAdcOverload(Overload, nowMs: 0);     // baseline
        r.HandleAdcOverload(Overload, nowMs: 105);   // offset 1
        r.HandleAdcOverload(Overload, nowMs: 210);   // offset 2
        Assert.Equal(2, r.Snapshot().AttOffsetDb);

        // Clean windows arrive but the 1 s hold has not elapsed since the last
        // overload (t=210): the offset is held, not unwound.
        r.HandleAdcOverload(Clean, nowMs: 400);
        r.HandleAdcOverload(Clean, nowMs: 800);
        r.HandleAdcOverload(Clean, nowMs: 1_100);
        Assert.Equal(2, r.Snapshot().AttOffsetDb);

        // Past the hold (t >= 210 + 1000): the offset unwinds one step per window.
        r.HandleAdcOverload(Clean, nowMs: 1_300);
        Assert.Equal(1, r.Snapshot().AttOffsetDb);
        r.HandleAdcOverload(Clean, nowMs: 1_500);
        Assert.Equal(0, r.Snapshot().AttOffsetDb);
    }

    [Fact]
    public void OverloadBurstsWithinWindow_CountAsOneStep()
    {
        var r = MakeService();
        r.SetAdcProtection(new AdcProtectionSetRequest(WarningThreshold: 0));
        r.HandleAdcOverload(Clean, nowMs: 0);    // baseline tick
        // Many events in one 100ms window.
        for (int i = 1; i <= 50; i++)
            r.HandleAdcOverload(Overload, nowMs: i);
        // First boundary cross — one step.
        r.HandleAdcOverload(Overload, nowMs: 105);
        Assert.Equal(1, r.Snapshot().AttOffsetDb);
    }

    [Fact]
    public void AdcOverloadWarning_FlipsRedWhenCounterExceedsThree()
    {
        var r = MakeService();
        r.HandleAdcOverload(Overload, nowMs: 0);   // baseline
        // Each overload tick adds +1 to the counter (Thetis analog), clamped to
        // 5. Red lamp on counter>3 means four sustained ticks (~400 ms).
        r.HandleAdcOverload(Overload, nowMs: 105);
        Assert.False(r.Snapshot().AdcOverloadWarning); // counter=1
        r.HandleAdcOverload(Overload, nowMs: 210);
        Assert.False(r.Snapshot().AdcOverloadWarning); // counter=2
        r.HandleAdcOverload(Overload, nowMs: 315);
        Assert.False(r.Snapshot().AdcOverloadWarning); // counter=3
        r.HandleAdcOverload(Overload, nowMs: 420);
        Assert.True(r.Snapshot().AdcOverloadWarning);  // counter=4 → red
    }

    [Fact]
    public void AdcOverloadWarning_DecaysToFalse()
    {
        var r = MakeService();
        r.HandleAdcOverload(Overload, nowMs: 0);
        for (int i = 1; i <= 4; i++)
            r.HandleAdcOverload(Overload, nowMs: i * 100 + 5);
        Assert.True(r.Snapshot().AdcOverloadWarning); // counter=4

        // Each clean tick decrements by 1. After 1 clean tick counter=3 → !warn.
        r.HandleAdcOverload(Clean, nowMs: 525);
        Assert.False(r.Snapshot().AdcOverloadWarning);
    }

    [Fact]
    public void AutoAttDisabled_EventsHaveNoEffect()
    {
        var r = MakeService();
        r.SetAutoAtt(false);

        r.HandleAdcOverload(Overload, nowMs: 0);
        r.HandleAdcOverload(Overload, nowMs: 105);
        r.HandleAdcOverload(Overload, nowMs: 210);

        var s = r.Snapshot();
        Assert.False(s.AutoAttEnabled);
        Assert.Equal(0, s.AttOffsetDb);
        Assert.False(s.AdcOverloadWarning);
    }

    [Fact]
    public void MoxOn_SuspendsControlLoop()
    {
        var r = MakeService();
        r.SetAdcProtection(new AdcProtectionSetRequest(WarningThreshold: 0));
        r.HandleAdcOverload(Overload, nowMs: 0);
        r.HandleAdcOverload(Overload, nowMs: 105);
        Assert.Equal(1, r.Snapshot().AttOffsetDb);

        r.SetMox(true);
        // While MOX, nothing happens to the ramp (TX path owns its own atten).
        for (int i = 2; i <= 10; i++)
            r.HandleAdcOverload(Overload, nowMs: i * 100 + 5);
        Assert.Equal(1, r.Snapshot().AttOffsetDb);

        r.SetMox(false);
        // After RX resumes, stepping resumes. The first post-MOX event fires a
        // tick because the 100 ms boundary has long since elapsed; subsequent
        // events keep the ramp going.
        int before = r.Snapshot().AttOffsetDb;
        r.HandleAdcOverload(Overload, nowMs: 2000);
        r.HandleAdcOverload(Overload, nowMs: 2105);
        Assert.True(r.Snapshot().AttOffsetDb > before,
            "offset must advance once MOX clears");
    }

    [Fact]
    public void TurningAutoAttOff_ResetsOffsetAndWarning()
    {
        var r = MakeService();
        r.SetAdcProtection(new AdcProtectionSetRequest(WarningThreshold: 0));
        r.HandleAdcOverload(Overload, nowMs: 0);
        for (int i = 1; i <= 4; i++)
            r.HandleAdcOverload(Overload, nowMs: i * 100 + 5);
        Assert.Equal(4, r.Snapshot().AttOffsetDb);
        Assert.True(r.Snapshot().AdcOverloadWarning);

        r.SetAutoAtt(false);
        var s = r.Snapshot();
        Assert.False(s.AutoAttEnabled);
        Assert.Equal(0, s.AttOffsetDb);
        Assert.False(s.AdcOverloadWarning);
    }

    [Fact]
    public void StateDto_SerializationRoundTrip_PreservesAutoAttFields()
    {
        var opts = new System.Text.Json.JsonSerializerOptions();
        var state = new StateDto(
            Status: ConnectionStatus.Connected,
            Endpoint: "192.168.1.100:1024",
            VfoHz: 14_200_000,
            Mode: RxMode.USB,
            FilterLowHz: 150,
            FilterHighHz: 2850,
            SampleRate: 192_000,
            AttenDb: 3,
            AutoAttEnabled: true,
            AttOffsetDb: 7,
            AdcOverloadWarning: true);

        string json = System.Text.Json.JsonSerializer.Serialize(state, opts);
        var back = System.Text.Json.JsonSerializer.Deserialize<StateDto>(json, opts);

        Assert.NotNull(back);
        Assert.True(back.AutoAttEnabled);
        Assert.Equal(7, back.AttOffsetDb);
        Assert.True(back.AdcOverloadWarning);
        Assert.Equal(3, back.AttenDb);
    }

    [Fact]
    public void AdcProtectionConfig_ClampsAndReportsLiveStatus()
    {
        var r = MakeService();

        var status = r.SetAdcProtection(new AdcProtectionSetRequest(
            AttackMs: 1,
            ReleaseMs: 20_000,
            AttackStepDb: 99,
            ReleaseStepDb: 99,
            MaxOffsetDb: 99,
            WarningThreshold: 99,
            MagnitudeSoftLimit: 99_999,
            ReleaseHoldMs: 99_999));

        Assert.Equal(25, status.Config.AttackMs);
        Assert.Equal(5_000, status.Config.ReleaseMs);
        Assert.Equal(6, status.Config.AttackStepDb);
        Assert.Equal(6, status.Config.ReleaseStepDb);
        Assert.Equal(31, status.Config.MaxOffsetDb);
        // Capped at 4 (not 5) so the gate stays reachable against the level-5 cap.
        Assert.Equal(4, status.Config.WarningThreshold);
        Assert.Equal((int)ushort.MaxValue, status.Config.MagnitudeSoftLimit);
        Assert.Equal(10_000, status.Config.ReleaseHoldMs);
    }

    private static P2TelemetryReading HotMagnitude(ushort adc0) => new(
        FwdAdc: 0,
        RevAdc: 0,
        ExciterAdc: 0,
        PttIn: false,
        PllLocked: true,
        AdcOverloadBits: 0,          // NO hard overload — magnitude alone must act
        Adc0MaxMagnitude: adc0,
        Adc1MaxMagnitude: 0);

    /// <summary>
    /// The soft limit acts on magnitude alone, before any hard overload bit is
    /// set — that is the whole point of it — and it acts on the FIRST reading
    /// rather than spending an attack interval establishing a baseline. An ADC
    /// that is already clipping does not benefit from us waiting 25 ms to begin.
    ///
    /// This replaces an older version of this test that expected 0 dB on the
    /// first reading and a 2 dB-per-interval climb. That was the pre-predictive
    /// contract; the loop now handles the first threat immediately (see
    /// "First threat after quiet is handled immediately" in RadioService).
    /// </summary>
    [Fact]
    public void P2MagnitudeSoftLimit_ActsOnFirstReading_WithoutHardOverload()
    {
        var r = MakeService();
        r.SetAdcProtection(new AdcProtectionSetRequest(
            AttackMs: 25,
            ReleaseMs: 50,
            AttackStepDb: 2,
            ReleaseStepDb: 1,
            MaxOffsetDb: 4,
            WarningThreshold: 0,   // isolate the magnitude path from the overload gate
            MagnitudeSoftLimit: 1_000));

        var hot = HotMagnitude(1_200);

        // Zones for a 1000 soft limit put Target at 795 (2 dB below attack), so
        // a peak of 1200 is 20*log10(1200/795) = 3.58 dB high and the loop asks
        // for 4 dB — the smallest whole decibel that seats the peak at Target.
        // MaxOffsetDb is also 4, so it arrives there in one move and holds.
        r.HandleP2AdcTelemetry(hot, nowMs: 0);
        Assert.Equal(4, r.Snapshot().AttOffsetDb);

        r.HandleP2AdcTelemetry(hot, nowMs: 25);
        Assert.Equal(4, r.Snapshot().AttOffsetDb);

        r.HandleP2AdcTelemetry(hot, nowMs: 75);
        Assert.Equal(4, r.Snapshot().AttOffsetDb);
    }

    /// <summary>
    /// AttackStepDb is a FLOOR on the magnitude path, not a ceiling. The step is
    /// derived from how far the observed peak sits above the target zone
    /// (MagnitudeAttackStepDb takes Math.Max(floor, excessDb)), so a configured
    /// step larger than the excess wins, and a configured step smaller than the
    /// excess is overridden.
    ///
    /// Pinned deliberately: this is the behaviour that made the older test look
    /// like a regression — an operator configuring 2 dB saw the attenuator move
    /// 4. It is intended for a protection path, where seating the peak in one
    /// move beats creeping toward it while the converter clips, and MaxOffsetDb
    /// still bounds the total. If that judgement is ever revisited, this test is
    /// the thing that should fail.
    /// </summary>
    [Fact]
    public void P2MagnitudeSoftLimit_AttackStepIsAFloorNotACeiling()
    {
        // Configured step (6) EXCEEDS the 3.58 dB excess, so the floor wins.
        var big = MakeService();
        big.SetAdcProtection(new AdcProtectionSetRequest(
            AttackMs: 25,
            ReleaseMs: 50,
            AttackStepDb: 6,
            ReleaseStepDb: 1,
            MaxOffsetDb: 31,
            WarningThreshold: 0,
            MagnitudeSoftLimit: 1_000));
        big.HandleP2AdcTelemetry(HotMagnitude(1_200), nowMs: 0);
        Assert.Equal(6, big.Snapshot().AttOffsetDb);

        // Configured step (2) is BELOW the excess, so the derived 4 dB wins.
        var small = MakeService();
        small.SetAdcProtection(new AdcProtectionSetRequest(
            AttackMs: 25,
            ReleaseMs: 50,
            AttackStepDb: 2,
            ReleaseStepDb: 1,
            MaxOffsetDb: 31,
            WarningThreshold: 0,
            MagnitudeSoftLimit: 1_000));
        small.HandleP2AdcTelemetry(HotMagnitude(1_200), nowMs: 0);
        Assert.Equal(4, small.Snapshot().AttOffsetDb);
    }

    [Fact]
    public void P2HardOverload_TracksBitsAndMaxMagnitudeAtTrip()
    {
        var r = MakeService();
        var trip = new P2TelemetryReading(
            FwdAdc: 0,
            RevAdc: 0,
            ExciterAdc: 0,
            PttIn: false,
            PllLocked: true,
            AdcOverloadBits: 0x03,
            Adc0MaxMagnitude: 50_000,
            Adc1MaxMagnitude: 49_000);

        r.HandleP2AdcTelemetry(trip, nowMs: 0);
        var status = r.GetAdcProtectionStatus();

        Assert.Equal(0x03, status.LastOverloadBits);
        Assert.Equal((ushort?)50_000, status.Adc0MaxMagnitude);
        Assert.Equal((ushort?)49_000, status.Adc1MaxMagnitude);
        Assert.Equal((ushort)50_000, status.Adc0MaxMagnitudeAtOverload);
        Assert.Equal((ushort)49_000, status.Adc1MaxMagnitudeAtOverload);
        Assert.NotNull(status.LastTelemetryUtc);
    }
}
