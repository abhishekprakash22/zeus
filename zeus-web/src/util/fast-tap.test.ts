// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it } from 'vitest';
import { createFastTap, SWALLOW_WINDOW_MS, TAP_SLOP_PX } from './fast-tap';

const touch = (x = 10, y = 10, pointerId = 1) => ({ pointerId, pointerType: 'touch', isPrimary: true, clientX: x, clientY: y });
const mouse = (x = 10, y = 10) => ({ pointerId: 9, pointerType: 'mouse', isPrimary: true, clientX: x, clientY: y });

function harness() {
  let t = 1000;
  let fired = 0;
  const tap = createFastTap(() => { fired += 1; }, () => t);
  return { tap, fired: () => fired, advance: (ms: number) => { t += ms; } };
}

describe('fast tap', () => {
  it('acts on finger-lift and swallows the late synthetic click', () => {
    const h = harness();
    h.tap.pointerDown(touch());
    h.tap.pointerUp(touch(), false);
    expect(h.fired()).toBe(1);
    h.advance(350);
    h.tap.click();
    expect(h.fired()).toBe(1);
  });

  it('two quick taps key then unkey even when both clicks arrive late', () => {
    const h = harness();
    h.tap.pointerDown(touch());
    h.tap.pointerUp(touch(), false);
    h.advance(200);
    h.tap.pointerDown(touch());
    h.tap.pointerUp(touch(), false);
    h.tap.click();
    h.tap.click();
    expect(h.fired()).toBe(2);
  });

  it('mouse keeps the ordinary click path', () => {
    const h = harness();
    h.tap.pointerDown(mouse());
    h.tap.pointerUp(mouse(), false);
    expect(h.fired()).toBe(0);
    h.tap.click();
    expect(h.fired()).toBe(1);
  });

  it('a drag past the slop or a cancel does not key', () => {
    const h = harness();
    h.tap.pointerDown(touch());
    h.tap.pointerUp(touch(10 + TAP_SLOP_PX + 5, 10), false);
    h.tap.pointerDown(touch());
    h.tap.pointerCancel();
    h.tap.pointerUp(touch(), false);
    expect(h.fired()).toBe(0);
  });

  it('a disabled key does not act on lift', () => {
    const h = harness();
    h.tap.pointerDown(touch());
    h.tap.pointerUp(touch(), true);
    expect(h.fired()).toBe(0);
  });

  it('an owed swallow expires so a later keyboard activation still works', () => {
    const h = harness();
    h.tap.pointerDown(touch());
    h.tap.pointerUp(touch(), false);
    h.advance(SWALLOW_WINDOW_MS + 1);
    h.tap.click();
    expect(h.fired()).toBe(2);
  });
});
