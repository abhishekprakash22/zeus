// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

/** @vitest-environment jsdom */

import { describe, expect, it } from 'vitest';
import { columnPaletteWeight, lutRgb, MiniPhosphor, mixRgb } from './mini-pan-effects';

describe('mini-pan palette colour', () => {
  it('keeps the floor in the trace colour on a quiet and a noisy band', () => {
    for (const floor of [-128, -112]) {
      expect(columnPaletteWeight(floor, floor)).toBe(0);
      expect(columnPaletteWeight(floor + 3, floor)).toBe(0);
      expect(columnPaletteWeight(floor + 20, floor)).toBe(1);
    }
    expect(columnPaletteWeight(Number.NaN, -120)).toBe(0);
  });

  it('reads the palette through the waterfall window and mixes toward it', () => {
    const lut = new Uint8Array(256 * 4);
    for (let i = 0; i < 256; i++) lut.set([i, 255 - i, 7, 255], i * 4);
    expect(lutRgb(lut, -140, -140, -60)).toEqual([0, 255, 7]);
    expect(lutRgb(lut, -60, -140, -60)).toEqual([255, 0, 7]);
    expect(lutRgb(lut, -20, -140, -60)).toEqual([255, 0, 7]);
    expect(mixRgb([255, 160, 40], [0, 0, 0], 0)).toEqual([255, 160, 40]);
    expect(mixRgb([255, 160, 40], [0, 0, 0], 1)).toEqual([0, 0, 0]);
  });
});

describe('mini-pan phosphor', () => {
  const W = 8;
  const H = 40;
  const flat = (v: number) => Float32Array.from({ length: W }, () => v);

  it('the noise floor on the bottom rail leaves no glow; a signal does', () => {
    const p = new MiniPhosphor();
    p.ensure(W, H, 'k');
    // Floor rail at y=38; gate collapsed onto it (all under the mini-pan's gate).
    p.step(flat(38), flat(38), flat(38), 2, 38);
    for (let x = 0; x < W; x++) expect(p.densityAt(x, 38)).toBe(0);
    // A signal column rising to y=10, gate from y=30 (floor+4) to y=24 (floor+10).
    const trace = flat(38);
    trace[3] = 10;
    p.step(trace, flat(30), flat(24), 2, 38);
    expect(p.densityAt(3, 12)).toBeGreaterThan(0.1);
    expect(p.densityAt(3, 36)).toBe(0); // below the gate: nothing
  });

  it('fades between frames and starts over when the view changes', () => {
    const p = new MiniPhosphor();
    p.ensure(W, H, 'a');
    const trace = flat(38);
    trace[2] = 10;
    p.step(trace, flat(30), flat(24), 2, 38);
    const first = p.densityAt(2, 12);
    p.step(flat(38), flat(38), flat(38), 2, 38);
    expect(p.densityAt(2, 12)).toBeLessThan(first);
    p.ensure(W, H, 'b');
    expect(p.densityAt(2, 12)).toBe(0);
  });

  it('cuts a clear halo around the live trace when drawn', () => {
    const p = new MiniPhosphor();
    p.ensure(W, H, 'k');
    const trace = flat(38);
    for (let x = 0; x < W; x++) trace[x] = 10;
    for (let i = 0; i < 20; i++) p.step(trace, flat(30), flat(24), 2, 38);
    let captured: ImageData | null = null;
    const ctx = {
      createImageData: (w: number, h: number) => ({ width: w, height: h, data: new Uint8ClampedArray(w * h * 4) }),
      drawImage: () => {},
    } as unknown as CanvasRenderingContext2D;
    const lut = new Uint8Array(256 * 4).fill(200);
    p.render(ctx, lut, trace, 1);
    captured = (p as unknown as { image: ImageData }).image;
    const alphaAt = (x: number, y: number) => captured!.data[(y * W + x) * 4 + 3]!;
    // On the live trace: no glow. The glow itself was laid on the trace, so
    // the halo is what keeps the line clean.
    expect(alphaAt(4, 10)).toBe(0);
  });
});
