// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// DIVERSITY panel — the frontend face of the shipped combiner
// (POST /api/rx/diversity; DspPipelineService complex combine, P2-only).
//
// Design: the combiner weight is ONE complex number w = gain·e^{jθ}, so the
// control is one point on a polar pad — angle is phase, radius is gain. One
// drag searches both dimensions at once, which matters because a null is a
// single point in (θ, g) space. The pad leaves a heat-trail of visited
// weights colored by the live S-meter, so the null valley becomes visible as
// the operator hunts; four memory slots persist per-browser (saved nulls are
// per-QRM-source: the MW plasma TV and the 40 m PSU hash live at different
// weights) and render ON the pad as tappable diamonds. Meter feedback is the
// existing calibrated signalAv stream — no new backend.

import { useEffect, useRef, useState } from 'react';
import { useDiversityStore } from '../state/diversity-store';
import { useRxMetersStore } from '../state/rx-meters-store';
import { useConnectionStore } from '../state/connection-store';

const GMAX = 2;
const clamp = (x: number, a: number, b: number) => Math.max(a, Math.min(b, x));

interface TrailPoint {
  phase: number;
  gain: number;
  dbm: number;
}

export function DiversityPanel() {
  const st = useDiversityStore();
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const [armSave, setArmSave] = useState(false);
  // trail + rolling meter window live in refs — redrawn per frame, never state
  const trail = useRef<TrailPoint[]>([]);
  const meterWin = useRef<{ min: number; max: number }>({ min: -100, max: -60 });
  const glide = useRef<{ phase: number; gain: number } | null>(null);
  const dragging = useRef(false);
  const longPress = useRef<ReturnType<typeof setTimeout> | null>(null);

  // ---- geometry helpers (canvas CSS pixel space) ----
  const geom = (c: HTMLCanvasElement) => {
    const w = c.clientWidth;
    const h = c.clientHeight;
    const cx = w / 2;
    const cy = h / 2;
    const R = Math.max(40, Math.min(w, h) / 2 - 18);
    return { w, h, cx, cy, R };
  };
  const toXY = (c: HTMLCanvasElement, phase: number, gain: number) => {
    const { cx, cy, R } = geom(c);
    const a = ((phase - 90) * Math.PI) / 180;
    const r = (gain / GMAX) * R;
    return [cx + Math.cos(a) * r, cy + Math.sin(a) * r] as const;
  };

  // ---- pointer interaction: drag the weight, tap a diamond to recall ----
  useEffect(() => {
    const c = canvasRef.current;
    if (!c) return;
    const fromXY = (x: number, y: number) => {
      const { cx, cy, R } = geom(c);
      const dx = x - cx;
      const dy = y - cy;
      const gain = clamp((Math.hypot(dx, dy) / R) * GMAX, 0, GMAX);
      const phase = ((Math.atan2(dy, dx) * 180) / Math.PI + 90 + 360) % 360;
      useDiversityStore.getState().setWeight(gain, phase);
    };
    const down = (e: PointerEvent) => {
      const r = c.getBoundingClientRect();
      const x = e.clientX - r.left;
      const y = e.clientY - r.top;
      const s = useDiversityStore.getState();
      for (let i = 0; i < s.mems.length; i++) {
        const m = s.mems[i];
        if (!m) continue;
        const [mx, my] = toXY(c, m.phaseDeg, m.gain);
        if (Math.hypot(x - mx, y - my) < 12) {
          glide.current = { phase: m.phaseDeg, gain: m.gain };
          s.recallMem(i);
          return;
        }
      }
      glide.current = null;
      dragging.current = true;
      c.setPointerCapture(e.pointerId);
      fromXY(x, y);
    };
    const move = (e: PointerEvent) => {
      if (!dragging.current) return;
      const r = c.getBoundingClientRect();
      fromXY(e.clientX - r.left, e.clientY - r.top);
    };
    const up = () => {
      dragging.current = false;
    };
    c.addEventListener('pointerdown', down);
    c.addEventListener('pointermove', move);
    c.addEventListener('pointerup', up);
    return () => {
      c.removeEventListener('pointerdown', down);
      c.removeEventListener('pointermove', move);
      c.removeEventListener('pointerup', up);
    };
  }, []);

  // ---- render loop ----
  useEffect(() => {
    let raf = 0;
    const draw = () => {
      raf = requestAnimationFrame(draw);
      const c = canvasRef.current;
      if (!c) return;
      const dpr = Math.min(2, window.devicePixelRatio || 1);
      const { w, h, cx, cy, R } = geom(c);
      if (w === 0 || h === 0) return;
      if (c.width !== Math.round(w * dpr)) c.width = Math.round(w * dpr);
      if (c.height !== Math.round(h * dpr)) c.height = Math.round(h * dpr);
      const g = c.getContext('2d');
      if (!g) return;
      g.setTransform(dpr, 0, 0, dpr, 0, 0);

      const s = useDiversityStore.getState();
      // glide toward a recalled memory
      if (glide.current) {
        let dp = glide.current.phase - s.phaseDeg;
        if (dp > 180) dp -= 360;
        if (dp < -180) dp += 360;
        const ng = s.gain + (glide.current.gain - s.gain) * 0.2;
        useDiversityStore.setState({
          phaseDeg: (s.phaseDeg + dp * 0.2 + 360) % 360,
          gain: ng,
        });
        if (Math.abs(dp) < 0.3 && Math.abs(glide.current.gain - ng) < 0.01)
          glide.current = null;
      }

      // live meter → rolling window normalization for trail/dot color
      const dbm = useRxMetersStore.getState().signalAv;
      const win = meterWin.current;
      if (Number.isFinite(dbm)) {
        win.min = Math.min(win.min * 0.999 + dbm * 0.001, dbm);
        win.max = Math.max(win.max * 0.999 + dbm * 0.001, dbm);
        if (s.enabled && (dragging.current || glide.current)) {
          trail.current.push({ phase: s.phaseDeg, gain: s.gain, dbm });
          if (trail.current.length > 900) trail.current.shift();
        }
      }
      const quiet = (v: number) =>
        win.max - win.min < 3 ? 0.5 : clamp((win.max - v) / (win.max - win.min), 0, 1);

      g.clearRect(0, 0, w, h);
      // polar grid
      g.strokeStyle = '#1b2436';
      g.lineWidth = 1;
      for (const k of [0.25, 0.5, 0.75, 1]) {
        g.beginPath();
        g.arc(cx, cy, R * k, 0, 7);
        g.stroke();
      }
      for (let a = 0; a < 360; a += 30) {
        const t = ((a - 90) * Math.PI) / 180;
        g.beginPath();
        g.moveTo(cx, cy);
        g.lineTo(cx + Math.cos(t) * R, cy + Math.sin(t) * R);
        g.stroke();
      }
      g.fillStyle = '#7d8694';
      g.font = '9px monospace';
      g.fillText('g=1', cx + R * 0.5 + 3, cy - 3);
      g.fillText('g=2', cx + R + 3, cy - 3);
      g.fillText('0°', cx - 6, cy - R - 4);
      // heat-trail
      for (const p of trail.current) {
        const u = quiet(p.dbm);
        const [x, y] = toXY(c, p.phase, p.gain);
        g.fillStyle = `rgba(${(57 + (255 - 57) * (1 - u)) | 0},${u > 0.5 ? 217 : 150},${
          u > 0.5 ? 138 : 90
        },${0.1 + 0.25 * u})`;
        g.fillRect(x - 2, y - 2, 4, 4);
      }
      // memory diamonds
      s.mems.forEach((m) => {
        if (!m) return;
        const [mx, my] = toXY(c, m.phaseDeg, m.gain);
        g.save();
        g.translate(mx, my);
        g.rotate(Math.PI / 4);
        g.fillStyle = 'rgba(255,177,60,.9)';
        g.fillRect(-5, -5, 10, 10);
        g.strokeStyle = '#0a0a0c';
        g.lineWidth = 1.5;
        g.strokeRect(-5, -5, 10, 10);
        g.restore();
        g.fillStyle = '#ffb13c';
        g.font = '8px monospace';
        g.fillText(m.label, mx + 8, my - 6);
      });
      // the live weight
      const [x, y] = toXY(c, s.phaseDeg, s.gain);
      const u = quiet(dbm);
      g.strokeStyle = '#31353d';
      g.beginPath();
      g.moveTo(cx, cy);
      g.lineTo(x, y);
      g.stroke();
      g.fillStyle = s.enabled
        ? u > 0.7
          ? '#39d98a'
          : u > 0.35
            ? '#ffb13c'
            : '#ff5d5d'
        : '#7d8694';
      g.beginPath();
      g.arc(x, y, 7, 0, 7);
      g.fill();
      g.strokeStyle = '#eaf4ff';
      g.lineWidth = 1.5;
      g.beginPath();
      g.arc(x, y, 7, 0, 7);
      g.stroke();
    };
    raf = requestAnimationFrame(draw);
    return () => cancelAnimationFrame(raf);
  }, []);

  const dial = useConnectionStore((s) =>
    s.vfoHz > 0 ? (s.vfoHz / 1e6).toFixed(3) : '—',
  );
  const dbm = useRxMetersStore((s) => s.signalAv);

  const onSlotDown = (i: number) => {
    longPress.current = setTimeout(() => {
      st.clearMem(i);
      longPress.current = null;
    }, 700);
  };
  const onSlotUp = (i: number) => {
    if (!longPress.current) return;
    clearTimeout(longPress.current);
    longPress.current = null;
    if (armSave) {
      const win = meterWin.current;
      const depth = Number.isFinite(dbm) ? dbm - win.max : 0;
      st.saveMem(i, depth, dial);
      setArmSave(false);
    } else if (st.mems[i]) {
      glide.current = { phase: st.mems[i]!.phaseDeg, gain: st.mems[i]!.gain };
      st.recallMem(i);
    }
  };

  return (
    <div ref={wrapRef} className="diversity-panel">
      <div className="diversity-toolbar">
        <button
          className={`ps-pill ${st.enabled ? 'on' : ''}`}
          onClick={() => st.setEnabled(!st.enabled)}
        >
          {st.enabled ? 'ENABLED' : 'OFF'}
        </button>
        <span className="diversity-src">
          SRC
          {[1, 2].map((rx) => (
            <button
              key={rx}
              className={`ps-pill sm ${st.sourceRx === rx ? 'on' : ''}`}
              onClick={() => st.setSourceRx(rx)}
            >
              RX{rx + 1}
            </button>
          ))}
        </span>
        <span className="diversity-spacer" />
        <button
          className={`ps-pill save ${armSave ? 'arm' : ''}`}
          onClick={() => setArmSave((v) => !v)}
          title="Arm, then tap a slot to store the current weight"
        >
          SAVE
        </button>
      </div>
      <canvas ref={canvasRef} className="diversity-pad" />
      <div className="diversity-readout">
        <span className="ro-g">g {st.gain.toFixed(2)}</span>
        <span className="ro-p">θ {st.phaseDeg.toFixed(1).padStart(5, '0')}°</span>
        <span className="ro-m">{Number.isFinite(dbm) ? `${dbm.toFixed(1)} dBm` : '—'}</span>
      </div>
      <div className="diversity-mems">
        {st.mems.map((m, i) => (
          <div
            key={i}
            className={`diversity-mem ${m ? '' : 'empty'}`}
            onPointerDown={() => onSlotDown(i)}
            onPointerUp={() => onSlotUp(i)}
            onPointerLeave={() => {
              if (longPress.current) {
                clearTimeout(longPress.current);
                longPress.current = null;
              }
            }}
          >
            {m ? (
              <>
                <div className="mem-t">{m.label}</div>
                <div className="mem-d">
                  θ{m.phaseDeg.toFixed(0)}° g{m.gain.toFixed(2)} · {m.dialMhz}
                </div>
              </>
            ) : (
              <>
                <div className="mem-t">M{i + 1}</div>
                <div className="mem-d">{armSave ? 'tap to save' : 'empty'}</div>
              </>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
