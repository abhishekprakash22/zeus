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
// Paper — the light theme's surface, chosen as ONE grey (a lightness slider
// plus a cool / neutral / warm tint) from which everything else is derived:
// chassis, cards, lines, ink, the panadapter / waterfall backgrounds and the
// waterfall palette's floor. Ink is darkened automatically wherever the
// chosen grey would push a text pair below its contrast floor (WCAG: 7:1 for
// the VFO digits and primary text, 4.5:1 for secondary text and the scales),
// so any slider position stays readable.
//
// The whole choice persists as one hex, the surface itself ("--paper-surface"
// in the theme overrides), so the existing /api/theme-settings store keeps it
// with no backend change.

export type Rgb = [number, number, number];
export type PaperTint = 'cool' | 'neutral' | 'warm';

export const PAPER_MIN_LIGHTNESS = 62;
export const PAPER_MAX_LIGHTNESS = 96;
export const PAPER_DEFAULT_LIGHTNESS = 86;
export const PAPER_DEFAULT_TINT: PaperTint = 'neutral';
export const PAPER_SURFACE_KEY = '--paper-surface';

const TINT: Record<PaperTint, Rgb> = { cool: [-3, 0, 5], neutral: [0, 0, 0], warm: [5, 2, -4] };

const clamp8 = (v: number) => Math.max(0, Math.min(255, Math.round(v)));

export function hex(c: Rgb): string {
  return '#' + c.map((v) => clamp8(v).toString(16).padStart(2, '0')).join('').toUpperCase();
}

export function rgbOf(h: string): Rgb | null {
  const m = /^#?([0-9a-f]{6})$/i.exec(h.trim());
  if (!m) return null;
  const v = parseInt(m[1]!, 16);
  return [(v >> 16) & 255, (v >> 8) & 255, v & 255];
}

/** A grey at `lightness` percent, plus `delta` percent, with the tint's offsets. */
export function greyAt(lightness: number, tint: PaperTint, delta = 0): Rgb {
  const v = 2.55 * Math.max(0, Math.min(100, lightness + delta));
  const t = TINT[tint];
  return [clamp8(v + t[0]), clamp8(v + t[1]), clamp8(v + t[2])];
}

/** The persisted surface for a slider position and tint. */
export function paperSurface(lightness: number, tint: PaperTint): string {
  const l = Math.round(Math.max(PAPER_MIN_LIGHTNESS, Math.min(PAPER_MAX_LIGHTNESS, lightness)));
  return hex(greyAt(l, tint));
}

/** Slider position and tint back from a stored surface (defaults if unreadable). */
export function paperParams(surface: string | null | undefined): { lightness: number; tint: PaperTint } {
  const c = surface ? rgbOf(surface) : null;
  if (!c) return { lightness: PAPER_DEFAULT_LIGHTNESS, tint: PAPER_DEFAULT_TINT };
  const tilt = c[0] - c[2];
  const tint: PaperTint = tilt >= 4 ? 'warm' : tilt <= -4 ? 'cool' : 'neutral';
  const t = TINT[tint];
  const mean = (c[0] - t[0] + (c[1] - t[1]) + (c[2] - t[2])) / 3;
  const lightness = Math.max(PAPER_MIN_LIGHTNESS, Math.min(PAPER_MAX_LIGHTNESS, Math.round(mean / 2.55)));
  return { lightness, tint };
}

export function luminance(c: Rgb): number {
  const ch = c.map((v) => {
    const s = v / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  });
  return 0.2126 * ch[0]! + 0.7152 * ch[1]! + 0.0722 * ch[2]!;
}

export function contrast(a: Rgb, b: Rgb): number {
  const la = luminance(a);
  const lb = luminance(b);
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}

/**
 * `fg` on `bg` at no less than `min`: scaled toward black (hue kept) until it
 * passes. Already-passing colours come back unchanged.
 */
export function readableOn(fg: Rgb, bg: Rgb, min: number): Rgb {
  if (contrast(fg, bg) >= min) return fg;
  let k = 1;
  let c: Rgb = fg;
  for (let i = 0; i < 60 && contrast(c, bg) < min; i++) {
    k *= 0.93;
    c = [clamp8(fg[0] * k), clamp8(fg[1] * k), clamp8(fg[2] * k)];
  }
  return c;
}

// Base inks (the dark-on-light design values) before contrast correction.
const INK_PRIMARY: Rgb = [14, 26, 38]; // #0e1a26
const INK_SECONDARY: Rgb = [47, 59, 72]; // #2f3b48
const INK_TERTIARY: Rgb = [74, 86, 101]; // #4a5665
const INK_MUTED: Rgb = [91, 104, 120]; // #5b6878
const ACCENT: Rgb = [31, 111, 184]; // #1f6fb8

