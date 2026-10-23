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
// The filter card's audio views, drawn from the radio's AF analysis frames
// (af-analysis.ts) on animation frames — no React render per frame. Colours
// come from the display tokens, so blue / black / paper displays all work.

import { useEffect, useRef } from 'react';
import { binDb, peakFrequencyHz, useAfAnalysisStore, type AfFrame } from './af-analysis';
import type { FilterView } from './filter-view-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { useRxMetersStore } from '../state/rx-meters-store';
import { useTxStore } from '../state/tx-store';
import { lutFor } from '../gl/colormap';
import { displayColormap } from '../theme/paper-display';

export type AfViewProps = {
  view: Exclude<FilterView, 'pass'>;
  source: number;
  rx: number;
  tx: boolean;
  /** Receiver filter edges (Hz, signed as the radio reports them). */
  filterLowHz: number;
  filterHighHz: number;
  cwPitchHz: number;
  /** The receiver's mode — lower-sideband modes draw the audio axis mirrored. */
  mode?: string;
  /**
   * Draw the spectrum referred to the receiver INPUT — the audio bins minus the
   * receiver's live AGC gain — so the noise floor stays put instead of rising
   * and falling 15-30 dB as the AGC works (it raises band noise between overs
   * and drops it under a signal). Off for TX and FreeDV decoded speech.
   */
  agcReferred?: boolean;
};

const STALE_MS = 1500;
// Spectrum / audio waterfall vertical range is anchored to the NOISE FLOOR
// (field report: ranging from the strongest bin drew a signal-free passband's
// noise at the top). The floor — a low percentile of the bins inside the
// passband, slowly tracked — sits FLOOR_MARGIN_DB above the bottom; the top is
// a fixed FIXED_SPAN_DB above the bottom (a strong signal no longer rescales it).
const FLOOR_PERCENTILE = 0.2;
const FLOOR_MARGIN_DB = 8;
// Floor tracker: follows a FALL quickly, a RISE only slowly (time constant),
// so a voice filling the passband does not lift the floor estimate.
const FLOOR_FALL_ALPHA = 0.3;
const FLOOR_RISE_TC_S = 5;
const FIXED_SPAN_DB = 70;
const TOP_DECAY_DB_PER_S = 3;

/**
 * The noise floor of a spectrum: the given percentile of the bins between loHz
 * and hiHz (dB). Pure; exported for tests.
 */
export function noiseFloorDb(binsDb: ArrayLike<number>, hzPerBin: number, loHz: number, hiHz: number, pct = FLOOR_PERCENTILE): number {
  const lo = Math.max(0, Math.floor(loHz / hzPerBin));
  const hi = Math.min(binsDb.length - 1, Math.ceil(hiHz / hzPerBin));
  const vals: number[] = [];
  for (let i = lo; i <= hi; i++) vals.push(binsDb[i]!);
  if (vals.length === 0) return -120;
  vals.sort((a, b) => a - b);
  return vals[Math.min(vals.length - 1, Math.floor(vals.length * pct))]!;
}

/** The display's dB window [bottom, top] for a floor and strongest bin. Pure; exported for tests. */
export function displayRangeDb(floorDb: number, strongestDb: number, prevTopDb: number, decayDb: number): [number, number] {
  // A FIXED span above the floor. The top used to widen to clear the strongest
  // bin, which squeezed the whole scale whenever a station appeared — the same
  // noise then drew lower and darker, and crept back as the top decayed (field
  // report: the floor "comes down with signals"). Stronger signals simply
  // reach the top. (strongestDb / prevTopDb / decayDb kept for the call site.)
  void strongestDb;
  void prevTopDb;
  void decayDb;
  const bottom = floorDb - FLOOR_MARGIN_DB;
  return [bottom, bottom + FIXED_SPAN_DB];
}

