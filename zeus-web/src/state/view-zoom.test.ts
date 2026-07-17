// SPDX-License-Identifier: GPL-2.0-or-later
//
// Unit tests for the animated display zoom (view-zoom.ts). Uses the
// injectable clock/raf so the tween is driven deterministically — no
// browser, no real time. Mirrors view-center.test.ts.

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import * as vz from './view-zoom';

let nowMs = 0;
let scheduled: ((t: number) => void) | null = null;
let nextHandle = 1;

function stepFrame(dtMs: number): void {
  nowMs += dtMs;
  const cb = scheduled;
  scheduled = null;
  cb?.(nowMs);
}

function drainFrames(dtMs: number, maxFrames = 1000): number {
  let frames = 0;
  while (scheduled && frames < maxFrames) {
    stepFrame(dtMs);
    frames++;
  }
  return frames;
}

beforeEach(() => {
  nowMs = 0;
  scheduled = null;
  vz._resetForTest();
  vz._setClockForTest(
    () => nowMs,
    (cb) => {
      scheduled = cb;
      return nextHandle++;
    },
    () => {
      scheduled = null;
    },
  );
});

afterEach(() => {
  vz._resetForTest();
});

// Spans for zoom 1 / 2 / 8 at 192 kHz over 2048 bins (sampleRate / zoom / Width).
const HZPP_Z1 = 192_000 / 1 / 2048;
const HZPP_Z2 = 192_000 / 2 / 2048;
const HZPP_Z8 = 192_000 / 8 / 2048;

describe('view-zoom tween', () => {
  it('starts parked with zero cost (no rAF scheduled)', () => {
    expect(vz._isLoopRunningForTest()).toBe(false);
    expect(scheduled).toBeNull();
    expect(vz.isInitialized()).toBe(false);
  });

  it('initialises from the first span without a glide', () => {
    vz.setTarget(HZPP_Z1);
    expect(vz.isInitialized()).toBe(true);
    expect(vz.getDisplayedHzPerPixel()).toBe(HZPP_Z1);
    expect(vz.getTargetHzPerPixel()).toBe(HZPP_Z1);
    expect(scheduled).toBeNull(); // snap, not tween
    expect(vz.isAnimating()).toBe(false);
  });

  it('eases to a new span and self-parks', () => {
    vz.snapTo(HZPP_Z1);
    vz.setTarget(HZPP_Z2); // zoom in: span halves
    expect(scheduled).not.toBeNull(); // loop armed
    expect(vz.isAnimating()).toBe(true);
    const frames = drainFrames(16.7);
    expect(frames).toBeLessThan(60); // converges well under a second
    expect(vz.getDisplayedHzPerPixel()).toBeCloseTo(HZPP_Z2, 6);
    expect(scheduled).toBeNull(); // parked again — zero idle cost
    expect(vz.isAnimating()).toBe(false);
  });

  it('moves monotonically toward the target on a zoom-in (no overshoot)', () => {
    vz.snapTo(HZPP_Z1);
    vz.setTarget(HZPP_Z8); // big jump (1x -> 8x)
    let prev = vz.getDisplayedHzPerPixel();
    while (scheduled) {
      stepFrame(16.7);
      const cur = vz.getDisplayedHzPerPixel();
      expect(cur).toBeLessThanOrEqual(prev + 1e-9); // decreasing (span shrinks)
      expect(cur).toBeGreaterThanOrEqual(HZPP_Z8 - 1e-9);
      prev = cur;
    }
    expect(prev).toBeCloseTo(HZPP_Z8, 6);
  });

  it('retargets mid-glide and converges on the latest span', () => {
    vz.snapTo(HZPP_Z1);
    vz.setTarget(HZPP_Z8);
    stepFrame(16.7);
    stepFrame(16.7);
    // operator drags back out before the first glide finished
    vz.setTarget(HZPP_Z2);
    const frames = drainFrames(16.7);
    expect(frames).toBeGreaterThan(0);
    expect(vz.getDisplayedHzPerPixel()).toBeCloseTo(HZPP_Z2, 6);
    expect(scheduled).toBeNull();
  });

  it('is a no-op when the span is unchanged (steady RX never arms the loop)', () => {
    vz.snapTo(HZPP_Z2);
    vz.setTarget(HZPP_Z2);
    vz.setTarget(HZPP_Z2);
    expect(scheduled).toBeNull();
    expect(vz.isAnimating()).toBe(false);
  });

  it('snapTo hard-sets with no glide (reset path)', () => {
    vz.snapTo(HZPP_Z1);
    vz.setTarget(HZPP_Z8);
    expect(scheduled).not.toBeNull();
    vz.snapTo(HZPP_Z2); // hard reset mid-glide
    expect(vz.getDisplayedHzPerPixel()).toBe(HZPP_Z2);
    expect(vz.getTargetHzPerPixel()).toBe(HZPP_Z2);
  });

  it('ignores non-positive spans', () => {
    vz.snapTo(HZPP_Z1);
    vz.setTarget(0);
    vz.setTarget(-5);
    expect(vz.getTargetHzPerPixel()).toBe(HZPP_Z1);
  });
});
