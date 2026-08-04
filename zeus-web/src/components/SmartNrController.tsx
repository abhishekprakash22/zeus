// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { useEffect } from 'react';
import { fetchHardwareDiagnostics, setNr, type NrConfigDto, type RadioStateDto } from '../api/client';
import { getNoiseFloor, getSignalConfidence, getSignalStationarity, registerEstimatorConsumer } from '../dsp/signal-estimator';
import {
  adaptSmartNrToDspCapabilities,
  labelSmartNrProfile,
  recommendSmartNr,
  shapeSmartNrRecommendation,
  smartNrProfileKey,
  type SmartNrCapabilityAdaptation,
  type SmartNrDspCapabilities,
  type SmartNrRecommendation,
} from '../dsp/smart-nr';
import { analyzeRxChain, type RxChainAnalysis } from '../dsp/rx-chain-health';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';
import { useRxMetersStore } from '../state/rx-meters-store';
import { useSmartNrStore } from '../state/smart-nr-store';
import { useTxStore } from '../state/tx-store';

const SAMPLE_INTERVAL_MS = 1500;
// Weak-signal evidence envelope (module-lifetime; resets with the page).
const evidenceRef: { ema: Float32Array | null; out: Float32Array | null } = { ema: null, out: null };
const PROFILE_SWITCH_DWELL_SAMPLES = 6;
const APPLY_COOLDOWN_MS = 15000;
const DSP_CAPABILITY_REFRESH_MS = 30000;
const DSP_CAPABILITY_TIMEOUT_MS = 2500;

function round1(v: number): number {
  return Math.round(v * 10) / 10;
}

function sameNr(a: NrConfigDto, b: NrConfigDto): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

function shouldHoldForRxChain(rx: RxChainAnalysis): boolean {
  return rx.state === 'overload' || rx.state === 'underfilled' || (rx.state === 'agc-stressed' && rx.actionTone === 'protect');
}

