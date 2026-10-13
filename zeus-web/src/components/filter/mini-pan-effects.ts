// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Display effects for the filter mini-pan (the 2D canvas in the filter card
// and ribbon), mirroring what Off / Subtle / Full do on the main panadapter:
//
//   Subtle  palette-coloured heat fill and trace — each column takes the
//           waterfall palette's colour for its level (waterfall dB window),
//           faded in from floor + PAL_GATE_LO_DB to floor + PAL_GATE_HI_DB so
//           the floor keeps the operator's trace colour on every band.
//   Full    phosphor persistence in the plot, gated at the noise floor, with
//           a clear halo around the live trace; the trace and a faint fill
//           go back to the single trace colour (the glow carries the
//           palette), and the peak-hold ghost stands aside.
//
// Same constants as the GL panadapter so both surfaces behave alike. The
// mini-pan is small (a few hundred columns at DPR ≤ 1), so the phosphor is a
// plain Float32Array decayed and splatted on the CPU per new frame.

import {
  PAL_GATE_HI_DB,
  PAL_GATE_LO_DB,
  PHOS_DECAY,
  PHOS_GATE_HI_DB,
  PHOS_GATE_LO_DB,
  PHOS_HALO_IN_PX,
  PHOS_HALO_OUT_PX,
  PHOS_HIT,
} from '../../gl/panadapter';

export type Rgb = [number, number, number];

const PHOS_GAIN = 1.6;
/** Heat-fill strength multiplier under phosphor (the glow is the colour). */
export const MINI_PHOSPHOR_FILL_SCALE = 0.4;

export function smoothstep(e0: number, e1: number, x: number): number {
  if (e1 <= e0) return x >= e1 ? 1 : 0;
  const t = Math.min(1, Math.max(0, (x - e0) / (e1 - e0)));
  return t * t * (3 - 2 * t);
}

/** How much palette colour a column at `db` takes over a floor at `floorDb`. */
export function columnPaletteWeight(db: number, floorDb: number): number {
  if (!Number.isFinite(db) || !Number.isFinite(floorDb)) return 0;
  return smoothstep(floorDb + PAL_GATE_LO_DB, floorDb + PAL_GATE_HI_DB, db);
}

/** Palette colour for a level, read through the waterfall's dB window. */
export function lutRgb(lut: Uint8Array, db: number, wfMin: number, wfMax: number): Rgb {
  const n = Math.min(1, Math.max(0, (db - wfMin) / Math.max(0.0001, wfMax - wfMin)));
  const i = Math.round(n * 255) * 4;
  return [lut[i] ?? 0, lut[i + 1] ?? 0, lut[i + 2] ?? 0];
}

export function mixRgb(a: Rgb, b: Rgb, t: number): Rgb {
  const k = Math.min(1, Math.max(0, t));
  return [
    Math.round(a[0] + (b[0] - a[0]) * k),
    Math.round(a[1] + (b[1] - a[1]) * k),
    Math.round(a[2] + (b[2] - a[2]) * k),
  ];
}

/**
 * CPU phosphor for the mini-pan plot. Coordinates are canvas pixels; larger Y
 * is lower on screen (a lower level), as in the mini-pan's own mapping.
 */
export class MiniPhosphor {
  private density: Float32Array = new Float32Array(0);
  private image: ImageData | null = null;
  private scratch: HTMLCanvasElement | null = null;
  w = 0;
  h = 0;
  key = '';

  /** Size to the plot and drop history when the view (key) changes. */
  ensure(w: number, h: number, key: string): void {
    if (w !== this.w || h !== this.h) {
      this.w = w;
      this.h = h;
      this.density = new Float32Array(w * h);
      this.image = null;
      this.key = key;
      return;
    }
    if (key !== this.key) {
      this.key = key;
      this.density.fill(0);
    }
  }

  /** Read-only access for tests. */
  densityAt(x: number, y: number): number {
    return this.density[y * this.w + x] ?? 0;
  }

