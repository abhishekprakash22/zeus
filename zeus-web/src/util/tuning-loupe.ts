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
// Tuning loupe — press and hold a finger on the panadapter or waterfall and a
// magnified view of the spectrum around the finger opens ABOVE it, so the
// signal you are aiming at is never hidden under your fingertip. Slide to
// aim; lift to tune there; slide off the display to cancel. Nothing is tuned
// until the finger lifts.
//
// The live surfaces are WebGL with no readback, so the loupe re-draws from
// display data: the current pan frame for the trace, and loupe-history rows
// for the waterfall underneath. It is one full-viewport 2D canvas with
// pointer-events:none, so it can never steal the gesture it is showing.

import { lutFor } from '../gl/colormap';
import { useConnectionStore } from '../state/connection-store';
import { selectDisplaySlice, useDisplayStore, type SpectrumReceiver } from '../state/display-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { loupeRows, type LoupeRow } from '../state/loupe-history';
import { effectiveRxWfWindow } from '../state/rx-db-window-store';
import { useTxStore } from '../state/tx-store';
import {
  displayFilterEdgesHz,
  getReceiverFilterHighHz,
  getReceiverFilterLowHz,
  getReceiverMode,
  getReceiverVfoHz,
  rxIndexOf,
} from '../state/receiver-state';
import { roundToStep } from './number';

/** Hold this long without moving to open the loupe (ms). */
export const LOUPE_HOLD_MS = 300;
/** Finger travel allowed during the hold; beyond it the press is a drag. */
export const LOUPE_SLOP_PX = 10;
/** How far above/below the display a finger may stray before release cancels. */
export const LOUPE_CANCEL_MARGIN_PX = 48;
/** Magnification relative to the current view. */
export const LOUPE_MAGNIFICATION = 4;
/** Loupe box size and its lift above the finger (CSS px). */
export const LOUPE_WIDTH_PX = 300;
export const LOUPE_HEIGHT_PX = 176;
export const LOUPE_LIFT_PX = 72;
const EDGE_GAP_PX = 6;
const SPECTRUM_FRACTION = 0.42;

const MAX_HZ = 10_000_000_000;

function roundRectPath(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number): void {
  if (typeof ctx.roundRect === 'function') {
    ctx.roundRect(x, y, w, h, r);
  } else {
    ctx.rect(x, y, w, h);
  }
}

export type LoupePlacement = { left: number; top: number; below: boolean };

/**
 * Where the loupe box goes for a finger at (x, y): centred over the finger and
 * lifted clear of it; dropped below the finger when there is no room above;
 * always clamped inside the viewport.
 */
export function placeLoupe(
  x: number,
  y: number,
  viewportW: number,
  viewportH: number,
  w = LOUPE_WIDTH_PX,
  h = LOUPE_HEIGHT_PX,
  lift = LOUPE_LIFT_PX,
): LoupePlacement {
  const left = Math.min(Math.max(EDGE_GAP_PX, x - w / 2), Math.max(EDGE_GAP_PX, viewportW - w - EDGE_GAP_PX));
  let top = y - lift - h;
  let below = false;
  if (top < EDGE_GAP_PX) {
    top = y + lift;
    below = true;
  }
  top = Math.min(Math.max(EDGE_GAP_PX, top), Math.max(EDGE_GAP_PX, viewportH - h - EDGE_GAP_PX));
  return { left, top, below };
}

/** True when a release at (x, y) should cancel instead of tuning. */
export function isLoupeCancelZone(
  x: number,
  y: number,
  rect: { left: number; right: number; top: number; bottom: number },
  margin = LOUPE_CANCEL_MARGIN_PX,
): boolean {
  return x < rect.left || x > rect.right || y < rect.top - margin || y > rect.bottom + margin;
}