export function SmartNrController() {
  useEffect(() => {
    let lastAt = 0;
    let lastAppliedAt = Number.NEGATIVE_INFINITY;
    let lastDspCapabilityRefreshAt = Number.NEGATIVE_INFINITY;
    let pendingProfileKey: string | null = null;
    let pendingCount = 0;
    let abort: AbortController | null = null;
    let diagnosticsAbort: AbortController | null = null;
    let diagnosticsTimeout: number | null = null;
    let diagnosticsInFlight = false;
    let diagnosticsRequestId = 0;
    let dspCapabilities: SmartNrDspCapabilities | null = null;
    let releaseEstimatorConsumer: (() => void) | null = null;

    const resetPending = () => {
      pendingProfileKey = null;
      pendingCount = 0;
    };
    const syncEstimatorConsumer = () => {
      const enabled = useSmartNrStore.getState().automationMode !== 'manual';
      if (enabled && releaseEstimatorConsumer === null) {
        releaseEstimatorConsumer = registerEstimatorConsumer();
      } else if (!enabled && releaseEstimatorConsumer !== null) {
        releaseEstimatorConsumer();
        releaseEstimatorConsumer = null;
      }
    };

    const clearDiagnosticsTimeout = () => {
      if (diagnosticsTimeout === null) return;
      window.clearTimeout(diagnosticsTimeout);
      diagnosticsTimeout = null;
    };
    const refreshDspCapabilities = (now: number) => {
      if (diagnosticsInFlight || now - lastDspCapabilityRefreshAt < DSP_CAPABILITY_REFRESH_MS) return;
      lastDspCapabilityRefreshAt = now;
      diagnosticsInFlight = true;
      diagnosticsAbort?.abort();
      const ac = new AbortController();
      const requestId = ++diagnosticsRequestId;
      diagnosticsAbort = ac;
      diagnosticsTimeout = window.setTimeout(() => {
        if (requestId !== diagnosticsRequestId || diagnosticsAbort !== ac) return;
        ac.abort();
        diagnosticsAbort = null;
        diagnosticsInFlight = false;
        diagnosticsTimeout = null;
      }, DSP_CAPABILITY_TIMEOUT_MS);
      fetchHardwareDiagnostics(ac.signal)
        .then((diag) => {
          if (ac.signal.aborted || requestId !== diagnosticsRequestId) return;
          dspCapabilities = {
            wdspActive: diag.dsp.wdspActive,
            wdspEmnrPost2Available: diag.dsp.wdspEmnrPost2Available,
            wdspNr4SbnrAvailable: diag.dsp.wdspNr4SbnrAvailable,
          };
        })
        .catch(() => {
          // Diagnostics capability is advisory. Keep the last known-good
          // snapshot and retry on the next refresh interval.
        })
        .finally(() => {
          if (requestId !== diagnosticsRequestId) return;
          if (diagnosticsAbort === ac) diagnosticsAbort = null;
          clearDiagnosticsTimeout();
          diagnosticsInFlight = false;
        });
    };

    const setStatus = (
      rec: SmartNrRecommendation,
      shaped: NrConfigDto,
      pending: boolean,
      applied: boolean,
      rx: RxChainAnalysis | null = null,
      heldByRxChain = false,
      capability: SmartNrCapabilityAdaptation | null = null,
    ) => {
      const c = rec.condition;
      const rxStatus = rx !== null && rx.state !== 'waiting' ? rx : null;
      useSmartNrStore.getState().setStatus({
        atUtc: new Date().toISOString(),
        profile: labelSmartNrProfile(shaped),
        reason: rec.reason,
        capabilityLimited: capability?.capabilityLimited || undefined,
        capabilityRecommendation: capability?.capabilityRecommendation,
        heldByRxChain,
        rxChainLabel: rxStatus?.label,
        rxChainRecommendation: rxStatus?.recommendation,
        rxChainTone: rxStatus?.actionTone,
        rxChainScore: rxStatus?.score,
        maxSnrDb: round1(c.maxSnrDb),
        occupancyPct: round1(c.occupancy6 * 100),
        coherentOccupancyPct: round1(c.coherentOccupancy6 * 100),
        impulsivePct: round1(c.impulsiveOccupancy12 * 100),
        peakCount: c.peakCount,
        coherentPeakCount: c.coherentPeakCount,
        coherentSubthresholdSignal: c.coherentSubthresholdSignal,
        pending,
        applied,
        nr: shaped,
      });
    };

    const applyNr = (nr: NrConfigDto) => {
      const conn = useConnectionStore.getState();
      abort?.abort();
      const ac = new AbortController();
      abort = ac;
      conn.setNr(nr);
      setNr(nr, ac.signal)
        .then((state: RadioStateDto) => {
          if (!ac.signal.aborted) useConnectionStore.getState().applyState(state);
        })
        .catch(() => {
          // Next poll or operator action will reconcile.
        });
    };

    const evaluate = () => {
      const now = Date.now();
      if (now - lastAt < SAMPLE_INTERVAL_MS) return;
      lastAt = now;

      const settings = useSmartNrStore.getState();
      if (settings.automationMode === 'manual') {
        resetPending();
        return;
      }
      const conn = useConnectionStore.getState();
      const tx = useTxStore.getState();
      if (conn.status !== 'Connected' || tx.moxOn || tx.tunOn) {
        resetPending();
        return;
      }
      refreshDspCapabilities(now);
      const runtimeCapabilities: SmartNrDspCapabilities = {
        ...(dspCapabilities ?? {
          wdspActive: true,
          wdspEmnrPost2Available: true,
          wdspNr4SbnrAvailable: true,
        }),
        wdspNr3RnnrAvailable: conn.wdspNr3RnnrAvailable,
        nr3ModelName: conn.nr3ModelName,
      };

      const display = useDisplayStore.getState();
      // Signal Intelligence: judge inside the RX filter passband. Slice the
      // pan bins to [vfo+filterLow, vfo+filterHigh] so a strong station
      // elsewhere on the panadapter cannot steer the NR decision for the
      // signal actually being worked. Falls back to the full pan when the
      // window is degenerate (<8 bins) or geometry is unknown.
      let spectrumForNr = display.panValid ? display.panDb : null;
      let filterWindow: { i0: number; i1: number; fullLen: number } | null = null;
      if (
        settings.filterScoped &&
        spectrumForNr &&
        display.hzPerPixel > 0 &&
        display.width > 1
      ) {
        const startHz = Number(display.centerHz) - (display.width / 2) * display.hzPerPixel;
        const loHz = conn.vfoHz + Math.min(conn.filterLowHz, conn.filterHighHz);
        const hiHz = conn.vfoHz + Math.max(conn.filterLowHz, conn.filterHighHz);
        const fullLen = spectrumForNr.length;
        const clampBin = (v: number) => Math.max(0, Math.min(fullLen, Math.round(v)));
        const i0 = clampBin((loHz - startHz) / display.hzPerPixel);
        const i1 = clampBin((hiHz - startHz) / display.hzPerPixel);
        if (i1 - i0 >= 8) {
          filterWindow = { i0, i1, fullLen };
          spectrumForNr = spectrumForNr.subarray(i0, i1);
        }
      }
      // Signal Intelligence: weak-signal evidence accumulates over time — a
      // per-bin fast-rise / slow-decay envelope over the confidence map, so a
      // faint carrier persisting a few dB above the noise earns recognition
      // that a single frame's SNR would not.
      const rawConfidence = getSignalConfidence();
      let confidenceForNr = rawConfidence;
      if (settings.weakSignalEvidence && rawConfidence) {
        if (!evidenceRef.ema || evidenceRef.ema.length !== rawConfidence.length)
          evidenceRef.ema = new Float32Array(rawConfidence.length);
        const ema = evidenceRef.ema;
        const out = (!evidenceRef.out || evidenceRef.out.length !== rawConfidence.length)
          ? (evidenceRef.out = new Float32Array(rawConfidence.length))
          : evidenceRef.out;
        for (let i = 0; i < rawConfidence.length; i++) {
          const v = rawConfidence[i]!;
          const e = ema[i]!;
          ema[i] = v > e ? e + 0.25 * (v - e) : e + 0.06 * (v - e);
          out[i] = Math.max(v, ema[i]!);
        }
        confidenceForNr = out;
      }
      // Slice every bin-aligned input with the same filter window so indices
      // stay coherent inside the recommender.
      const stationarityRaw = getSignalStationarity();
      let stationarityForNr = stationarityRaw;
      if (filterWindow && spectrumForNr) {
        const { i0, i1, fullLen } = filterWindow;
        const sliceAligned = (a: Float32Array | null) =>
          a && a.length === fullLen ? a.subarray(i0, i1) : a;
        confidenceForNr = sliceAligned(confidenceForNr);
        stationarityForNr = sliceAligned(stationarityRaw);
      }
      const rxMeters = useRxMetersStore.getState();
      const rx = analyzeRxChain(
        {
          signalPk: rxMeters.signalPk,
          signalAv: rxMeters.signalAv,
          adcPk: rxMeters.adcPk,
          adcAv: rxMeters.adcAv,
          agcGain: rxMeters.agcGain,
          agcEnvPk: rxMeters.agcEnvPk,
          agcEnvAv: rxMeters.agcEnvAv,
          fallbackDbm: tx.rxDbm,
        },
        {
          autoAgcEnabled: conn.autoAgcEnabled,
          autoAttEnabled: conn.autoAttEnabled,
        },
      );
      const rec = recommendSmartNr({
        spectrum: spectrumForNr,
        floor: getNoiseFloor(),
        confidence: confidenceForNr,
        stationarity: stationarityForNr,
        rx: {
          signalDbm: rx.signalDbm,
          adcHeadroomDb: rx.adcHeadroomDb,
          agcGain: rx.agcGain,
        },
        dsp: runtimeCapabilities,
        current: conn.nr,
        mode: conn.mode,
      });
      if (!rec) return;

      const capability = adaptSmartNrToDspCapabilities(
        shapeSmartNrRecommendation(rec, settings),
        runtimeCapabilities,
      );
      const shaped = capability.nr;
      const heldByRxChain = shouldHoldForRxChain(rx);
      if (settings.automationMode === 'suggest') {
        setStatus(rec, shaped, false, false, rx, heldByRxChain, capability);
        resetPending();
        return;
      }
      if (heldByRxChain) {
        setStatus(rec, shaped, false, false, rx, true, capability);
        resetPending();
        return;
      }
      if (sameNr(conn.nr, shaped)) {
        setStatus(rec, shaped, false, false, rx, false, capability);
        resetPending();
        return;
      }
      const targetProfileKey = smartNrProfileKey(shaped);
      const currentProfileKey = smartNrProfileKey(conn.nr);
      if (targetProfileKey === currentProfileKey) {
        setStatus(rec, shaped, false, false, rx, false, capability);
        resetPending();
        return;
      }
      if (pendingProfileKey !== targetProfileKey) {
        pendingProfileKey = targetProfileKey;
        pendingCount = 1;
        setStatus(rec, shaped, true, false, rx, false, capability);
        return;
      }
      pendingCount++;
      const requiredDwell = Math.max(settings.dwellSamples, PROFILE_SWITCH_DWELL_SAMPLES);
      const ready = pendingCount >= requiredDwell && now - lastAppliedAt >= APPLY_COOLDOWN_MS;
      setStatus(rec, shaped, !ready, ready, rx, false, capability);
      if (ready) {
        lastAppliedAt = now;
        applyNr(shaped);
        resetPending();
      }
    };

    const unsubDisplay = useDisplayStore.subscribe((state, prev) => {
      if (state.lastSeq !== prev.lastSeq) evaluate();
    });
    const unsubSettings = useSmartNrStore.subscribe((state, prev) => {
      if (state.automationMode !== prev.automationMode) syncEstimatorConsumer();
      if (state.automationMode === 'manual' && prev.automationMode !== 'manual') {
        state.setStatus(null);
        resetPending();
      }
    });
    syncEstimatorConsumer();

    return () => {
      abort?.abort();
      diagnosticsRequestId++;
      diagnosticsAbort?.abort();
      clearDiagnosticsTimeout();
      releaseEstimatorConsumer?.();
      unsubDisplay();
      unsubSettings();
    };
  }, []);

  return null;
}
