// SPDX-License-Identifier: GPL-2.0-or-later
//
// Palette picker: one choice for the waterfall AND the 3D panadapter.
//
// Each tile is a live thumbnail: a canned synthetic band rendered through the
// SAME LUT the GPU samples (lutFor), with the same two mappings the 3D shader
// uses — height from a pan window, colour from a waterfall window — and the
// same 0.6→1.0 vertical gradient inside each curtain. Drawn on a 2D canvas so
// the picker works on every client (no WebGPU needed for a thumbnail), but
// the colours are the shader's colours: same table, same mapping.
//
// Choosing a tile sets the waterfall palette and the surface palette together
// — there is no separate 3D palette, because separate controls are how the
// surface and waterfall came to disagree in the first place.

import { useEffect, useRef, type CSSProperties } from 'react';
import { COLORMAPS, lutFor, type ColormapId } from '../gl/colormap';
import { useDisplaySettingsStore } from '../state/display-settings-store';

// Canned band, deterministic: noise floor at -118 dBm with four signals of
// different strengths and one broad patch of activity. Same for every tile so
// the eye compares palettes, not bands.
const COLS = 96;
const ROWS = 22;
const FLOOR = -121;
const PAN_RANGE = 44;     // height window
const WF_RANGE = 30;      // colour window (a typical waterfall aperture)
let HIST: Float32Array | null = null;
function history(): Float32Array {
  if (HIST) return HIST;
  const h = new Float32Array(ROWS * COLS);
  let seed = 7;
  const rnd = () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff; };
  for (let r = 0; r < ROWS; r++) {
    for (let c = 0; c < COLS; c++) {
      const x = c / (COLS - 1);
      let v = -118 + (rnd() - 0.5) * 3.2;
      const sig: Array<[number, number, number, number]> = [[0.22, 0.03, 34, 0], [0.47, 0.02, 26, 1.3], [0.61, 0.05, 18, 2.1], [0.83, 0.025, 30, 0.7]];
      for (const [cc, w, a, ph] of sig) {
        const amp = a * (0.65 + 0.35 * Math.sin(r * 0.5 + ph));
        v += amp * Math.exp(-(((x - cc) / w) ** 2));
      }
      v += 6 * Math.exp(-(((x - 0.47) / 0.15) ** 2));
      h[r * COLS + c] = v;
    }
  }
  HIST = h;
  return h;
}

function lutRgb(lut: Uint8Array, t: number): [number, number, number] {
  const i = Math.max(0, Math.min(255, Math.round(t * 255))) * 4;
  return [lut[i]!, lut[i + 1]!, lut[i + 2]!];
}

function drawTile(canvas: HTMLCanvasElement, id: ColormapId): void {
  const ctx = canvas.getContext('2d');
  if (!ctx) return;
  const W = canvas.width;
  const H = canvas.height;
  const lut = lutFor(id);
  const h = history();
  ctx.fillStyle = '#0b0e13';
  ctx.fillRect(0, 0, W, H);

  // --- 3D surface, curtain model: back rows first, each column a vertical
  // gradient from 0.6*strength (base) to strength (ridge).
  const surfH = Math.round(H * 0.68);
  const depthSpan = 0.55;
  const ridgeFrac = 0.42;
  const haze = 0.45;
  const bg = [11, 14, 19];
  const colW = W / (COLS - 1);
  for (let r = 0; r < ROWS; r++) {
    const v = 1 - r / (ROWS - 1);              // 1 back .. 0 front
    const base = surfH - depthSpan * surfH * v; // baseline rises with depth
    for (let c = 0; c < COLS - 1; c++) {
      const db = h[r * COLS + c]!;
      const level = Math.pow(Math.max(0, Math.min(1, (db - FLOOR) / PAN_RANGE)), 0.7);
      const strength = Math.max(0, Math.min(1, (db - FLOOR) / WF_RANGE));
      const top = base - level * ridgeFrac * surfH * (1 - 0.45 * v);
      const g = ctx.createLinearGradient(0, base, 0, top);
      const lo = lutRgb(lut, strength * 0.6);
      const hi = lutRgb(lut, strength);
      const mixBg = (rgb: [number, number, number]) =>
        `rgb(${rgb.map((ch, i) => Math.round(ch * (1 - haze * v) + bg[i]! * haze * v)).join(',')})`;
      g.addColorStop(0, mixBg(lo));
      g.addColorStop(1, mixBg(hi));
      ctx.fillStyle = g;
      ctx.fillRect(c * colW, top, colW + 0.5, base - top);
    }
  }
  // --- waterfall strip, same LUT, colour window only.
  const wfTop = surfH + 3;
  const wfH = H - wfTop;
  const img = ctx.createImageData(COLS, ROWS);
  for (let r = 0; r < ROWS; r++) {
    for (let c = 0; c < COLS; c++) {
      const db = h[(ROWS - 1 - r) * COLS + c]!;
      const t = Math.max(0, Math.min(1, (db - FLOOR) / WF_RANGE));
      const [R, G, B] = lutRgb(lut, t);
      const o = (r * COLS + c) * 4;
      img.data[o] = R; img.data[o + 1] = G; img.data[o + 2] = B; img.data[o + 3] = 255;
    }
  }
  // scale the tiny image up without smoothing (waterfall texels are blocks)
  const off = document.createElement('canvas');
  off.width = COLS; off.height = ROWS;
  off.getContext('2d')?.putImageData(img, 0, 0);
  ctx.imageSmoothingEnabled = false;
  ctx.drawImage(off, 0, wfTop, W, wfH);
}