/**
 * Every CSS custom property the light (paper) theme sets for a surface. The
 * keys are the existing chassis tokens plus the display / card tokens the
 * hard-coded dark surfaces now read.
 */
export function paperTokens(surface: string): Record<string, string> {
  const { lightness: L, tint } = paperParams(surface);
  const S = rgbOf(surface) ?? greyAt(L, tint);
  const g = (d: number) => greyAt(L, tint, d);
  const card = g(Math.min(5, 98 - L));
  const chrome = g(-4);
  const fg0 = readableOn(INK_PRIMARY, card, 7);
  const fg1 = readableOn(INK_SECONDARY, S, 7);
  const fg2 = readableOn(INK_TERTIARY, S, 4.5);
  const fg3 = readableOn(INK_MUTED, S, 4.5);
  const fg4 = readableOn(g(-30), S, 2.2);
  const accent = readableOn(ACCENT, card, 4.5);
  const line = g(-22);
  const lineSoft = g(-12);
  const lineStrong = g(-34);
  const rgbList = (c: Rgb) => `${c[0]}, ${c[1]}, ${c[2]}`;
  return {
    'color-scheme': 'light',
    '--bg-app': hex(chrome),
    '--bg-workspace': hex(chrome),
    '--bg-0': hex(chrome),
    '--bg-1': hex(card),
    '--bg-2': hex(g(-2)),
    '--bg-3': hex(g(-7)),
    '--line': hex(line),
    '--line-soft': hex(lineSoft),
    '--line-strong': hex(lineStrong),
    '--fg-0': hex(fg0),
    '--fg-1': hex(fg1),
    '--fg-2': hex(fg2),
    '--fg-3': hex(fg3),
    '--fg-4': hex(fg4),
    '--accent-bright': hex(accent),
    '--panel-head-top': hex(card),
    '--panel-head-bot': hex(g(Math.min(2, 98 - L))),
    '--panel-top': hex(card),
    '--panel-bot': hex(g(Math.min(2, 98 - L))),
    '--panel-border': hex(line),
    '--panel-edge': hex(line),
    '--panel-hl-top': 'rgba(255, 255, 255, 0.45)',
    '--panel-shadow': '0 1px 0 rgba(255,255,255,0.45) inset, 0 1px 3px rgba(20,30,45,0.14)',
    '--btn-top': hex(card),
    '--btn-bot': hex(g(Math.min(2, 98 - L))),
    '--btn-hl': 'rgba(255, 255, 255, 0.45)',
    '--btn-edge': hex(lineStrong),
    '--btn-text': hex(fg0),
    // Display surfaces (panadapter / waterfall) — the paper itself.
    '--spec-bg': hex(S),
    '--wf-0': hex(S),
    '--spec-grid': 'rgba(0, 0, 0, 0.08)',
    '--disp-dim-rgb': rgbList(S),
    '--disp-chip-bg': `rgba(${rgbList(card)}, 0.92)`,
    '--disp-chip-line': hex(lineSoft),
    '--scale-bg': `rgba(${rgbList(chrome)}, 0.72)`,
    '--scale-ink': hex(fg2),
    '--scale-tick': hex(fg3),
    // Floating cards (G2 VFO / meter cards, flags).
    '--card-bg': `rgba(${rgbList(card)}, 0.96)`,
    '--card-line': hex(lineSoft),
    '--card-shadow': '0 6px 20px rgba(20, 30, 45, 0.14)',
    '--card-head-bg': hex(g(Math.min(2, 98 - L))),
    '--card-bg-solid': hex(card),
    '--card-sunk': hex(g(-3)),
    '--flag-badge-ink': '#FFFFFF',
    '--select-bg': hex(card),
    '--select-ink': hex(fg0),
    '--axis-bg': `rgba(${rgbList(chrome)}, 0.78)`,
    '--axis-ink': hex(fg1),
    '--axis-tick': hex(fg3),
  };
}

/** The ink the panadapter trace should use on the paper (operator hue kept). */
export function paperTraceRgb(traceHex: string, surface: string): Rgb {
  const fg = rgbOf(traceHex) ?? [255, 160, 40];
  const bg = rgbOf(surface) ?? greyAt(PAPER_DEFAULT_LIGHTNESS, PAPER_DEFAULT_TINT);
  return readableOn(fg, bg, 3.5);
}