/** Asymmetric floor tracking: fast down, slow up. Pure; exported for tests. */
export function trackFloorDb(prev: number, now: number, dtS: number): number {
  if (Number.isNaN(prev)) return now;
  if (now < prev) return prev + FLOOR_FALL_ALPHA * (now - prev);
  const k = 1 - Math.exp(-Math.max(0, dtS) / FLOOR_RISE_TC_S);
  return prev + k * (now - prev);
}
// Higher-resolution bins are noisier frame to frame: exponential average in dB.
const SMOOTH_ALPHA = 0.35;
// Scope auto-gain: the trace's peak lands at SCOPE_FILL of the half-height;
// the reference peak decays at SCOPE_DECAY_DB_PER_S so the gain recovers
// after a loud burst, and never exceeds SCOPE_MAX_GAIN_DB on silence.
const SCOPE_FILL = 0.85;
const SCOPE_DECAY_DB_PER_S = 6;
const SCOPE_MAX_GAIN_DB = 60;

/** Exponential average of a frame's bins (dB) into `prev` (new array when sizes differ). */
export function smoothBins(
  prev: Float32Array | null,
  bins: Uint8Array,
  alpha = SMOOTH_ALPHA,
  offsetDb = 0,
): Float32Array {
  const out = prev && prev.length === bins.length ? prev : null;
  const res = out ?? new Float32Array(bins.length);
  for (let i = 0; i < bins.length; i++) {
    const db = binDb(bins[i]!) - offsetDb;
    res[i] = out ? res[i]! + alpha * (db - res[i]!) : db;
  }
  return res;
}

/**
 * The gain the AGC is APPLYING to the audio (dB, positive = boosting) from
 * WDSP's RXA_AGC_GAIN meter. That meter is 20·log10(volts / out_target)
 * (wcpAGC.c: a->gain = a->volts * a->inv_out_target) while the audio is
 * multiplied by out_target / volts — so the applied gain is the meter's
 * NEGATIVE: quiet band noise boosted +80 dB reads −80 on the meter. (The
 * first input-referred build used the meter as-is and doubled the AGC's
 * breathing instead of removing it.) Pure; exported for tests.
 */
export function appliedAgcGainDb(meterDb: number): number {
  // −400-ish = "meter has not run": treat as no compensation.
  if (!Number.isFinite(meterDb) || meterDb < -300) return 0;
  return -meterDb;
}

/** The receiver's applied AGC gain right now (dB), 0 when unknown. */
function agcGainDbFor(rx: number): number {
  const m = useRxMetersStore.getState();
  const meters = rx === 0 ? m : m.byReceiver[rx];
  const g = meters?.agcGain;
  return typeof g === 'number' ? appliedAgcGainDb(g) : 0;
}

/** Scope display gain (linear) for a held peak (full-scale units). */
export function scopeGain(holdPeak: number): number {
  const minPeak = Math.pow(10, -SCOPE_MAX_GAIN_DB / 20) * SCOPE_FILL;
  return SCOPE_FILL / Math.max(holdPeak, minPeak);
}

/** Audio passband edges in 0..4 kHz from a signed filter (LSB filters are negative). */
export function audioEdges(lowHz: number, highHz: number): [number, number] {
  const a = Math.abs(lowHz);
  const b = Math.abs(highHz);
  return [Math.max(0, Math.min(a, b)), Math.min(4000, Math.max(a, b))];
}

const LOWER_SIDEBAND = new Set(['LSB', 'CWL', 'DIGL']);

/**
 * The audio axis, laid out to sit beside the RF passband it came from:
 * - span: the filter's audio width plus a small margin (not a fixed 0..4 kHz,
 *   which left a 2.7 kHz filter in two thirds of the display);
 * - mirror: in lower-sideband modes audio pitch runs OPPOSITE to RF (a station
 *   near the dial is a low tone), so 0 Hz is drawn at the RIGHT — each trace
 *   then lines up with its station in the panadapter's passband.
 * TX audio keeps a plain 0..4 kHz. Pure; exported for tests.
 */
