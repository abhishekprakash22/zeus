// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

import { describe, expect, it } from 'vitest';
import { occupiedExtent, smoothDb } from './observe';

const FLOOR = -120;

/** A ragged voice-like body: strong peaks with deep dips between them. */
function voice(n: number, lo: number, hi: number): Float32Array {
  const a = new Float32Array(n).fill(FLOOR);
  for (let i = lo; i <= hi; i++) {
    // 20 dB swings every few bins, like formants and pitch harmonics.
    a[i] = -85 + ((i * 7) % 5 === 0 ? -22 : (i % 3) * 6);
  }
  return a;
}

describe('occupied extent', () => {
  it('measures a ragged voice body as one signal, not fragments', () => {
    // 10 Hz/bin: a 2.4 kHz body from bin 400 to 640.
    const pan = voice(1000, 400, 640);
    const smooth = smoothDb(pan, 12);
    const { lo, hi, fill } = occupiedExtent(smooth, null, FLOOR, 520, 40, 1200);
    expect(lo).toBeLessThanOrEqual(405);
    expect(hi).toBeGreaterThanOrEqual(635);
    expect((hi - lo + 1) * 10).toBeGreaterThan(2_200);
    expect((hi - lo + 1) * 10).toBeLessThan(2_900);
    expect(fill).toBeGreaterThan(0.8);
  });

  it('keeps a lone carrier narrow', () => {
    const pan = new Float32Array(1000).fill(FLOOR);
    pan[500] = -70;
    pan[499] = -90;
    pan[501] = -90;
    const { lo, hi } = occupiedExtent(smoothDb(pan, 1), null, FLOOR, 500, 4, 1200);
    expect(hi - lo).toBeLessThanOrEqual(4);
  });

  it('does not bridge two signals farther apart than the bridge', () => {
    const pan = new Float32Array(1000).fill(FLOOR);
    for (let i = 300; i <= 310; i++) pan[i] = -80;
    for (let i = 400; i <= 410; i++) pan[i] = -80;
    const { lo, hi } = occupiedExtent(smoothDb(pan, 2), null, FLOOR, 305, 40, 1200);
    expect(lo).toBeGreaterThanOrEqual(296);
    expect(hi).toBeLessThanOrEqual(314);
  });
});
