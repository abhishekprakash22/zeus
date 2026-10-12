// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

import { describe, expect, it } from 'vitest';
import { PHOS_DECAY, PHOS_HIT, phosphorNeedsReset } from './panadapter';
import { spotlightExtents } from '../components/PassbandOverlay';
import { PAN_FS, PAN_FILL_FS, PHOS_DISPLAY_FS } from './shaders';

describe('phosphor persistence', () => {
  const g = { offsetPx: 0, scaleX: 1, dbMin: -140, dbMax: -60, w: 640, h: 120 };

  it('starts empty and keeps accumulating while the view is still', () => {
    expect(phosphorNeedsReset(null, g)).toBe(true);
    expect(phosphorNeedsReset(g, { ...g })).toBe(false);
    expect(phosphorNeedsReset(g, { ...g, offsetPx: 0.3 })).toBe(false);
  });

  it('throws away screen-space history when the view or scale moves', () => {
    expect(phosphorNeedsReset(g, { ...g, offsetPx: 2 })).toBe(true);
    expect(phosphorNeedsReset(g, { ...g, scaleX: 0.98 })).toBe(true);
    expect(phosphorNeedsReset(g, { ...g, dbMax: -50 })).toBe(true);
    expect(phosphorNeedsReset(g, { ...g, w: 320 })).toBe(true);
  });

  it('a steady carrier saturates while a single burst fades within about a second', () => {
    // Equilibrium of d = d*decay + hit.
    expect(PHOS_HIT / (1 - PHOS_DECAY)).toBeGreaterThan(2);
    // One hit after 30 frames (~1 s) is a small fraction of itself.
    expect(Math.pow(PHOS_DECAY, 30)).toBeLessThan(0.4);
  });
});

describe('palette trace shaders', () => {
  it('trace and fill colour through the shared palette LUT', () => {
    for (const src of [PAN_FS, PAN_FILL_FS, PHOS_DISPLAY_FS]) {
      expect(src).toContain('uniform sampler2D uLut;');
      expect(src).toContain('lutColor(');
    }
  });
});

describe('passband spotlight', () => {
  it('dims everything left and right of the passband', () => {
    expect(spotlightExtents(40, 46)).toEqual({ leftW: 40, rightL: 46 });
  });
  it('clamps a passband that runs off either edge', () => {
    expect(spotlightExtents(-5, 3)).toEqual({ leftW: 0, rightL: 3 });
    expect(spotlightExtents(97, 120)).toEqual({ leftW: 97, rightL: 100 });
  });
});