function Tile({ id, label, active, onPick }: { id: ColormapId; label: string; active: boolean; onPick: () => void }) {
  const ref = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    if (ref.current) drawTile(ref.current, id);
  }, [id]);
  return (
    <button
      type="button"
      onClick={onPick}
      aria-pressed={active}
      title={`${label}: waterfall and 3D panadapter`}
      style={{ ...tile, ...(active ? tileActive : null) }}
    >
      <canvas ref={ref} width={160} height={96} style={tileCanvas} />
      <span style={tileLabel}>{label}</span>
    </button>
  );
}

export function PalettePicker({ compact = false }: { compact?: boolean }) {
  const colormap = useDisplaySettingsStore((s) => s.colormap);
  const setColormap = useDisplaySettingsStore((s) => s.setColormap);
  const ridge = useDisplaySettingsStore((s) => s.pan3dRidgeLines);
  const setRidge = useDisplaySettingsStore((s) => s.setPan3dRidgeLines);
  return (
    <div style={{ ...wrap, ...(compact ? { gap: 6 } : null) }}>
      <span style={label}>PALETTE</span>
      <div style={tiles}>
        {COLORMAPS.map((c) => (
          <Tile key={c.id} id={c.id} label={c.label} active={colormap === c.id} onPick={() => setColormap(c.id)} />
        ))}
      </div>
      <label style={ridgeRow} title="Ridge lines on the 3D panadapter. Off is the solid-colour look.">
        <span style={label}>3D RIDGE LINES</span>
        <select
          value={ridge}
          onChange={(e) => setRidge(e.target.value as 'off' | 'signals' | 'all')}
          style={select}
        >
          <option value="off">Off</option>
          <option value="signals">Signals only</option>
          <option value="all">All rows</option>
        </select>
      </label>
    </div>
  );
}

const wrap: CSSProperties = { display: 'flex', flexDirection: 'column', gap: 8, padding: '6px 0' };
const label: CSSProperties = { fontSize: 10, letterSpacing: '0.08em', color: 'var(--fg-3, #8a94a3)' };
const tiles: CSSProperties = { display: 'flex', gap: 8, flexWrap: 'wrap' };
const tile: CSSProperties = {
  display: 'flex', flexDirection: 'column', gap: 4, padding: 4, borderRadius: 6,
  border: '1px solid var(--line, #2a3140)', background: 'rgba(10, 14, 20, 0.7)', cursor: 'pointer',
  color: 'var(--fg-1, #cfd6e0)',
};
const tileActive: CSSProperties = { borderColor: 'var(--accent, #f0b33a)', boxShadow: '0 0 0 1px var(--accent, #f0b33a) inset' };
const tileCanvas: CSSProperties = { display: 'block', width: 160, height: 96, borderRadius: 3 };
const tileLabel: CSSProperties = { fontSize: 11, textAlign: 'center' };
const ridgeRow: CSSProperties = { display: 'flex', alignItems: 'center', gap: 10 };
const select: CSSProperties = {
  background: '#0d1526', color: '#cfe6ff', border: '1px solid var(--line, #2a3140)', borderRadius: 4, padding: '3px 6px', fontSize: 12,
};
