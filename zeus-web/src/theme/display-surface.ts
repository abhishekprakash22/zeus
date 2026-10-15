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
// Display surface — the background the spectrum displays (panadapter,
// waterfall, filter mini-pan) sit on, chosen as a DEPTH along a curated blue
// path: 0 = black (the classic look), then Midnight, Navy, Ocean, Steel.
//
//   Dark theme:  0..DARK_MAX (black..Ocean), default 0 — unchanged look.
//   Light theme: blue 4..LIGHT_MAX (Midnight..Steel), default Navy; or the
//                paper displays (theme/paper.ts) instead.
//
// The waterfall palettes keep their colours; their black floor is lifted to
// the chosen blue (gl/colormap setDisplayFloor) so the noise blends into the
// background. The path is capped at Steel: beyond it the background rises
// into the palettes' own blues and weak signals start to sink.

export type Rgb = [number, number, number];

const PATH: [number, Rgb][] = [
  [0, [0, 0, 0]],
  [4, [8, 14, 26]],
  [22, [12, 24, 44]],
  [40, [17, 36, 64]],
  [58, [26, 52, 86]],
];

export const DISPLAY_DEPTH_MIN = 0;
export const DARK_MAX_DEPTH = 40; // Ocean
export const LIGHT_MIN_DEPTH = 4; // Midnight
export const LIGHT_MAX_DEPTH = 58; // Steel
export const DARK_DEFAULT_DEPTH = 0; // black: the classic look
export const LIGHT_DEFAULT_DEPTH = 22; // Navy

export const DISPLAY_PRESETS: { label: string; depth: number }[] = [
  { label: 'Black', depth: 0 },
  { label: 'Midnight', depth: 4 },
  { label: 'Navy', depth: 22 },
  { label: 'Ocean', depth: 40 },
  { label: 'Steel', depth: 58 },
];

const toHex = (c: Rgb) =>
  '#' + c.map((v) => Math.max(0, Math.min(255, Math.round(v))).toString(16).padStart(2, '0')).join('').toUpperCase();

export function depthRgb(depth: number): Rgb {
  const d = Math.max(PATH[0]![0], Math.min(PATH[PATH.length - 1]![0], depth));
  let k = 0;
  while (k < PATH.length - 2 && d > PATH[k + 1]![0]) k++;
  const [d0, a] = PATH[k]!;
  const [d1, b] = PATH[k + 1]!;
  const t = (d - d0) / (d1 - d0);
  return [0, 1, 2].map((i) => Math.round(a[i]! + (b[i]! - a[i]!) * t)) as Rgb;
}

export function depthHex(depth: number): string {
  return toHex(depthRgb(depth));
}

/** The depth whose colour is nearest a stored hex (0 for junk). */
export function depthOf(hex: string | null | undefined): number {
  const m = hex ? /^#?([0-9a-f]{6})$/i.exec(hex.trim()) : null;
  if (!m) return 0;
  const v = parseInt(m[1]!, 16);
  const c: Rgb = [(v >> 16) & 255, (v >> 8) & 255, v & 255];
  let best = 0;
  let bestD = Infinity;
  for (let d = 0; d <= LIGHT_MAX_DEPTH; d++) {
    const p = depthRgb(d);
    const dist = (p[0] - c[0]) ** 2 + (p[1] - c[1]) ** 2 + (p[2] - c[2]) ** 2;
    if (dist < bestD) {
      bestD = dist;
      best = d;
    }
  }
  return best;
}

export function clampDepth(depth: number, theme: 'dark' | 'light'): number {
  const lo = theme === 'light' ? LIGHT_MIN_DEPTH : DISPLAY_DEPTH_MIN;
  const hi = theme === 'light' ? LIGHT_MAX_DEPTH : DARK_MAX_DEPTH;
  return Math.round(Math.max(lo, Math.min(hi, depth)));
}

/**
 * Tokens for a blue/black display surface: the display backgrounds, plus the
 * LIGHT inks every display overlay (scales, axis, RX chip, mini-pan labels)
 * needs on a dark surface — whatever the chassis theme around it.
 */
export function blueDisplayTokens(depth: number): Record<string, string> {
  const bg = depthHex(depth);
  const [r, g, b] = depthRgb(depth);
  return {
    '--spec-bg': depth === 0 ? '#151A20' : bg,
    '--wf-0': depth === 0 ? '#000814' : bg,
    '--spec-grid': 'rgba(255, 255, 255, 0.08)',
    '--disp-dim-rgb': '0, 0, 0',
    '--disp-chip-bg': 'rgba(8, 10, 14, 0.78)',
    '--disp-chip-line': 'rgba(255, 255, 255, 0.16)',
    '--scale-bg': 'rgba(10, 10, 10, 0.45)',
    '--scale-ink': '#B4BFCC',
    '--scale-tick': '#7E8A98',
    '--axis-bg': 'rgba(10, 10, 10, 0.55)',
    '--axis-ink': '#DCE3EA',
    '--axis-tick': '#A3AEBB',
    '--minipan-bg': depth === 0 ? 'rgba(2, 4, 7, 0.48)' : `rgb(${r}, ${g}, ${b})`,
    '--disp-ink-0': '#EDEEF1',
    '--disp-ink-1': '#CCD3DB',
    '--disp-ink-2': '#9AA6B3',
    '--disp-ink-3': '#6F7B89',
  };
}
