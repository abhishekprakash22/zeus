// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import {
  SMETER_PEAK_DECAY_MS,
  SMETER_PEAK_HOLD_MS,
  initialSMeterPeakHoldState,
  stepSMeterPeakHold,
} from './sMeterPeakHold';

describe('SMeter peak hold', () => {
  it('rises immediately when the live level exceeds the held peak', () => {
    const state = initialSMeterPeakHoldState(0.25, 0);
    const next = stepSMeterPeakHold(state, 0.75, 100);

    expect(next.peak).toBe(0.75);
    expect(next.holdUntilMs).toBe(100 + SMETER_PEAK_HOLD_MS);
  });

  it('holds the peak for about two seconds after the live level drops', () => {
    let state = initialSMeterPeakHoldState(1, 0);

    state = stepSMeterPeakHold(state, 0, 100);
    expect(state.peak).toBe(1);
    expect(state.holdUntilMs).toBe(100 + SMETER_PEAK_HOLD_MS);

    state = stepSMeterPeakHold(state, 0, 100 + SMETER_PEAK_HOLD_MS - 1);
    expect(state.peak).toBe(1);

    state = stepSMeterPeakHold(state, 0, 100 + SMETER_PEAK_HOLD_MS + 16);
    expect(state.peak).toBeCloseTo(1 - 16 / SMETER_PEAK_DECAY_MS);
  });

  it('starts the hold when a long-lived peak finally falls', () => {
    let state = initialSMeterPeakHoldState(0.8, 0);

    state = stepSMeterPeakHold(state, 0.2, 5000);
    expect(state.peak).toBe(0.8);
    expect(state.holdUntilMs).toBe(5000 + SMETER_PEAK_HOLD_MS);

    state = stepSMeterPeakHold(state, 0.2, 5000 + SMETER_PEAK_HOLD_MS - 1);
    expect(state.peak).toBe(0.8);
  });

  it('never decays below the live value', () => {
    let state = initialSMeterPeakHoldState(1, 0);

    state = stepSMeterPeakHold(state, 0.4, 100);
    state = stepSMeterPeakHold(
      state,
      0.4,
      100 + SMETER_PEAK_HOLD_MS + SMETER_PEAK_DECAY_MS * 4,
    );

    expect(state.peak).toBe(0.4);
  });
});