export function audioAxis(
  mode: string | undefined,
  filterLowHz: number,
  filterHighHz: number,
  tx: boolean,
): { spanHz: number; mirror: boolean } {
  if (tx) return { spanHz: 4000, mirror: false };
  const [, hi] = audioEdges(filterLowHz, filterHighHz);
  const span = Math.round(hi + Math.max(250, hi * 0.08));
  return {
    spanHz: Math.max(1000, Math.min(4000, span)),
    mirror: LOWER_SIDEBAND.has((mode ?? '').toUpperCase()),
  };
}

/** x (canvas px) of an audio frequency on that axis. */
export function audioX(hz: number, width: number, spanHz: number, mirror: boolean): number {
  const t = hz / spanHz;
  return (mirror ? 1 - t : t) * width;
}

/** Rising zero crossing in the first half of the scope buffer (0 when none). */
export function scopeTrigger(scope: Int8Array): number {
  const half = scope.length >> 1;
  for (let i = 1; i < half; i++) if (scope[i - 1]! < 0 && scope[i]! >= 0) return i;
  return 0;
}

function cssVar(name: string, fallback: string): string {
  const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return v || fallback;
}

export function AfView(props: AfViewProps) {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const propsRef = useRef(props);
  propsRef.current = props;

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    let raf = 0;
    let lastAt = -1;
    let lastView = '';
    const wf = document.createElement('canvas');
    let colours = { bg: '#0b1420', grid: 'rgba(255,255,255,0.08)', ink: '#9aa6b3', ink0: '#edeef1' };
    let coloursAt = 0;
    let smooth: Float32Array | null = null;
    let smoothAt = -1;
    let topDb = -30;
    let floorDb = NaN;
    let hold = 0.01;
    let lastNow = 0;

    const draw = (now: number) => {
      raf = requestAnimationFrame(draw);
      const p = propsRef.current;
      const dpr = window.devicePixelRatio || 1;
      const r = canvas.getBoundingClientRect();
      const W = Math.max(1, Math.round(r.width * dpr));
      const H = Math.max(1, Math.round(r.height * dpr));
      if (canvas.width !== W || canvas.height !== H) {
        canvas.width = W;
        canvas.height = H;
      }
      // Old waterfall rows are drawn on the old axis: clear them when the view,
      // the span or the mirroring changes (mode or filter change).
      const axisNow = audioAxis(p.mode, p.filterLowHz, p.filterHighHz, p.tx);
      const viewKey = `${p.view}|${axisNow.spanHz}|${axisNow.mirror ? 'm' : 'n'}`;
      if (wf.width !== W || wf.height !== H || lastView !== viewKey) {
        wf.width = W;
        wf.height = H;
        lastView = viewKey;
        const wctx = wf.getContext('2d');
        if (wctx) {
          wctx.fillStyle = colours.bg;
          wctx.fillRect(0, 0, W, H);
        }
      }
      if (now - coloursAt > 1000) {
        coloursAt = now;
        colours = {
          bg: cssVar('--spec-bg', '#0b1420'),
          grid: cssVar('--spec-grid', 'rgba(255,255,255,0.08)'),
          ink: cssVar('--disp-ink-2', '#9aa6b3'),
          ink0: cssVar('--disp-ink-0', '#edeef1'),
        };
      }
      const traceColour = p.tx ? '#ff7a6b' : useDisplaySettingsStore.getState().rxTraceColor || '#FFA028';
      ctx.fillStyle = colours.bg;
      ctx.fillRect(0, 0, W, H);
      const f: AfFrame | undefined = useAfAnalysisStore.getState().frames[p.source];
      const fs = 11 * dpr;
      ctx.font = `${fs}px ui-monospace, monospace`;

      if (!f || now - f.at > STALE_MS) {
        ctx.fillStyle = colours.ink;
        ctx.fillText('Waiting for audio…', 10 * dpr, H / 2);
        return;
      }
      const dt = lastNow ? Math.min(0.25, (now - lastNow) / 1000) : 0;
      lastNow = now;
      // Input-referred: subtract the live AGC gain so the floor holds still.
      const agcOffset = p.agcReferred && !p.tx ? agcGainDbFor(p.rx) : 0;
      if (f.at !== smoothAt) {
        smoothAt = f.at;
        smooth = smoothBins(smooth, f.bins, undefined, agcOffset);
      }
      const sm = smooth!;
      let strongest = -120;
      for (let i = 0; i < sm.length; i++) if (sm[i]! > strongest) strongest = sm[i]!;
      // Floor from the receive passband (the whole voice band on TX).
      const [edgeLo, edgeHi] = p.tx ? [150, 3500] : audioEdges(p.filterLowHz, p.filterHighHz);
      const hzPerBin = f.rateHz / 2 / sm.length;
      const floorNow = noiseFloorDb(sm, hzPerBin, edgeLo, edgeHi);
      floorDb = trackFloorDb(floorDb, floorNow, dt);
      const [dbLo, dbTop] = displayRangeDb(floorDb, strongest, topDb, TOP_DECAY_DB_PER_S * dt);
      topDb = dbTop;
      const RANGE_DB = Math.max(1, dbTop - dbLo);
      const axis = audioAxis(p.mode, p.filterLowHz, p.filterHighHz, p.tx);
      const fx = (hz: number) => audioX(hz, W, axis.spanHz, axis.mirror);
      const visibleBins = Math.min(f.bins.length, Math.ceil(axis.spanHz / hzPerBin) + 1);
      const yOf = (db: number) => H - ((db - dbLo) / RANGE_DB) * H;
      const tickHz = axis.spanHz > 2000 ? 1000 : 500;
      const tickLabel = (hz: number) => (hz % 1000 === 0 ? `${hz / 1000}k` : `${(hz / 1000).toFixed(1)}k`);
      const grid = () => {
        ctx.strokeStyle = colours.grid;
        ctx.lineWidth = 1;
        for (let hz = tickHz; hz < axis.spanHz; hz += tickHz) {
          const x = fx(hz);
          ctx.beginPath();
          ctx.moveTo(x, 0);
          ctx.lineTo(x, H);
          ctx.stroke();
          ctx.fillStyle = colours.ink;
          ctx.fillText(tickLabel(hz), x + 3 * dpr, H - 4 * dpr);
        }
      };
      const passband = () => {
        if (p.tx) return;
        const [lo, hi] = audioEdges(p.filterLowHz, p.filterHighHz);
        ctx.fillStyle = 'rgba(74,163,223,0.10)';
        ctx.fillRect(Math.min(fx(lo), fx(hi)), 0, Math.abs(fx(hi) - fx(lo)), H);
        ctx.strokeStyle = 'rgba(74,163,223,0.8)';
        ctx.setLineDash([4 * dpr, 3 * dpr]);
        for (const e of [lo, hi]) {
          ctx.beginPath();
          ctx.moveTo(fx(e), 0);
          ctx.lineTo(fx(e), H);
          ctx.stroke();
        }
        ctx.setLineDash([]);
      };

      if (p.view === 'aspec') {
        grid();
        passband();
        const n = visibleBins;
        const x0 = fx(0);
        let xLast = x0;
        ctx.beginPath();
        for (let i = 0; i < n; i++) {
          const x = fx(i * hzPerBin);
          const y = yOf(sm[i]!);
          if (i === 0) ctx.moveTo(x, y);
          else ctx.lineTo(x, y);
          xLast = x;
        }
        ctx.strokeStyle = traceColour;
        ctx.lineWidth = 1.3 * dpr;
        ctx.stroke();
        ctx.lineTo(xLast, H);
        ctx.lineTo(x0, H);
        ctx.closePath();
        ctx.globalAlpha = 0.16;
        ctx.fillStyle = traceColour;
        ctx.fill();
        ctx.globalAlpha = 1;
      } else if (p.view === 'scope') {
        ctx.strokeStyle = colours.grid;
        for (let k = 1; k < 4; k++) {
          const y = (k / 4) * H;
          ctx.beginPath();
          ctx.moveTo(0, y);
          ctx.lineTo(W, y);
          ctx.stroke();
        }
        // Auto-gain: hold the recent peak (true full-scale units) with a slow
        // decay, and scale so it fills SCOPE_FILL of the half-height.
        const peakLin = Math.pow(10, f.peakDbfs / 20);
        hold = Math.max(peakLin, hold * Math.pow(10, (-SCOPE_DECAY_DB_PER_S * dt) / 20));
        const gain = scopeGain(hold);
        const half = H / 2;
        // Clip lines mark TRUE full scale (±1.0) — on screen only when the
        // audio is loud enough for them to matter.
        const clipY = gain * half;
        if (clipY < half * 1.02) {
          ctx.strokeStyle = 'rgba(224,80,80,0.6)';
          ctx.setLineDash([4 * dpr, 4 * dpr]);
          for (const y of [half - clipY, half + clipY]) {
            ctx.beginPath();
            ctx.moveTo(0, y);
            ctx.lineTo(W, y);
            ctx.stroke();
          }
          ctx.setLineDash([]);
        }
        const t0 = scopeTrigger(f.scope);
        const span = Math.min(256, f.scope.length - t0);
        const unit = f.scopeScale / 127;
        ctx.beginPath();
        for (let i = 0; i < span; i++) {
          const x = (i / (span - 1)) * W;
          const v = f.scope[t0 + i]! * unit * gain;
          const y = half - Math.max(-1.05, Math.min(1.05, v)) * half;
          if (i === 0) ctx.moveTo(x, y);
          else ctx.lineTo(x, y);
        }
        ctx.strokeStyle = p.tx ? '#ff7a6b' : '#7fe0a6';
        ctx.lineWidth = 1.4 * dpr;
        ctx.stroke();
        ctx.fillStyle = colours.ink;
        const gainDb = Math.round(20 * Math.log10(gain));
        ctx.fillText(
          `${Math.round((span / f.rateHz) * 1000)} ms · gain ${gainDb >= 0 ? '+' : ''}${gainDb} dB · peak ${f.peakDbfs} dBFS`,
          6 * dpr,
          13 * dpr,
        );
      } else if (p.view === 'awf') {
        const wctx = wf.getContext('2d');
        if (wctx && f.at !== lastAt) {
          lastAt = f.at;
          const step = Math.max(1, Math.round(2 * dpr));
          wctx.drawImage(wf, 0, 0, W, H - step, 0, step, W, H - step);
          const lut = lutFor(displayColormap(useDisplaySettingsStore.getState().colormap));
          const n = visibleBins;
          const binW = (W * hzPerBin) / axis.spanHz + 1;
          for (let i = 0; i < n; i++) {
            const t = Math.max(
              0,
              Math.min(255, Math.round(((binDb(f.bins[i]!) - agcOffset - dbLo) / RANGE_DB) * 255)),
            );
            wctx.fillStyle = `rgb(${lut[t * 4]},${lut[t * 4 + 1]},${lut[t * 4 + 2]})`;
            // Left edge of the bin on screen (its high-frequency side when mirrored).
            const xa = fx(i * hzPerBin);
            const xb = fx((i + 1) * hzPerBin);
            wctx.fillRect(Math.min(xa, xb), 0, binW, step);
          }
        }
        ctx.drawImage(wf, 0, 0);
        passband();
        ctx.fillStyle = colours.ink0;
        for (let hz = tickHz; hz < axis.spanHz; hz += tickHz) ctx.fillText(tickLabel(hz), fx(hz) + 3 * dpr, H - 4 * dpr);
      } else if (p.view === 'cw') {
        const pitch = p.cwPitchHz;
        const span = 200;
        const cx = W / 2;
        const scaleX = (d: number) => cx + (d / span) * W * 0.42;
        ctx.fillStyle = 'rgba(255,255,255,0.05)';
        ctx.fillRect(W * 0.08, H * 0.42, W * 0.84, H * 0.16);
        ctx.fillStyle = colours.ink;
        for (let d = -span; d <= span; d += 50) {
          ctx.fillRect(scaleX(d), H * 0.38, 1, H * 0.24);
          ctx.fillText(`${d > 0 ? '+' : ''}${d}`, scaleX(d) - 10 * dpr, H * 0.76);
        }
        ctx.strokeStyle = '#4aa3df';
        ctx.lineWidth = 2 * dpr;
        ctx.beginPath();
        ctx.moveTo(cx, H * 0.3);
        ctx.lineTo(cx, H * 0.7);
        ctx.stroke();
        const heard = peakFrequencyHz(f, pitch - 300, pitch + 300);
        ctx.font = `600 ${13 * dpr}px ui-monospace, monospace`;
        if (heard == null) {
          ctx.fillStyle = colours.ink;
          ctx.fillText('No CW tone in the passband', W * 0.08, H * 0.2);
        } else {
          const off = Math.max(-span, Math.min(span, heard - pitch));
          const x = scaleX(off);
          ctx.fillStyle = traceColour;
          ctx.fillRect(Math.min(cx, x), H * 0.44, Math.abs(x - cx), H * 0.12);
          ctx.beginPath();
          ctx.arc(x, H * 0.5, 7 * dpr, 0, Math.PI * 2);
          ctx.fill();
          ctx.fillStyle = colours.ink0;
          const hint = Math.abs(off) < 10 ? 'zero-beat' : off > 0 ? 'tune down' : 'tune up';
          ctx.fillText(`${off > 0 ? '+' : ''}${Math.round(off)} Hz — ${hint}`, W * 0.08, H * 0.2);
        }
        ctx.font = `${fs}px ui-monospace, monospace`;
        ctx.fillStyle = colours.ink;
        ctx.fillText(`sidetone ${pitch} Hz`, W * 0.62, H * 0.92);
      } else if (p.view === 'lvl') {
        const bar = (row: number, label: string, frac: number, colour: string, text: string) => {
          const y = H * (0.1 + row * 0.3);
          const h = H * 0.16;
          ctx.fillStyle = 'rgba(255,255,255,0.06)';
          ctx.fillRect(W * 0.2, y, W * 0.58, h);
          ctx.fillStyle = colour;
          ctx.fillRect(W * 0.2, y, W * 0.58 * Math.max(0, Math.min(1, frac)), h);
          ctx.fillStyle = colours.ink;
          ctx.fillText(label, W * 0.03, y + h * 0.72);
          ctx.fillStyle = colours.ink0;
          ctx.fillText(text, W * 0.8, y + h * 0.72);
        };
        const lvlFrac = (db: number) => (db + 60) / 60;
        bar(0, p.tx ? 'MIC' : 'AF', lvlFrac(f.rmsDbfs), p.tx ? '#ff7a6b' : '#7fe0a6', `${f.rmsDbfs} dBFS`);
        ctx.fillStyle = colours.ink0;
        ctx.fillRect(W * 0.2 + W * 0.58 * Math.max(0, Math.min(1, lvlFrac(f.peakDbfs))), H * 0.1, 2 * dpr, H * 0.16);
        if (p.tx) {
          const tx = useTxStore.getState();
          bar(1, 'ALC', tx.alcGr / 20, '#4aa3df', `−${tx.alcGr.toFixed(0)} dB`);
          bar(2, 'COMP', (tx.compAv + 30) / 30, '#e0a46a', `${tx.compAv.toFixed(0)} dB`);
        } else {
          const m = useRxMetersStore.getState();
          const meters = p.rx === 0 ? m : m.byReceiver[p.rx];
          const sig = meters?.signalAv ?? -140;
          const agc = meters?.agcGain ?? 0;
          bar(1, 'SIGNAL', (sig + 130) / 80, '#4aa3df', `${sig.toFixed(0)} dBm`);
          bar(2, 'AGC', (agc + 20) / 80, '#e0a46a', `${agc >= 0 ? '+' : ''}${agc.toFixed(0)} dB`);
        }
      }
      if (f.muted && !p.tx) {
        ctx.fillStyle = colours.ink0;
        ctx.fillText('MUTED', W - 52 * dpr, 13 * dpr);
      }
    };
    raf = requestAnimationFrame(draw);
    return () => cancelAnimationFrame(raf);
  }, []);

  return <canvas ref={canvasRef} className="fv-canvas" data-testid={`af-view-${props.view}`} />;
}
