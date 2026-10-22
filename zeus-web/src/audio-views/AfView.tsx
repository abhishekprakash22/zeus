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
};

const STALE_MS = 1500;
const DB_LO = -100;
const DB_HI = -10;

/** Audio passband edges in 0..4 kHz from a signed filter (LSB filters are negative). */
export function audioEdges(lowHz: number, highHz: number): [number, number] {
  const a = Math.abs(lowHz);
  const b = Math.abs(highHz);
  return [Math.max(0, Math.min(a, b)), Math.min(4000, Math.max(a, b))];
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
      if (wf.width !== W || wf.height !== H || lastView !== p.view) {
        wf.width = W;
        wf.height = H;
        lastView = p.view;
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
      const fx = (hz: number) => (hz / (f.rateHz / 2)) * W;
      const yOf = (db: number) => H - ((db - DB_LO) / (DB_HI - DB_LO)) * H;
      const grid = () => {
        ctx.strokeStyle = colours.grid;
        ctx.lineWidth = 1;
        for (let k = 1; k < 4; k++) {
          const x = fx(k * 1000);
          ctx.beginPath();
          ctx.moveTo(x, 0);
          ctx.lineTo(x, H);
          ctx.stroke();
          ctx.fillStyle = colours.ink;
          ctx.fillText(`${k}k`, x + 3 * dpr, H - 4 * dpr);
        }
      };
      const passband = () => {
        if (p.tx) return;
        const [lo, hi] = audioEdges(p.filterLowHz, p.filterHighHz);
        ctx.fillStyle = 'rgba(74,163,223,0.10)';
        ctx.fillRect(fx(lo), 0, fx(hi) - fx(lo), H);
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
        const n = f.bins.length;
        ctx.beginPath();
        for (let i = 0; i < n; i++) {
          const x = (i / n) * W;
          const y = yOf(binDb(f.bins[i]!));
          if (i === 0) ctx.moveTo(x, y);
          else ctx.lineTo(x, y);
        }
        ctx.strokeStyle = traceColour;
        ctx.lineWidth = 1.3 * dpr;
        ctx.stroke();
        ctx.lineTo(W, H);
        ctx.lineTo(0, H);
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
        ctx.strokeStyle = 'rgba(224,80,80,0.6)';
        ctx.setLineDash([4 * dpr, 4 * dpr]);
        for (const fr of [0.07, 0.93]) {
          ctx.beginPath();
          ctx.moveTo(0, fr * H);
          ctx.lineTo(W, fr * H);
          ctx.stroke();
        }
        ctx.setLineDash([]);
        const t0 = scopeTrigger(f.scope);
        const span = Math.min(256, f.scope.length - t0);
        ctx.beginPath();
        for (let i = 0; i < span; i++) {
          const x = (i / (span - 1)) * W;
          const y = H / 2 - (f.scope[t0 + i]! / 127) * H * 0.43;
          if (i === 0) ctx.moveTo(x, y);
          else ctx.lineTo(x, y);
        }
        ctx.strokeStyle = p.tx ? '#ff7a6b' : '#7fe0a6';
        ctx.lineWidth = 1.4 * dpr;
        ctx.stroke();
        ctx.fillStyle = colours.ink;
        ctx.fillText(`${Math.round((span / f.rateHz) * 1000)} ms`, 6 * dpr, 13 * dpr);
      } else if (p.view === 'awf') {
        const wctx = wf.getContext('2d');
        if (wctx && f.at !== lastAt) {
          lastAt = f.at;
          const step = Math.max(1, Math.round(2 * dpr));
          wctx.drawImage(wf, 0, 0, W, H - step, 0, step, W, H - step);
          const lut = lutFor(displayColormap(useDisplaySettingsStore.getState().colormap));
          const n = f.bins.length;
          for (let i = 0; i < n; i++) {
            const t = Math.max(0, Math.min(255, Math.round(((binDb(f.bins[i]!) - DB_LO) / (DB_HI - DB_LO)) * 255)));
            wctx.fillStyle = `rgb(${lut[t * 4]},${lut[t * 4 + 1]},${lut[t * 4 + 2]})`;
            wctx.fillRect((i / n) * W, 0, W / n + 1, step);
          }
        }
        ctx.drawImage(wf, 0, 0);
        passband();
        ctx.fillStyle = colours.ink0;
        for (let k = 1; k < 4; k++) ctx.fillText(`${k}k`, fx(k * 1000) + 3 * dpr, H - 4 * dpr);
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
