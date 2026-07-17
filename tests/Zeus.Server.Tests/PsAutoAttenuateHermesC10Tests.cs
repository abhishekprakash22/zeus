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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Protocol1;
using Zeus.Protocol2;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Single-ADC P1 auto-attenuate — the single-ADC P1
/// dance driven through the REAL <see cref="PsAutoAttenuateService.Tick1"/>
/// gate + dispatch chain, against a real (constructed, never connected — no
/// socket I/O) <see cref="Protocol1Client"/> so the wire value lands in the
/// same atten_on_Tx plumbing the radio reads. Covers: board dispatch (the
/// supported single-ADC P1 boards dance, every other P1 board still skips),
/// the plain mi0bot walk math
/// (tooHot / tooQuiet, full-ddB step), the 0..31 clamps, the
/// AutoAttenuate-off skip, the fb-zero skip (deliberately NO
/// stall-acquisition walk — fb-zero recovery is the operator's manual
/// control), the one-disable/one-restore SetPsControl bracket, the
/// no-new-calc gate, and the arm-edge ground-truth baseline (silicon 31,
/// never a phantom 0).
///
/// Also pins the GH #426 engine-arm guard
/// (<see cref="DspPipelineService.P1PsEngineArmSupported"/>) board-by-board:
/// HermesC10 and HermesII now arm the engine; every other P1 board still skips.
/// </summary>
public class PsAutoAttenuateHermesC10Tests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-psattc10-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", ".pa", ".ps" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { }
        }
    }

    // Hot / in-window / quiet feedback levels around the mi0bot window
    // [128, 181] with ideal 152.293 (PsAutoAttenuateService thresholds).
    private static PsStageMeters Meters(float feedback, int calibrationAttempts) => new(
        FeedbackLevel: feedback,
        CalState: 8,               // STAYON — outside the wedge-watchdog 4..7 window
        Correcting: true,
        CorrectionDb: 0f,
        MaxTxEnvelope: 0.5f,
        CalibrationAttempts: calibrationAttempts);

    private sealed class Harness : IDisposable
    {
        public RadioService Radio = null!;
        public TxService Tx = null!;
        public Protocol1Client Client = null!;
        public FakePsEngine Engine = null!;
        public PsAutoAttenuateService Svc = null!;

        public void Dispose()
        {
            Radio.SetActiveClientForTest(null);
            Client.Dispose();
            Radio.Dispose();
        }
    }

    private sealed class P2Harness : IDisposable
    {
        public RadioService Radio = null!;
        public TxService Tx = null!;
        public Protocol2Client Client = null!;
        public FakePsEngine Engine = null!;
        public PsAutoAttenuateService Svc = null!;

        public void Dispose()
        {
            Client.Dispose();
            Radio.Dispose();
        }
    }

    private Harness Build(HpsdrBoardKind board)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        // Real PS store so the persist-policy tests can assert exactly what
        // lands on disk (mid-walk values must NOT; in-window results must).
        var psStore = new PsSettingsStore(NullLogger<PsSettingsStore>.Instance, _dbPath + ".ps");
        var radio = new RadioService(
            loggerFactory, dspStore, paStore,
            filterPresetStore: null, txIqSource: null,
            preferredRadioStore: null, psStore: psStore);

        // Constructed but never connected — RadioService then reports the
        // client's board kind and IsConnected without any socket I/O.
        var client = new Protocol1Client();
        client.SetBoardKind(board);
        radio.SetActiveClientForTest(client);

        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var engine = new FakePsEngine();
        var pipe = new TestPipeline(radio, hub, loggerFactory, engine);
        var tx = new TxService(radio, pipe, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        var svc = new PsAutoAttenuateService(radio, tx, pipe, NullLogger<PsAutoAttenuateService>.Instance);

        return new Harness { Radio = radio, Tx = tx, Client = client, Engine = engine, Svc = svc };
    }

    private P2Harness BuildP2(HpsdrBoardKind board)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var psStore = new PsSettingsStore(NullLogger<PsSettingsStore>.Instance, _dbPath + ".ps");
        var radio = new RadioService(
            loggerFactory, dspStore, paStore,
            filterPresetStore: null, txIqSource: null,
            preferredRadioStore: null, psStore: psStore);

        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var engine = new FakePsEngine();
        var pipe = new TestPipeline(radio, hub, loggerFactory, engine);
        var tx = new TxService(radio, pipe, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        var svc = new PsAutoAttenuateService(radio, tx, pipe, NullLogger<PsAutoAttenuateService>.Instance);
        var p2 = new Protocol2Client(NullLogger<Protocol2Client>.Instance);
        p2.SetBoardKind(board);
        pipe.SetP2ClientForTest(p2);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000, p2, board);
        radio.ApplyPsHwPeakForConnection(isProtocol2: true, board);

        return new P2Harness { Radio = radio, Tx = tx, Client = p2, Engine = engine, Svc = svc };
    }

    // Arm PS (auto-cal mode, the operator default), key MOX, and start the
    // two-tone generator — the G2E servo is two-tone-only (piHPSDR parity),
    // so every dance test must run inside the deliberate calibration window.
    // Then clear any engine calls the arm path recorded so the assertions
    // see the dance bracket alone.
    private static void ArmAndKey(Harness h)
    {
        h.Radio.SetPs(new PsControlSetRequest(Enabled: true, Auto: true, Single: false));
        Assert.True(h.Tx.TrySetMox(true, out var err), $"TrySetMox failed: {err}");
        h.Tx.SetTwoToneOn(true);
        h.Engine.PsControlCalls.Clear();
    }

    private static void ArmAndKey(P2Harness h)
    {
        h.Radio.SetPs(new PsControlSetRequest(Enabled: true, Auto: true, Single: false));
        Assert.True(h.Tx.TrySetMox(true, out var err), $"TrySetMox failed: {err}");
        h.Tx.SetTwoToneOn(true);
        h.Engine.PsControlCalls.Clear();
    }

    // ---- Dispatch -----------------------------------------------------------

    [Fact]
    public void C10Board_HotFeedback_DancesAndWiresAttenOnTx()
    {
        using var h = Build(HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(10);
        ArmAndKey(h);
        // fb=250 → tooHot; ddB = 20·log10(250/152.293) = 4.31 → step +4.
        h.Engine.Meters = Meters(250, calibrationAttempts: 1);

        h.Svc.Tick1();   // Monitor: disable PS, compute delta
        h.Svc.Tick1();   // SetNewValues: 10 + 4 → 14 on the wire + persisted
        h.Svc.Tick1();   // RestoreOperation: re-arm with saved cal-mode

        Assert.Equal(14, h.Client.PsTxAttenOnTxDb);
        Assert.Equal(14, h.Radio.Snapshot().PsTxFeedbackAttenuationDb);
        // Exactly one disable/restore bracket, restoring the operator's
        // (auto=true, single=false) — mi0bot PSForm.cs:763/:790-815.
        Assert.Equal(new[] { (false, false), (true, false) }, h.Engine.PsControlCalls);
    }

    [Fact]
    public void HermesIIBoard_HotFeedback_DancesAndWiresAttenOnTx()
    {
        using var h = Build(HpsdrBoardKind.HermesII);
        h.Client.SetPsTxAttenOnTxDb(10);
        ArmAndKey(h);
        h.Engine.Meters = Meters(250, calibrationAttempts: 1);

        h.Svc.Tick1();
        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal(14, h.Client.PsTxAttenOnTxDb);
        Assert.Equal(14, h.Radio.Snapshot().PsTxFeedbackAttenuationDb);
        Assert.Equal(new[] { (false, false), (true, false) }, h.Engine.PsControlCalls);
    }

    [Theory]
    [InlineData(HpsdrBoardKind.Metis)]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.Angelia)]
    [InlineData(HpsdrBoardKind.Orion)]
    public void UnsupportedP1Board_HotFeedback_StillSkips(HpsdrBoardKind board)
    {
        // Every unsupported P1 board has no PS feedback attenuator: the dispatch
        // must fall through to the P2 branch and land on skip=p2-null (no
        // P2 client here) — no engine bracket, no wire write, no persist.
        using var h = Build(board);
        h.Client.SetPsTxAttenOnTxDb(10);   // stale value must stay untouched
        ArmAndKey(h);
        int attnBefore = h.Radio.Snapshot().PsTxFeedbackAttenuationDb;
        h.Engine.Meters = Meters(250, calibrationAttempts: 1);

        h.Svc.Tick1();
        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal(10, h.Client.PsTxAttenOnTxDb);
        Assert.Equal(attnBefore, h.Radio.Snapshot().PsTxFeedbackAttenuationDb);
        Assert.Empty(h.Engine.PsControlCalls);
    }

    // ---- Gates --------------------------------------------------------------

    [Fact]
    public void AutoAttenuateOff_SkipsDance()
    {
        using var h = Build(HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(10);
        ArmAndKey(h);
        h.Radio.SetPsAdvanced(new PsAdvancedSetRequest(AutoAttenuate: false));
        h.Engine.Meters = Meters(250, calibrationAttempts: 1);

        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal(10, h.Client.PsTxAttenOnTxDb);
        Assert.Empty(h.Engine.PsControlCalls);
    }

    [Fact]
    public void FeedbackZero_NoCompletedFit_NeverWalks()
    {
        // fb=0 with CalibrationAttempts stuck at 0 = calcc genuinely hasn't
        // completed a fit (e.g. hw_peak stall) — must NOT walk in either
        // direction. Only a COMPLETED cold fit (info5 > 0) earns the rail.
        using var h = Build(HpsdrBoardKind.HermesC10);
        ArmAndKey(h);

        for (int i = 0; i < 4; i++)
        {
            h.Engine.Meters = Meters(0, calibrationAttempts: 0);
            h.Svc.Tick1();
        }

        Assert.Equal(31, h.Client.PsTxAttenOnTxDb);   // silicon default, untouched
        Assert.Empty(h.Engine.PsControlCalls);
    }

    [Fact]
    public void ColdFits_WalkDownTheMinusTenRail_OutOfDeafness()
    {
        // The #1248 deaf-at-31 escape: a heavily-padded tap completes fits
        // whose feedback level integer-truncates to 0 (calcc.c:368-369).
        // Each fresh cold fit rides the Thetis −10 dB rail DOWN
        // (20·log10(0/152.293) → −∞ → −10): 31 → 21 → 11 → 1, one full
        // dance (disable → write → restore) per fit. At attn 0 the rail
        // stops (nothing left to open up).
        using var h = Build(HpsdrBoardKind.HermesC10);
        ArmAndKey(h);

        var expected = new[] { 21, 11, 1 };
        for (int cal = 1; cal <= 3; cal++)
        {
            h.Engine.Meters = Meters(0, calibrationAttempts: cal);
            h.Svc.Tick1();   // Monitor: cold-fit rail → disable PS
            h.Svc.Tick1();   // SetNewValues: −10 on the wire
            h.Svc.Tick1();   // RestoreOperation: re-arm saved cal-mode
            Assert.Equal(expected[cal - 1], h.Client.PsTxAttenOnTxDb);
        }
        // Three complete brackets — PS never left disabled.
        Assert.Equal(6, h.Engine.PsControlCalls.Count);
    }

    [Fact]
    public void VoiceMoxWithoutTwoTone_SkipsDance()
    {
        // G2E policy (piHPSDR parity): the attenuation servo runs ONLY
        // during the deliberate two-tone calibration — an armed PS must
        // never dance the attenuator mid-QSO voice TX (the class that
        // burned the G2, g2_ps_blowout_508, and poisoned the G2E testers'
        // stores).
        using var h = Build(HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(10);
        ArmAndKey(h);
        h.Tx.SetTwoToneOn(false);   // voice MOX only
        h.Engine.Meters = Meters(250, calibrationAttempts: 1);

        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal(10, h.Client.PsTxAttenOnTxDb);
        Assert.Empty(h.Engine.PsControlCalls);
    }

    [Fact]
    public void FeedbackInWindow_NoDance()
    {
        using var h = Build(HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(10);
        ArmAndKey(h);
        h.Engine.Meters = Meters(150, calibrationAttempts: 1);   // 128 ≤ fb ≤ 181

        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal(10, h.Client.PsTxAttenOnTxDb);
        Assert.Empty(h.Engine.PsControlCalls);
    }

    [Fact]
    public void NoNewCalc_GatesTheNextDance()
    {
        // mi0bot PSForm.cs:1097-1099 — step only after calcc completes a NEW
        // fit. With CalibrationAttempts frozen, a still-hot feedback must not
        // start a second dance (stepping mid-fit is the binfo[6] wedge).
        using var h = Build(HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(10);
        ArmAndKey(h);
        h.Engine.Meters = Meters(250, calibrationAttempts: 1);

        for (int i = 0; i < 6; i++) h.Svc.Tick1();   // dance (3 ticks) + 3 gated ticks

        Assert.Equal(14, h.Client.PsTxAttenOnTxDb);            // stepped exactly once
        Assert.Equal(2, h.Engine.PsControlCalls.Count);        // one bracket only
    }

    // ---- Walk math + clamps --------------------------------------------------

    [Fact]
    public void TooQuiet_WalksDown_ClampsAtZero()
    {
        using var h = Build(HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(3);
        ArmAndKey(h);
        // fb=100 < 128 with attn > 0 → tooQuiet; ddB = 20·log10(100/152.293)
        // = -3.65 → step -4 → clamp(3 - 4) = 0.
        h.Engine.Meters = Meters(100, calibrationAttempts: 1);

        h.Svc.Tick1();
        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal(0, h.Client.PsTxAttenOnTxDb);
        Assert.Equal(0, h.Radio.Snapshot().PsTxFeedbackAttenuationDb);
    }

    [Fact]
    public void TooHot_WalksUp_ClampsAt31()
    {
        using var h = Build(HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(30);
        ArmAndKey(h);
        // fb=2000 → ddB = 22.4 → step +22 → clamp(30 + 22) = 31.
        h.Engine.Meters = Meters(2000, calibrationAttempts: 1);

        h.Svc.Tick1();
        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal(31, h.Client.PsTxAttenOnTxDb);
    }

    [Fact]
    public void ArmEdge_BaselinesToRadioGroundTruth_Not_PhantomZero()
    {
        // The operator never set a value → the radio is holding the silicon
        // reset 31 (the sentinel read-back). The arm-edge baseline must read
        // that ground truth: a hot dance from 31 computes 31 + 4 → clamps to
        // 31 → NO wire write. A phantom-0 baseline would compute 0 + 4 = 4
        // and slam the wire — the exact desync bug the arm-edge sync fixed.
        using var h = Build(HpsdrBoardKind.HermesC10);
        ArmAndKey(h);
        int attnBefore = h.Radio.Snapshot().PsTxFeedbackAttenuationDb;
        h.Engine.Meters = Meters(250, calibrationAttempts: 1);

        h.Svc.Tick1();
        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal(31, h.Client.PsTxAttenOnTxDb);
        Assert.Equal(attnBefore, h.Radio.Snapshot().PsTxFeedbackAttenuationDb);
        // The bracket still completes so PS is never left disabled.
        Assert.Equal(new[] { (false, false), (true, false) }, h.Engine.PsControlCalls);
    }

    // ---- Persist policy --------------------------------------------------------

    [Fact]
    public void PersistsOnlyInWindowCalibrationResults_NeverMidWalk()
    {
        // The #1249 poison-ratchet class: mid-walk servo values must never
        // land in the per-board store (both field testers' stores were
        // ratcheted to 31 dB and re-applied on every connect). Only a value
        // that PRODUCED an in-window fit — a completed calibration —
        // persists. Mid-walk the live value still surfaces in state for the
        // PURESIGNAL panel.
        using var h = Build(HpsdrBoardKind.HermesC10);
        // Cache the per-board key exactly like a real P1 G2E connect does.
        h.Radio.ApplyPsHwPeakForConnection(isProtocol2: false, board: HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(10);
        ArmAndKey(h);

        // Quiet fit: fb=100 → −4 → attn 6. Mid-walk: wire + state move,
        // store does NOT.
        h.Engine.Meters = Meters(100, calibrationAttempts: 1);
        h.Svc.Tick1();   // Monitor → disable
        h.Svc.Tick1();   // SetNewValues → wire 6
        h.Svc.Tick1();   // RestoreOperation
        Assert.Equal(6, h.Client.PsTxAttenOnTxDb);
        Assert.Equal(6, h.Radio.Snapshot().PsTxFeedbackAttenuationDb);
        Assert.Null(h.Radio.GetPersistedPsTxAttnDb());   // nothing on disk yet

        // The next fit lands in-window at attn 6 → THAT persists.
        h.Engine.Meters = Meters(150, calibrationAttempts: 2);
        h.Svc.Tick1();   // Monitor: in-window → persist calibration result
        Assert.Equal(6, h.Radio.GetPersistedPsTxAttnDb());
    }

    [Fact]
    public void P2HermesII_VoiceMoxWithoutTwoTone_SkipsDance()
    {
        using var h = BuildP2(HpsdrBoardKind.HermesII);
        h.Client.SetTxAttenuationDb(10);
        ArmAndKey(h);
        h.Tx.SetTwoToneOn(false);
        h.Engine.Meters = Meters(250, calibrationAttempts: 1);

        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal((byte)10, h.Client.TxStepAttnDb);
        Assert.Empty(h.Engine.PsControlCalls);
    }

    [Fact]
    public void P2HermesII_ColdFits_WalkDownTheMinusTenRail()
    {
        using var h = BuildP2(HpsdrBoardKind.HermesII);
        h.Client.SetTxAttenuationDb(31);
        ArmAndKey(h);

        h.Engine.Meters = Meters(0, calibrationAttempts: 1);
        h.Svc.Tick1();
        h.Svc.Tick1();
        h.Svc.Tick1();

        Assert.Equal((byte)21, h.Client.TxStepAttnDb);
        Assert.Equal(21, h.Radio.Snapshot().PsTxFeedbackAttenuationDb);
        Assert.Null(h.Radio.GetPersistedPsTxAttnDb());
        Assert.Equal(new[] { (false, false), (true, false) }, h.Engine.PsControlCalls);
    }

    [Fact]
    public void P2HermesII_PersistsOnlyInWindowCalibrationResults_NeverMidWalk()
    {
        using var h = BuildP2(HpsdrBoardKind.HermesII);
        h.Client.SetTxAttenuationDb(10);
        ArmAndKey(h);

        h.Engine.Meters = Meters(100, calibrationAttempts: 1);
        h.Svc.Tick1();
        h.Svc.Tick1();
        h.Svc.Tick1();
        Assert.Equal((byte)6, h.Client.TxStepAttnDb);
        Assert.Equal(6, h.Radio.Snapshot().PsTxFeedbackAttenuationDb);
        Assert.Null(h.Radio.GetPersistedPsTxAttnDb());

        h.Engine.Meters = Meters(150, calibrationAttempts: 2);
        h.Svc.Tick1();
        Assert.Equal(6, h.Radio.GetPersistedPsTxAttnDb());
    }

    // ---- Mid-dance recovery ---------------------------------------------------

    [Fact]
    public void UnkeyMidDance_RestoresOperatorCalMode()
    {
        // If MOX drops between the disable and the restore, the not-keyed
        // gate must re-issue SetPsControl with the operator's saved cal-mode
        // — otherwise PS sits disabled in WDSP for the rest of the session.
        using var h = Build(HpsdrBoardKind.HermesC10);
        h.Client.SetPsTxAttenOnTxDb(10);
        ArmAndKey(h);
        h.Engine.Meters = Meters(250, calibrationAttempts: 1);

        h.Svc.Tick1();   // Monitor → disable PS, dance in flight
        Assert.Equal(new[] { (false, false) }, h.Engine.PsControlCalls);

        Assert.True(h.Tx.TrySetMox(false, out _));
        h.Tx.SetTwoToneOn(false);   // two-tone drops with the unkey
        h.Svc.Tick1();   // not-keyed recover path

        Assert.Equal(new[] { (false, false), (true, false) }, h.Engine.PsControlCalls);
        Assert.Equal(10, h.Client.PsTxAttenOnTxDb);   // aborted before the write
    }

    // ---- GH #426 engine-arm guard ---------------------------------------------

    [Theory]
    [InlineData(HpsdrBoardKind.HermesLite2, true)]
    [InlineData(HpsdrBoardKind.HermesC10, true)]    // the G2E carve-out under test
    [InlineData(HpsdrBoardKind.HermesII, true)]     // ANAN-10E two-DDC PS path
    [InlineData(HpsdrBoardKind.Metis, false)]
    [InlineData(HpsdrBoardKind.Hermes, false)]
    [InlineData(HpsdrBoardKind.Angelia, false)]
    [InlineData(HpsdrBoardKind.Orion, false)]
    [InlineData(HpsdrBoardKind.OrionMkII, false)]
    public void P1PsEngineArmSupported_CarvesOutOnlySupportedP1Boards(HpsdrBoardKind board, bool expected)
    {
        // GH #426: arming WDSP PS on a P1 board with no feedback path parks
        // calcc in COLLECT and freezes RX audio + waterfall. Only boards with
        // a proven paired feedback layout arm WDSP PS.
        Assert.Equal(expected, DspPipelineService.P1PsEngineArmSupported(p1Connected: true, board));
    }

    [Fact]
    public void P1PsEngineArmSupported_Protocol2_AlwaysArms()
    {
        // No P1 client (Protocol 2 session) → the ANAN-class feedback DDC
        // path exists; board kind is irrelevant to the guard.
        Assert.True(DspPipelineService.P1PsEngineArmSupported(
            p1Connected: false, HpsdrBoardKind.OrionMkII));
        Assert.True(DspPipelineService.P1PsEngineArmSupported(
            p1Connected: false, HpsdrBoardKind.Hermes));
    }

    [Theory]
    [InlineData(HpsdrBoardKind.HermesC10, 48_000, true)]
    [InlineData(HpsdrBoardKind.HermesII, 96_000, true)]
    [InlineData(HpsdrBoardKind.HermesLite2, 48_000, false)]
    [InlineData(HpsdrBoardKind.Hermes, 384_000, false)]
    public void P1PsFeedbackRateOverride_ScopedToHermesFamilySingleAdcBoards(
        HpsdrBoardKind board,
        int wireRateHz,
        bool expectedCall)
    {
        var calls = new List<int>();

        DspPipelineService.ApplyP1PsFeedbackRateOverride(board, wireRateHz, calls.Add);

        if (expectedCall)
            Assert.Equal(new[] { wireRateHz }, calls);
        else
            Assert.Empty(calls);
    }

    // ---- Test doubles ----------------------------------------------------------

    // Minimal IDspEngine: controllable PS stage meters + a SetPsControl
    // recording. Everything else is a safe no-op — the auto-attenuate tick
    // never calls the RX/TX processing surface.
    private sealed class FakePsEngine : IDspEngine
    {
        public PsStageMeters Meters = PsStageMeters.Silent;
        public List<(bool Auto, bool Single)> PsControlCalls { get; } = new();

        public PsStageMeters GetPsStageMeters() => Meters;
        public void SetPsControl(bool autoCal, bool singleCal) =>
            PsControlCalls.Add((autoCal, singleCal));

        public int TxBlockSamples => 1024;
        public int TxOutputSamples => 1024;
        public int OpenChannel(int sampleRateHz, int pixelWidth) => 0;
        public void CloseChannel(int channelId) { }
        public void FeedIq(int channelId, ReadOnlySpan<double> interleavedIqSamples) { }
        public void SetMode(int channelId, RxMode mode) { }
        public void SetFilter(int channelId, int lowHz, int highHz) { }
        public void SetVfoHz(int channelId, long vfoHz) { }
        public void SetCtunShift(int channelId, int shiftHz) { }
        public void SetAgcTop(int channelId, double topDb) { }
        public void SetAgcThresh(int channelId, double threshDbm) { }
        public double GetAgcTop(int channelId) => 0.0;
        public double GetAgcThresh(int channelId) => 0.0;
        public void SetAgc(int channelId, AgcConfig cfg) { }
        public void SetSquelch(int channelId, SquelchConfig cfg) { }
        public void SetTxLeveling(int channelId, TxLevelingConfig cfg) { }
        public void SetTxPhaseRotator(int channelId, TxPhaseRotatorConfig cfg) { }
        public void SetRxDisplayFastAttack(int channelId, bool fast) { }
        public void SetRxAfGainDb(int channelId, double db) { }
        public void SetNoiseReduction(int channelId, NrConfig cfg) { }
        public Zeus.Dsp.Nr3ModelLoadResult LoadNr3Model(string? modelFilePath) => Zeus.Dsp.Nr3ModelLoadResult.Unavailable;
        public void SetNotches(IReadOnlyList<NotchDto> notches) { }
        public void SetNotchTuneFrequencyHz(double loHz) { }
        public void SetZoom(int channelId, int level) { }
        public int ReadAudio(int channelId, Span<float> output) => 0;
        public bool TryGetDisplayPixels(int channelId, DisplayPixout which, Span<float> dbOut) => false;
        public bool TryGetTxDisplayPixels(DisplayPixout which, Span<float> dbOut) => false;
        public void ConfigureTxDisplayAnalyzer(int fftSize, int windowType, double avgTauSec) { }
        public bool TryGetPsFeedbackDisplayPixels(DisplayPixout which, Span<float> dbOut) => false;
        public int OpenTxChannel(int outputRateHz = 48_000) => 0;
        public void SetMox(bool moxOn) { }
        public double GetRxaSignalDbm(int channelId) => -140.0;
        public RxStageMeters GetRxStageMeters(int channelId) => RxStageMeters.Silent;
        public void SetTxMode(RxMode mode) { }
        public void SetTxFilter(int lowHz, int highHz) { }
        public void SetRxBandpassWindow(int channelId, BandpassWindow window) { }
        public void SetTxBandpassWindow(BandpassWindow window) { }
        public int ProcessTxBlock(ReadOnlySpan<float> micMono, Span<float> iqInterleaved) => 0;
        public void SetTxTune(bool on) { }
        public TxStageMeters GetTxStageMeters() => TxStageMeters.Silent;
        public void SetTwoTone(bool on, double freq1, double freq2, double mag) { }
        public void SetPsEnabled(bool enabled) { }
        public void SetPsHold(bool hold) { }
        public void SetPsAdvanced(bool ptol, double moxDelaySec, double loopDelaySec,
                                  double ampDelayNs, double hwPeak, int ints, int spi) { }
        public void SetPsHwPeak(double hwPeak) { }
        public void FeedPsFeedbackBlock(ReadOnlySpan<float> txI, ReadOnlySpan<float> txQ,
                                        ReadOnlySpan<float> rxI, ReadOnlySpan<float> rxQ) { }
        public void ResetPs() { }
        public void SavePsCorrection(string path) { }
        public void RestorePsCorrection(string path) { }
        public void SetCfcConfig(CfcConfig cfg) { }
        public void SetTxPanelGain(double linearGain) { }
        public void SetTxLevelerMaxGain(double maxGainDb) { }
        public void SetTxMonitorEnabled(bool enabled) { }
        public int ReadTxMonitorAudio(Span<float> output) => 0;
        public bool IsTxMonitorOn => false;
        public void Dispose() { }
    }

    // Non-hosted subclass: CurrentEngine returns the fake so the tick sees a
    // live engine without the Synthetic/WDSP lifecycle (same pattern as
    // MicGainEndpointTests.TestPipeline).
    private sealed class TestPipeline(
        RadioService radio,
        StreamingHub hub,
        ILoggerFactory logs,
        FakePsEngine engine) : DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), logs)
    {
        public override IDspEngine CurrentEngine => engine;

        public void SetP2ClientForTest(Protocol2Client client)
        {
            var field = typeof(DspPipelineService).GetField("_p2Client", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(this, client);
        }
    }
}