  /**
   * One new frame: decay everything, then splat the trace. Each column's
   * segment covers the vertical span to the next column (as the drawn line
   * does); every pixel in it gains PHOS_HIT weighted by the noise gate, which
   * fades in between gateLoY (floor + PHOS_GATE_LO_DB) and gateHiY
   * (floor + PHOS_GATE_HI_DB) for that column.
   */
  step(
    traceY: Float32Array,
    gateLoY: Float32Array,
    gateHiY: Float32Array,
    plotTop: number,
    plotBottom: number,
  ): void {
    const d = this.density;
    for (let i = 0; i < d.length; i++) {
      const v = d[i]! * PHOS_DECAY - 0.0005;
      d[i] = v > 0 ? v : 0;
    }
    const w = Math.min(this.w, traceY.length);
    const top = Math.max(0, Math.floor(plotTop));
    const bottom = Math.min(this.h - 1, Math.ceil(plotBottom));
    for (let x = 0; x < w; x++) {
      const y0 = traceY[x]!;
      const y1 = traceY[x + 1 < w ? x + 1 : x]!;
      const lo = Math.max(top, Math.floor(Math.min(y0, y1)));
      const hi = Math.min(bottom, Math.ceil(Math.max(y0, y1)));
      const gLo = gateLoY[x]!;
      const gHi = gateHiY[x]!;
      const span = gLo - gHi; // > 0: the gate's top sits above its bottom
      for (let y = lo; y <= hi; y++) {
        // A collapsed gate (the mini-pan maps everything under its own noise
        // gate onto the bottom rail): only pixels clearly above it count.
        const wgt = span > 0 ? smoothstep(0, 1, (gLo - y) / span) : y < gLo - 1 ? 1 : 0;
        if (wgt <= 0) continue;
        d[y * this.w + x]! += PHOS_HIT * wgt;
      }
    }
  }

  /**
   * Draw the glow through the palette, with a clear halo around the live
   * trace (distance to each column's vertical span), at device scale `dpr`.
   */
  render(ctx: CanvasRenderingContext2D, lut: Uint8Array, liveY: Float32Array, dpr: number): void {
    if (this.w <= 0 || this.h <= 0) return;
    if (!this.image || this.image.width !== this.w || this.image.height !== this.h) {
      this.image = ctx.createImageData(this.w, this.h);
    }
    const px = this.image.data;
    const d = this.density;
    const haloIn = PHOS_HALO_IN_PX * dpr;
    const haloOut = PHOS_HALO_OUT_PX * dpr;
    for (let x = 0; x < this.w; x++) {
      const a0 = liveY[x] ?? 1e9;
      const a1 = liveY[x + 1 < this.w ? x + 1 : x] ?? a0;
      const lo = Math.min(a0, a1);
      const hi = Math.max(a0, a1);
      for (let y = 0; y < this.h; y++) {
        const o = (y * this.w + x) * 4;
        const v = d[y * this.w + x]!;
        if (v <= 0.0005) {
          px[o + 3] = 0;
          continue;
        }
        const n = 1 - Math.exp(-v * PHOS_GAIN);
        const li = Math.round((0.48 + 0.52 * n) * 255) * 4;
        const dy = y < lo ? lo - y : y > hi ? y - hi : 0;
        const a = smoothstep(0, 0.06, v) * (0.5 + 0.35 * n) * smoothstep(haloIn, haloOut, dy);
        px[o] = lut[li] ?? 0;
        px[o + 1] = lut[li + 1] ?? 0;
        px[o + 2] = lut[li + 2] ?? 0;
        px[o + 3] = Math.round(a * 255);
      }
    }
    // Through a scratch canvas so it composites (putImageData would replace).
    if (!this.scratch) this.scratch = document.createElement('canvas');
    if (this.scratch.width !== this.w || this.scratch.height !== this.h) {
      this.scratch.width = this.w;
      this.scratch.height = this.h;
    }
    this.scratch.getContext('2d')?.putImageData(this.image, 0, 0);
    ctx.drawImage(this.scratch, 0, 0);
  }
}

export { PHOS_GATE_HI_DB, PHOS_GATE_LO_DB };
