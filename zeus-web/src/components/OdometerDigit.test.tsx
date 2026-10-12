// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { describe, expect, it } from 'vitest';
import { act, render } from './meters/__tests__/harness';
import { OdometerDigit, ODOMETER_MS, odometerMotion, odometerStep } from './OdometerDigit';

describe('odometerStep', () => {
  it('rolls forward when tuning up, through the carry', () => {
    expect(odometerStep(3, 4, 1, 'tick')).toBe(1);
    expect(odometerStep(9, 0, 1, 'roll')).toBe(1);
    expect(odometerStep(7, 2, 1, 'roll')).toBe(5);
  });
  it('rolls backward when tuning down, through the borrow', () => {
    expect(odometerStep(4, 3, -1, 'tick')).toBe(-1);
    expect(odometerStep(0, 9, -1, 'roll')).toBe(-1);
    expect(odometerStep(2, 7, -1, 'roll')).toBe(-5);
  });
  it('adds a full revolution on a spin, and stays still when unchanged', () => {
    expect(odometerStep(3, 4, 1, 'spin')).toBe(11);
    expect(odometerStep(4, 3, -1, 'spin')).toBe(-11);
    expect(odometerStep(5, 5, 1, 'spin')).toBe(0);
  });
});

describe('odometerMotion', () => {
  it('ticks on a one-digit change, rolls otherwise, spins on big jumps', () => {
    expect(odometerMotion(10, 1)).toBe('tick');
    expect(odometerMotion(-10, 1)).toBe('tick');
    expect(odometerMotion(990, 3)).toBe('roll');
    expect(odometerMotion(7_000_000, 4)).toBe('spin');
    expect(odometerMotion(-100_000, 1)).toBe('spin');
  });
});

describe('OdometerDigit', () => {
  const col = (c: HTMLElement) => c.querySelector('[data-testid="odo-col"]') as HTMLElement;

  it('keeps the numeral readable for assistive tech and hides the strip', () => {
    const r = render(createElement(OdometerDigit, { value: 7, dir: 1, motion: 'tick' }));
    expect(r.container.querySelector('.odo-ghost')?.textContent).toBe('7');
    expect(r.container.querySelector('.odo-win')?.getAttribute('aria-hidden')).toBe('true');
    expect(col(r.container).style.transform).toBe('translateY(-2700%)');
    expect(col(r.container).style.transition).toBe('none');
    r.unmount();
  });

  it('animates to the new numeral, then parks on the middle copy', () => {
    const r = render(createElement(OdometerDigit, { value: 9, dir: 1, motion: 'tick' }));
    r.rerender(createElement(OdometerDigit, { value: 0, dir: 1, motion: 'roll' }));
    // 29 → 30: forward one cell into the next copy, with the roll timing.
    expect(col(r.container).style.transform).toBe('translateY(-3000%)');
    expect(col(r.container).style.transition).toContain(`${ODOMETER_MS.roll}ms`);
    act(() => {
      col(r.container).dispatchEvent(new Event('transitionend', { bubbles: true }));
    });
    expect(col(r.container).style.transform).toBe('translateY(-2000%)');
    expect(col(r.container).style.transition).toBe('none');
    r.unmount();
  });

  it('a band-jump spin travels a full extra revolution', () => {
    const r = render(createElement(OdometerDigit, { value: 4, dir: 1, motion: 'tick' }));
    r.rerender(createElement(OdometerDigit, { value: 2, dir: -1, motion: 'spin' }));
    // 24 → 24 - 2 - 10 = 12
    expect(col(r.container).style.transform).toBe('translateY(-1200%)');
    expect(col(r.container).style.transition).toContain(`${ODOMETER_MS.spin}ms`);
    r.unmount();
  });
});
