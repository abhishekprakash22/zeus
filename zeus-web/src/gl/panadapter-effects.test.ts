// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

import { describe, expect, it } from 'vitest';
import {
  estimateFloorDb,
  PAL_GATE_HI_DB,
  PAL_GATE_LO_DB,
  paletteGateDb,
  trackFloorDb,
  PHOS_DECAY,
  PHOS_GATE_HI_DB,
  PHOS_GATE_LO_DB,
  PHOS_HIT,
  phosphorGate,
  phosphorNeedsReset,
} from './panadapter';
import { spotlightExtents } from '../components/PassbandOverlay';
import { PAN_FS, PAN_FILL_FS, PHOS_DISPLAY_FS, PHOS_SPLAT_FS } from './shaders';
import { PHOS_HALO_IN_PX, PHOS_HALO_OUT_PX, PHOSPHOR_FILL_ALPHA_TOP, traceStyle } from './panadapter';

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

  it('a steady carrier still saturates while a burst fades fast', () => {
    // Equilibrium of d = d*decay + hit, through the display's 1 - exp(-1.6 d).
    const eq = PHOS_HIT / (1 - PHOS_DECAY);
    expect(1 - Math.exp(-1.6 * eq)).toBeGreaterThan(0.85);
    // A burst halves within ~10 frames (~1/3 s at 30 fps)…
    expect(Math.pow(PHOS_DECAY, 10)).toBeLessThan(0.5);
    // …and is under a tenth of itself within a second.
    expect(Math.pow(PHOS_DECAY, 30)).toBeLessThan(0.1);
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

describe('phosphor noise-floor gate', () => {
  // A band of noise around -120 dB with ±3 dB wobble and two signals.
  const noisy = () => {
    const a = new Float32Array(2048);
    let seed = 3;
    const rnd = () => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff);
    for (let i = 0; i < a.length; i++) a[i] = -120 + (rnd() - 0.5) * 6;
    for (let i = 700; i < 720; i++) a[i] = -95; // strong carrier
    for (let i = 1500; i < 1504; i++) a[i] = -112; // weak signal, +8 dB
    return a;
  };

  it('finds the floor under the signals', () => {
    expect(estimateFloorDb(noisy())).toBeCloseTo(-120, 0);
    expect(Number.isNaN(estimateFloorDb(new Float32Array(0)))).toBe(true);
  });

  it('leaves the noise band out and lets real signals through', () => {
    const dbMin = -140;
    const dbMax = -60;
    const g = phosphorGate(-120, dbMin, dbMax);
    const level = (db: number) => (db - dbMin) / (dbMax - dbMin);
    const weight = (db: number) => {
      const t = Math.min(1, Math.max(0, (level(db) - g.lo) / (g.hi - g.lo)));
      return t * t * (3 - 2 * t);
    };
    // Noise up to its +3 dB excursions deposits nothing.
    expect(weight(-120)).toBe(0);
    expect(weight(-117)).toBe(0);
    // A weak +8 dB signal still glows; a strong carrier is at full weight.
    expect(weight(-112)).toBeGreaterThan(0.4);
    expect(weight(-95)).toBe(1);
    expect(PHOS_GATE_HI_DB).toBeGreaterThan(PHOS_GATE_LO_DB);
  });

  it('opens fully when there is no floor estimate yet', () => {
    expect(phosphorGate(Number.NaN, -140, -60)).toEqual({ lo: 0, hi: 0 });
  });

  it('the splat shader applies the gate', () => {
    expect(PHOS_SPLAT_FS).toContain('smoothstep(uGateLo');
  });
});

describe('palette trace follows the noise floor, not the waterfall window', () => {
  const ss = (lo: number, hi: number, x: number) => {
    const t = Math.min(1, Math.max(0, (x - lo) / (hi - lo)));
    return t * t * (3 - 2 * t);
  };

  it('keeps the floor in the trace colour on a quiet band and a noisy one', () => {
    // 20 m floor near -128 dB, 40 m floor near -112 dB (photos): with a fixed
    // waterfall-window gate the 40 m floor fell inside the palette (blue).
    for (const floor of [-128, -112, -100]) {
      const g = paletteGateDb(floor);
      expect(ss(g.lo, g.hi, floor)).toBe(0);
      expect(ss(g.lo, g.hi, floor + 3)).toBe(0);
      expect(ss(g.lo, g.hi, floor + 20)).toBe(1);
    }
  });

  it('is shut until the first floor estimate', () => {
    const g = paletteGateDb(Number.NaN);
    expect(ss(g.lo, g.hi, -20)).toBe(0);
    expect(PAL_GATE_HI_DB).toBeGreaterThan(PAL_GATE_LO_DB);
  });

  it('eases on drift and snaps on a band change', () => {
    expect(trackFloorDb(Number.NaN, -120)).toBe(-120);
    const eased = trackFloorDb(-120, -118);
    expect(eased).toBeGreaterThan(-120);
    expect(eased).toBeLessThan(-118);
    expect(trackFloorDb(-128, -112)).toBe(-112);
    expect(trackFloorDb(-120, Number.NaN)).toBe(-120);
  });

  it('the trace and fill shaders take their palette weight from the floor gate', () => {
    expect(PAN_FS).toContain('paletteWeight(vPal)');
    expect(PAN_FILL_FS).toContain('paletteWeight(v_pal)');
  });
});

describe('phosphor keeps the live trace readable', () => {
  it('cuts the glow away around the live trace, measured to the segment span', () => {
    expect(PHOS_DISPLAY_FS).toContain('smoothstep(uHaloInPx, uHaloOutPx, dy)');
    expect(PHOS_DISPLAY_FS).toContain('levelAt(b0 + 1)');
    expect(PHOS_HALO_OUT_PX).toBeGreaterThan(PHOS_HALO_IN_PX);
    expect(PHOS_HALO_IN_PX).toBeGreaterThanOrEqual(1);
  });
});

describe('the live trace stays THE line under phosphor', () => {
  it('Subtle: trace and fill take the palette, full fill', () => {
    expect(traceStyle(1, 0, false)).toEqual({ traceLutMix: 1, fillLutMix: 1, fillAlphaTop: 0.55 });
  });
  it('Full (phosphor): single trace colour and a faint anchoring fill', () => {
    const s = traceStyle(1, 0, true);
    expect(s.traceLutMix).toBe(0);
    expect(s.fillLutMix).toBe(0);
    expect(s.fillAlphaTop).toBe(PHOSPHOR_FILL_ALPHA_TOP);
    expect(PHOSPHOR_FILL_ALPHA_TOP).toBeGreaterThan(0);
    expect(PHOSPHOR_FILL_ALPHA_TOP).toBeLessThan(0.55);
  });
  it('Signal Pop keeps owning its colouring', () => {
    expect(traceStyle(1, 1, true)).toEqual({ traceLutMix: 0, fillLutMix: 0, fillAlphaTop: 0.55 });
  });
});