/** The dial frequency a loupe release commits: the operator's tuning step grid. */
export function loupeCommitHz(aimHz: number, stepHz: number): number {
  if (!Number.isFinite(aimHz)) return 0;
  const q = stepHz > 0 ? roundToStep(aimHz, stepHz) : Math.round(aimHz);
  return Math.min(MAX_HZ, Math.max(0, q));
}

/** Linear-interpolated dB of a row at an absolute frequency (NaN off the row). */
export function sampleRowDb(bins: Float32Array, centerHz: number, hzPerPixel: number, hz: number): number {
  const n = bins.length;
  if (n === 0 || hzPerPixel <= 0) return Number.NaN;
  const pos = (hz - centerHz) / hzPerPixel + n / 2 - 0.5;
  if (pos < 0 || pos > n - 1) return Number.NaN;
  const i = Math.floor(pos);
  const f = pos - i;
  const a = bins[i]!;
  const b = bins[Math.min(n - 1, i + 1)]!;
  return a + (b - a) * f;
}

/**
 * The dB window the loupe's trace is drawn with — the SAME window the
 * receiver's panadapter uses (RX1 range, a standalone RX2 pane's own range,
 * the TX range while keyed), so the noise floor sits at the same height in
 * the loupe as on the display under it. Autoscaling the magnified slice to
 * its own floor..peak exaggerated the noise.
 */
export function loupePanWindow(rxIndex: number, keyed: boolean): { dbMin: number; dbMax: number } {
  const ds = useDisplaySettingsStore.getState();
  if (keyed) return { dbMin: ds.txDbMin, dbMax: ds.txDbMax };
  if (rxIndex === 1) return { dbMin: ds.rx2DbMin, dbMax: ds.rx2DbMax };
  return { dbMin: ds.dbMin, dbMax: ds.dbMax };
}

/**
 * The dB window the loupe's waterfall is coloured with — the SAME window the
 * receiver's own waterfall uses (operator's WF range, per-RX override or
 * floor normalisation; the TX window while keyed). The colormaps keep their
 * lowest ~40% black and their top end white, so stretching the palette over
 * the magnified slice's own floor..peak painted the noise black and the
 * signals white — a black-and-white loupe beside a coloured waterfall.
 */
export function loupeWfWindow(rxIndex: number, keyed: boolean): { dbMin: number; dbMax: number } {
  if (keyed) {
    const ds = useDisplaySettingsStore.getState();
    return { dbMin: ds.wfTxDbMin, dbMax: ds.wfTxDbMax };
  }
  const w = effectiveRxWfWindow(rxIndex);
  return { dbMin: w.wfDbMin, dbMax: w.wfDbMax };
}

/** LUT index for a dB value in a window — the waterfall shader's own mapping. */
export function wfLutIndex(db: number, dbMin: number, dbMax: number): number {
  if (!Number.isFinite(db)) return 0;
  const n = (db - dbMin) / Math.max(0.0001, dbMax - dbMin);
  return Math.min(255, Math.max(0, Math.round(n * 255)));
}

/** "#RRGGBB" → "rgba(r,g,b,a)"; anything else falls back to the default trace amber. */
export function traceRgba(hex: string, alpha: number): string {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim());
  const v = parseInt(m ? m[1]! : 'FFA028', 16);
  return `rgba(${(v >> 16) & 255},${(v >> 8) & 255},${v & 255},${alpha})`;
}

/** 14074010 → "14.074.010" (the VFO's own grouping). */
export function formatLoupeHz(hz: number): string {
  const s = Math.max(0, Math.round(hz)).toString().padStart(7, '0');
  return `${s.slice(0, -6)}.${s.slice(-6, -3)}.${s.slice(-3)}`;
}

/** What the gesture hands the overlay every frame. */
export type LoupeAim = {
  clientX: number;
  clientY: number;
  aimHz: number;      // frequency under the finger
  commitHz: number;   // what a release would tune to
  viewSpanHz: number; // span of the surface being touched
  cancel: boolean;    // finger is in the cancel zone
};

