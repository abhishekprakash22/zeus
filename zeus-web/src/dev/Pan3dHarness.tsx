// SPDX-License-Identifier: GPL-2.0-or-later
//
// 3D panadapter harness. Open the app with ?pan3dharness=1.
//
// Drives the REAL WebGPU renderer (createPanSurfaceRenderer) with a canned,
// deterministic band, every tuning value on a slider, and a snapshot button.
// This exists because the last three 3D rounds were judged by eye on a live
// band and all three were reverted: the arithmetic has to be looked at with
// the same input every time, or it cannot be looked at at all.

import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { probeWebGpu } from '../gl/webgpu/caps';
import { createPanSurfaceRenderer, type PanSurfaceRenderer, type PanSurfaceRidgeMode } from '../gl/webgpu/pan-surface';
import { COLORMAPS, type ColormapId } from '../gl/colormap';

const COLS = 1024;
const CENTER_HZ = 14_200_000;
const HZ_PER_PX = 48; // ~49 kHz across

// Same band every run. Row t is one FFT; signals breathe with t so scroll is visible.
function makeRow(t: number, seed: { v: number }): Float32Array {
  const row = new Float32Array(COLS);
  const rnd = () => { seed.v = (seed.v * 1103515245 + 12345) & 0x7fffffff; return seed.v / 0x7fffffff; };
  for (let c = 0; c < COLS; c++) {
    const x = c / (COLS - 1);
    let v = -118 + (rnd() - 0.5) * 3.4;
    const sig: Array<[number, number, number, number]> = [
      [0.22, 0.012, 34, 0], [0.47, 0.008, 26, 1.3], [0.61, 0.02, 18, 2.1], [0.83, 0.010, 30, 0.7],
    ];
    for (const [cc, w, a, ph] of sig) {
      const amp = a * (0.65 + 0.35 * Math.sin(t * 0.25 + ph));
      v += amp * Math.exp(-(((x - cc) / w) ** 2));
    }
    v += 6 * Math.exp(-(((x - 0.47) / 0.15) ** 2));
    row[c] = v;
  }
  return row;
}

type Knobs = {
  panMin: number; panMax: number; wfMin: number; wfMax: number;
  haze: number; relief: number; ridge: PanSurfaceRidgeMode; colormap: ColormapId; running: boolean;
};

