// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

import { describe, expect, it } from 'vitest';
import {
  PAPER_MAX_LIGHTNESS,
  PAPER_MIN_LIGHTNESS,
  contrast,
  paperParams,
  paperSurface,
  paperTokens,
  paperTraceRgb,
  readableOn,
  rgbOf,
  type PaperTint,
} from './paper';
import { lutFor, paperLutVersion, setPaperFloor } from '../gl/colormap';

const TINTS: PaperTint[] = ['cool', 'neutral', 'warm'];
const ALL = () => {
  const out: { l: number; t: PaperTint; s: string }[] = [];
  for (let l = PAPER_MIN_LIGHTNESS; l <= PAPER_MAX_LIGHTNESS; l++) for (const t of TINTS) out.push({ l, t, s: paperSurface(l, t) });
  return out;
};
const c = (hex: string) => rgbOf(hex)!;

describe('paper surface', () => {
  it('the slider position and tint survive the round trip through the stored hex', () => {
    for (const { l, t, s } of ALL()) expect(paperParams(s)).toEqual({ lightness: l, tint: t });
  });

  it('clamps out-of-range positions and reads junk as the default', () => {
    expect(paperParams(paperSurface(10, 'neutral')).lightness).toBe(PAPER_MIN_LIGHTNESS);
    expect(paperParams(paperSurface(100, 'neutral')).lightness).toBe(PAPER_MAX_LIGHTNESS);
    expect(paperParams('nonsense')).toEqual({ lightness: 86, tint: 'neutral' });
  });
});

describe('paper tokens stay readable at every slider position and tint', () => {
  it('VFO digits and primary text 7:1, secondary text and scales 4.5:1, accent 4.5:1', () => {
    for (const { s } of ALL()) {
      const t = paperTokens(s);
      const S = c(s);
      const card = c(t['--bg-1']!);
      expect(contrast(c(t['--fg-0']!), card)).toBeGreaterThanOrEqual(7);
      expect(contrast(c(t['--fg-1']!), S)).toBeGreaterThanOrEqual(7);
      expect(contrast(c(t['--fg-2']!), S)).toBeGreaterThanOrEqual(4.5);
      expect(contrast(c(t['--fg-3']!), S)).toBeGreaterThanOrEqual(4.5);
      expect(contrast(c(t['--scale-ink']!), S)).toBeGreaterThanOrEqual(4.5);
      expect(contrast(c(t['--accent-bright']!), card)).toBeGreaterThanOrEqual(4.5);
    }
  });

  it('the displays sit on the paper itself, and the spotlight washes toward it', () => {
    const s = paperSurface(80, 'warm');
    const t = paperTokens(s);
    expect(t['--spec-bg']).toBe(s);
    expect(t['--wf-0']).toBe(s);
    expect(t['--disp-dim-rgb']).toBe(c(s).join(', '));
  });

  it('cards are lighter than the paper and the chassis darker', () => {
    const s = paperSurface(80, 'neutral');
    const t = paperTokens(s);
    expect(c(t['--bg-1']!)[0]).toBeGreaterThan(c(s)[0]);
    expect(c(t['--bg-0']!)[0]).toBeLessThan(c(s)[0]);
  });
});

describe('trace ink on paper', () => {
  it("keeps the operator's hue and reaches 3.5:1 on any surface", () => {
    for (const trace of ['#FFA028', '#7FD4FF', '#00FF00', '#FFFFFF']) {
      for (const { s } of ALL()) expect(contrast(paperTraceRgb(trace, s), c(s))).toBeGreaterThanOrEqual(3.5);
    }
    const amber = paperTraceRgb('#FFA028', paperSurface(86, 'neutral'));
    expect(amber[0]).toBeGreaterThan(amber[2]); // still amber-ish, just darker
  });

  it('leaves already-readable colours alone', () => {
    expect(readableOn([14, 26, 38], [219, 219, 219], 7)).toEqual([14, 26, 38]);
  });
});

describe('paper waterfall palette', () => {
  it('its floor is the surface, and moving the surface changes the LUT', () => {
    setPaperFloor([200, 200, 200]);
    const v = paperLutVersion();
    const a = lutFor('paper');
    expect(Array.from(a.slice(0, 3))).toEqual([200, 200, 200]);
    setPaperFloor([230, 225, 215]);
    expect(paperLutVersion()).toBe(v + 1);
    const b = lutFor('paper');
    expect(Array.from(b.slice(0, 3))).toEqual([230, 225, 215]);
    // Strong signals run to a dark crimson regardless of the floor.
    expect(b[255 * 4]!).toBeGreaterThan(b[255 * 4 + 2]!);
  });

  it('setting the same floor again is a no-op', () => {
    setPaperFloor([210, 210, 210]);
    const v = paperLutVersion();
    setPaperFloor([210, 210, 210]);
    expect(paperLutVersion()).toBe(v);
  });
});