type Palette = { accent: string; warn: string; mono: string };

function readPalette(): Palette {
  let accent = '#4aa3df';
  let mono = 'ui-monospace, monospace';
  try {
    const cs = getComputedStyle(document.body);
    const a = cs.getPropertyValue('--accent').trim();
    if (a) accent = a;
    const m = cs.getPropertyValue('--font-mono').trim();
    if (m) mono = `${m}, ui-monospace, monospace`;
  } catch {
    /* no DOM style (tests) */
  }
  return { accent, warn: '#e0a46a', mono };
}

export class LoupeOverlay {
  private canvas: HTMLCanvasElement;
  private ctx: CanvasRenderingContext2D | null;
  private raf = 0;
  private source: (() => LoupeAim | null) | null = null;
  private image: ImageData | null = null;
  private scratch: HTMLCanvasElement | null = null;
  private palette: Palette = readPalette();

  constructor(
    private readonly receiver: SpectrumReceiver,
    private readonly tuneReceiver: SpectrumReceiver,
  ) {
    const c = document.createElement('canvas');
    c.setAttribute('aria-hidden', 'true');
    c.dataset.testid = 'tuning-loupe';
    Object.assign(c.style, {
      position: 'fixed',
      left: '0',
      top: '0',
      width: '100vw',
      height: '100vh',
      pointerEvents: 'none',
      zIndex: '1200',
      display: 'none',
    });
    document.body.appendChild(c);
    this.canvas = c;
    this.ctx = c.getContext('2d');
  }

  get isOpen(): boolean {
    return this.source !== null;
  }

  open(source: () => LoupeAim | null): void {
    this.source = source;
    this.palette = readPalette();
    const dpr = window.devicePixelRatio || 1;
    this.canvas.width = Math.round(window.innerWidth * dpr);
    this.canvas.height = Math.round(window.innerHeight * dpr);
    this.canvas.style.display = '';
    if (this.raf === 0) this.raf = requestAnimationFrame(this.tick);
  }

  close(): void {
    this.source = null;
    if (this.raf !== 0) cancelAnimationFrame(this.raf);
    this.raf = 0;
    this.ctx?.clearRect(0, 0, this.canvas.width, this.canvas.height);
    this.canvas.style.display = 'none';
  }

  destroy(): void {
    this.close();
    this.canvas.remove();
  }

  private tick = () => {
    this.raf = 0;
    if (!this.source) return;
    const aim = this.source();
    if (aim) this.draw(aim);
    this.raf = requestAnimationFrame(this.tick);
  };