export function Pan3dHarness() {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const rendererRef = useRef<PanSurfaceRenderer | null>(null);
  const [status, setStatus] = useState('probing WebGPU…');
  const [fps, setFps] = useState(0);
  const [k, setK] = useState<Knobs>({
    panMin: -130, panMax: -60, wfMin: -122, wfMax: -82,
    haze: 0.45, relief: 0.74, ridge: 'off', colormap: 'blue', running: true,
  });
  const kRef = useRef(k);
  kRef.current = k;

  useEffect(() => {
    let disposed = false;
    let raf = 0;
    let pushTimer = 0;
    (async () => {
      const probe = await probeWebGpu();
      const canvas = canvasRef.current;
      if (!canvas) return;
      if (!probe.supported || !probe.device) { setStatus(`WebGPU unavailable: ${probe.reason ?? 'unknown'}`); return; }
      const ctx = canvas.getContext('webgpu');
      if (!ctx) { setStatus('no webgpu canvas context'); return; }
      const r = createPanSurfaceRenderer(probe.device, ctx, probe.format);
      rendererRef.current = r;
      r.resize(canvas.width, canvas.height);
      setStatus(`WebGPU ${probe.adapter}`);
      const seed = { v: 11 };
      let t = 0;
      // pre-fill history so the first frame already has depth
      for (let i = 0; i < 160; i++) r.pushRow(makeRow(t++, seed), CENTER_HZ, HZ_PER_PX, 'rx');
      pushTimer = window.setInterval(() => {
        if (disposed || !kRef.current.running) return;
        r.pushRow(makeRow(t++, seed), CENTER_HZ, HZ_PER_PX, 'rx');
      }, 50);
      let frames = 0; let last = performance.now();
      const loop = () => {
        if (disposed) return;
        const kk = kRef.current;
        r.setColormap(kk.colormap);
        r.setRidgeMode(kk.ridge);
        r.setHaze(kk.haze);
        r.setReliefDepth(kk.relief);
        r.draw(kk.panMin, kk.panMax, CENTER_HZ, HZ_PER_PX, {
          rxDbMin: kk.panMin, rxDbMax: kk.panMax, txDbMin: kk.panMin, txDbMax: kk.panMax,
          colorDbMin: kk.wfMin, colorDbMax: kk.wfMax,
        });
        frames++;
        const now = performance.now();
        if (now - last >= 1000) { setFps(Math.round((frames * 1000) / (now - last))); frames = 0; last = now; }
        raf = requestAnimationFrame(loop);
      };
      raf = requestAnimationFrame(loop);
    })();
    return () => {
      disposed = true;
      cancelAnimationFrame(raf);
      clearInterval(pushTimer);
      rendererRef.current?.dispose();
      rendererRef.current = null;
    };
  }, []);

  const snapshot = () => {
    const c = canvasRef.current;
    if (!c) return;
    // Draw one more frame synchronously into the same canvas, then read it.
    c.toBlob((blob) => {
      if (!blob) return;
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob);
      a.download = `pan3d-${k.colormap}-${k.ridge}-haze${k.haze}-${Date.now()}.png`;
      a.click();
    });
  };

  const num = (key: keyof Knobs, min: number, max: number, step: number) => (
    <label style={row}>
      <span style={lab}>{key}</span>
      <input type="range" min={min} max={max} step={step} value={k[key] as number}
        onChange={(e) => setK({ ...k, [key]: Number(e.target.value) })} style={{ flex: 1 }} />
      <span style={val}>{String(k[key])}</span>
    </label>
  );

  return (
    <div style={page}>
      <div style={head}>
        <strong>3D panadapter harness</strong> · {status} · {fps} fps
        <button type="button" onClick={snapshot} style={btn}>Snapshot PNG</button>
        <button type="button" onClick={() => setK({ ...k, running: !k.running })} style={btn}>{k.running ? 'Pause' : 'Run'}</button>
      </div>
      <canvas ref={canvasRef} width={1280} height={420} style={cv} />
      <div style={knobs}>
        {num('panMin', -160, -40, 1)}
        {num('panMax', -160, -40, 1)}
        {num('wfMin', -160, -40, 1)}
        {num('wfMax', -160, -40, 1)}
        {num('haze', 0, 1, 0.01)}
        {num('relief', 0, 1, 0.01)}
        <label style={row}><span style={lab}>ridge</span>
          <select value={k.ridge} onChange={(e) => setK({ ...k, ridge: e.target.value as PanSurfaceRidgeMode })}>
            <option value="off">off</option><option value="signals">signals</option><option value="all">all</option>
          </select>
        </label>
        <label style={row}><span style={lab}>palette</span>
          <select value={k.colormap} onChange={(e) => setK({ ...k, colormap: e.target.value as ColormapId })}>
            {COLORMAPS.map((c) => <option key={c.id} value={c.id}>{c.label}</option>)}
          </select>
        </label>
      </div>
      <p style={note}>
        Canned band, deterministic. Height uses panMin/panMax; colour uses wfMin/wfMax through the waterfall's LUT.
        The two are independent on purpose. Pi target: ≥ 20 fps.
      </p>
    </div>
  );
}

const page: CSSProperties = { background: '#0b0e13', color: '#e8ecf1', minHeight: '100vh', padding: 12, fontFamily: 'system-ui, sans-serif' };
const head: CSSProperties = { display: 'flex', gap: 12, alignItems: 'center', marginBottom: 8, fontSize: 13 };
const cv: CSSProperties = { display: 'block', width: '100%', maxWidth: 1280, border: '1px solid #2a3140', borderRadius: 4 };
const knobs: CSSProperties = { display: 'grid', gridTemplateColumns: 'repeat(2, minmax(280px, 1fr))', gap: '6px 24px', marginTop: 10, maxWidth: 900 };
const row: CSSProperties = { display: 'flex', alignItems: 'center', gap: 8, fontSize: 12 };
const lab: CSSProperties = { width: 64, color: '#9aa4b1' };
const val: CSSProperties = { width: 48, textAlign: 'right', fontVariantNumeric: 'tabular-nums' };
const btn: CSSProperties = { marginLeft: 8, padding: '3px 10px', background: '#1a2030', color: '#e8ecf1', border: '1px solid #2a3140', borderRadius: 4, cursor: 'pointer' };
const note: CSSProperties = { color: '#9aa4b1', fontSize: 12, marginTop: 10 };
