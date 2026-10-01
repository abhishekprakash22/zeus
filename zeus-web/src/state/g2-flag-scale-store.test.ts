// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it } from 'vitest';
import { clampFlagScale, FLAG_SCALE_MAX, FLAG_SCALE_MIN, useG2FlagScaleStore } from './g2-flag-scale-store';

describe('g2 flag scale', () => {
  it('clamps to 1..2 and rejects non-finite values', () => {
    expect(clampFlagScale(0.5)).toBe(FLAG_SCALE_MIN);
    expect(clampFlagScale(3)).toBe(FLAG_SCALE_MAX);
    expect(clampFlagScale(1.4)).toBeCloseTo(1.4);
    expect(clampFlagScale(Number.NaN)).toBe(FLAG_SCALE_MIN);
  });

  it('setScale clamps live updates', () => {
    useG2FlagScaleStore.getState().setScale(5);
    expect(useG2FlagScaleStore.getState().scale).toBe(FLAG_SCALE_MAX);
    useG2FlagScaleStore.getState().setScale(1.25);
    expect(useG2FlagScaleStore.getState().scale).toBeCloseTo(1.25);
  });
});