  private draw(aim: LoupeAim): void {
    const ctx = this.ctx;
    if (!ctx) return;
    const dpr = window.devicePixelRatio || 1;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    ctx.scale(dpr, dpr);

    const { accent, warn } = this.palette;
    const place = placeLoupe(aim.clientX, aim.clientY, window.innerWidth, window.innerHeight);
    const w = LOUPE_WIDTH_PX;
    const h = LOUPE_HEIGHT_PX;
    const specH = Math.round(h * SPECTRUM_FRACTION);
    const wfH = h - specH;
    const spanHz = aim.viewSpanHz / LOUPE_MAGNIFICATION;
    const loHz = aim.aimHz - spanHz / 2;
    const xOf = (hz: number) => place.left + ((hz - loHz) / spanHz) * w;

    // Leader from the fingertip to the box, then the box itself.
    ctx.strokeStyle = aim.cancel ? warn : accent;
    ctx.globalAlpha = 0.55;
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(aim.clientX, aim.clientY);
    ctx.lineTo(aim.clientX, place.below ? place.top : place.top + h);
    ctx.stroke();
    ctx.globalAlpha = 1;
    ctx.fillStyle = aim.cancel ? warn : accent;
    ctx.beginPath();
    ctx.arc(aim.clientX, aim.clientY, 4, 0, Math.PI * 2);
    ctx.fill();

    ctx.save();
    ctx.beginPath();
    roundRectPath(ctx, place.left, place.top, w, h, 10);
    ctx.clip();
    ctx.fillStyle = '#070d15';
    ctx.fillRect(place.left, place.top, w, h);

    // Spectrum trace from the live frame.
    const slice = selectDisplaySlice(useDisplayStore.getState(), this.receiver);
    const trace = new Float32Array(w);
    if (slice.panDb && slice.hzPerPixel > 0) {
      const c = Number(slice.centerHz);
      for (let x = 0; x < w; x++) {
        trace[x] = sampleRowDb(slice.panDb, c, slice.hzPerPixel, loHz + ((x + 0.5) / w) * spanHz);
      }
    } else {
      trace.fill(Number.NaN);
    }
    const txState = useTxStore.getState();
    const keyed = txState.moxOn || txState.tunOn;
    const rxIdx = rxIndexOf(this.receiver);
    const pan = loupePanWindow(rxIdx, keyed);
    const panSpan = Math.max(0.0001, pan.dbMax - pan.dbMin);
    const yOf = (db: number) => {
      const n = Math.min(1, Math.max(0, (db - pan.dbMin) / panSpan));
      return place.top + specH - 1 - n * (specH - 3);
    };
    // The trace in the operator's panadapter colour, filled underneath like
    // the panadapter itself.
    const traceColor = useDisplaySettingsStore.getState().rxTraceColor;
    const base = place.top + specH;
    ctx.beginPath();
    let started = false;
    let firstX = 0;
    let lastX = 0;
    for (let x = 0; x < w; x++) {
      const v = trace[x]!;
      if (!Number.isFinite(v)) continue;
      const y = Math.max(place.top + 2, yOf(v));
      if (started) ctx.lineTo(place.left + x, y);
      else {
        ctx.moveTo(place.left + x, y);
        started = true;
        firstX = x;
      }
      lastX = x;
    }
    if (started) {
      ctx.strokeStyle = traceRgba(traceColor, 1);
      ctx.lineWidth = 1.25;
      ctx.stroke();
      ctx.lineTo(place.left + lastX, base);
      ctx.lineTo(place.left + firstX, base);
      ctx.closePath();
      ctx.fillStyle = traceRgba(traceColor, 0.22);
      ctx.fill();
    }

    // Waterfall rows underneath, newest at the top.
    const win = loupeWfWindow(rxIdx, keyed);
    this.drawWaterfall(ctx, loupeRows(this.receiver), place.left, place.top + specH, w, wfH, loHz, spanHz, win.dbMin, win.dbMax, dpr);

    // Where the passband sits now (faint) and where it will land (accent).
    const conn = useConnectionStore.getState();
    const mode = getReceiverMode(conn, this.tuneReceiver);
    const cwOffset = mode === 'CWU' ? -conn.cwPitchHz : mode === 'CWL' ? conn.cwPitchHz : 0;
    const vfoHz = getReceiverVfoHz(conn, this.tuneReceiver);
    const edgesNow = displayFilterEdgesHz(
      mode,
      vfoHz,
      getReceiverFilterLowHz(conn, this.tuneReceiver),
      getReceiverFilterHighHz(conn, this.tuneReceiver),
    );
    const nowL = xOf(vfoHz + cwOffset + edgesNow.lowHz);
    const nowR = xOf(vfoHz + cwOffset + edgesNow.highHz);
    ctx.fillStyle = 'rgba(255,255,255,0.07)';
    ctx.fillRect(nowL, place.top, nowR - nowL, h);
    const edgesThere = displayFilterEdgesHz(
      mode,
      aim.commitHz,
      getReceiverFilterLowHz(conn, this.tuneReceiver),
      getReceiverFilterHighHz(conn, this.tuneReceiver),
    );
    const tgtL = xOf(aim.commitHz + cwOffset + edgesThere.lowHz);
    const tgtR = xOf(aim.commitHz + cwOffset + edgesThere.highHz);
    ctx.fillStyle = aim.cancel ? 'rgba(224,164,106,0.10)' : 'rgba(74,163,223,0.20)';
    ctx.fillRect(tgtL, place.top, tgtR - tgtL, h);

    // Aim line at the committed dial.
    const ax = xOf(aim.commitHz);
    ctx.strokeStyle = '#ffffff';
    ctx.lineWidth = 1.5;
    ctx.setLineDash([5, 4]);
    ctx.beginPath();
    ctx.moveTo(ax, place.top);
    ctx.lineTo(ax, place.top + h);
    ctx.stroke();
    ctx.setLineDash([]);

    // Readout pill.
    const label = aim.cancel ? 'Release to cancel' : `${formatLoupeHz(aim.commitHz)}  ${LOUPE_MAGNIFICATION}×`;
    ctx.font = `500 14px ${this.palette.mono}`;
    const tw = ctx.measureText(label).width + 18;
    const tx = Math.min(Math.max(place.left + 6, ax - tw / 2), place.left + w - tw - 6);
    const ty = place.top + h - 30;
    ctx.fillStyle = 'rgba(7,13,21,0.88)';
    ctx.beginPath();
    roundRectPath(ctx, tx, ty, tw, 24, 6);
    ctx.fill();
    ctx.fillStyle = aim.cancel ? warn : '#eef6fc';
    ctx.textBaseline = 'middle';
    ctx.fillText(label, tx + 9, ty + 12.5);
    ctx.restore();

    ctx.strokeStyle = aim.cancel ? warn : accent;
    ctx.lineWidth = 2;
    ctx.beginPath();
    roundRectPath(ctx, place.left, place.top, w, h, 10);
    ctx.stroke();
  }

  private drawWaterfall(
    ctx: CanvasRenderingContext2D,
    rows: readonly LoupeRow[],
    left: number,
    top: number,
    w: number,
    h: number,
    loHz: number,
    spanHz: number,
    dbMin: number,
    dbMax: number,
    dpr: number,
  ): void {
    if (rows.length === 0) return;
    const pw = Math.max(1, Math.round(w * dpr));
    const ph = Math.max(1, Math.round(h * dpr));
    if (!this.image || this.image.width !== pw || this.image.height !== ph) {
      this.image = ctx.createImageData(pw, ph);
    }
    const data = this.image.data;
    const lut = lutFor(useDisplaySettingsStore.getState().colormap);
    const shown = Math.min(rows.length, 48);
    const line = new Uint8Array(pw * 4);
    let lastRow = -1;
    for (let y = 0; y < ph; y++) {
      const r = Math.min(shown - 1, Math.floor((y / ph) * shown));
      if (r !== lastRow) {
        lastRow = r;
        const row = rows[r]!;
        for (let x = 0; x < pw; x++) {
          const db = sampleRowDb(row.bins, row.centerHz, row.hzPerPixel, loHz + ((x + 0.5) / pw) * spanHz);
          const t = wfLutIndex(db, dbMin, dbMax);
          line[x * 4] = lut[t * 4]!;
          line[x * 4 + 1] = lut[t * 4 + 1]!;
          line[x * 4 + 2] = lut[t * 4 + 2]!;
          line[x * 4 + 3] = 255;
        }
      }
      data.set(line, y * pw * 4);
    }
    // Through a scratch canvas so the rounded clip still applies.
    if (!this.scratch) this.scratch = document.createElement('canvas');
    if (this.scratch.width !== pw || this.scratch.height !== ph) {
      this.scratch.width = pw;
      this.scratch.height = ph;
    }
    this.scratch.getContext('2d')?.putImageData(this.image, 0, 0);
    ctx.drawImage(this.scratch, left, top, w, h);
  }
}
