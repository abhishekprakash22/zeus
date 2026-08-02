// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { useEffect, useRef, useState } from 'react';
import { detectPeaks, peakAlpha, useSignalEnhanceStore, type DetectedPeak } from '../dsp/signal-estimator';
import { useDisplayStore } from '../state/display-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';

// Snap-target markers: short ticks rising from the noise-floor baseline at each
// detected carrier, so the operator can see exactly where a snap click will
// land. Shown only while SNAP is engaged. Coloured to match the operator's RX
// trace colour (sanctioned amber peak-tick by default — signal-strength
// visualisation, varying alpha; see dev-conventions.md), with opacity scaled by
// the peak's SNR above the noise floor.
//
// Recompute is throttled to ~5 Hz: markers don't need the 30 Hz frame rate, and
// the spectrum surfaces deliberately keep React out of the per-frame path.
//
// RENDERING: one shared <canvas>, not per-peak DOM divs. Each shadowed div was
// its own composited quad; on a crowded band the churning marker set made
// Chromium allocate tile/texture memory that its cache policy never returns on
// feature-disable (field report: GPU figure creeps under SNAP and latches
// until reload). A single fixed canvas layer allocates once, draws ticks in 2D
// at the throttled cadence, and leaves NOTHING to accumulate — disable
// unmounts one layer and the footprint is exactly what it was before enable.
const RECOMPUTE_MIN_INTERVAL_MS = 200; // Pi CPU budget: markers don't need >5 Hz

type Snapshot = { peaks: DetectedPeak[]; centerHz: number; spanHz: number };

const EMPTY: Snapshot = { peaks: [], centerHz: 0, spanHz: 0 };

export function PeakMarkerOverlay() {
  const snapEnabled = useSignalEnhanceStore((s) => s.snapEnabled);
  const peakMinSnrDb = useSignalEnhanceStore((s) => s.peakMinSnrDb);
  const traceColor = useDisplaySettingsStore((s) => s.rxTraceColor);
  const [snap, setSnap] = useState<Snapshot>(EMPTY);

  useEffect(() => {
    if (!snapEnabled) {
      setSnap(EMPTY);
      return;
    }
    let lastAt = 0;
    const recompute = () => {
      const s = useDisplayStore.getState();
      if (!s.panDb || s.hzPerPixel <= 0) {
        setSnap(EMPTY);
        return;
      }
      const centerHz = Number(s.centerHz);
      const spanHz = s.panDb.length * s.hzPerPixel;
      const peaks = detectPeaks(s.panDb, centerHz, s.hzPerPixel);
      // Skip the React churn when nothing moved: same peak set + same view →
      // no setState, no reconcile of N marker divs (Pi CPU budget).
      setSnap((prev) => {
        if (
          prev.centerHz === centerHz &&
          prev.spanHz === spanHz &&
          prev.peaks.length === peaks.length &&
          prev.peaks.every((pk, i) => pk.hz === peaks[i]!.hz && pk.snrDb === peaks[i]!.snrDb)
        )
          return prev;
        return { peaks, centerHz, spanHz };
      });
    };
    const unsub = useDisplayStore.subscribe((state, prev) => {
      if (state.lastSeq === prev.lastSeq) return;
      const now = performance.now();
      if (now - lastAt < RECOMPUTE_MIN_INTERVAL_MS) return;
      lastAt = now;
      recompute();
    });
    recompute();
    return unsub;
  }, [snapEnabled, peakMinSnrDb]);

  const canvasRef = useRef<HTMLCanvasElement | null>(null);

  // Draw the ticks whenever the (throttled) snapshot changes. The canvas is a
  // single 16 px strip along the baseline — one layer, constant memory.
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const cssW = canvas.clientWidth;
    const cssH = canvas.clientHeight;
    if (cssW === 0 || cssH === 0) return;
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const w = Math.round(cssW * dpr);
    const h = Math.round(cssH * dpr);
    if (canvas.width !== w) canvas.width = w;
    if (canvas.height !== h) canvas.height = h;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    ctx.clearRect(0, 0, w, h);
    if (snap.peaks.length === 0 || snap.spanHz <= 0) return;
    const startHz = snap.centerHz - snap.spanHz / 2;
    const tickW = Math.max(1, Math.round(1.5 * dpr));
    for (const p of snap.peaks) {
      const frac = (p.hz - startHz) / snap.spanHz;
      if (frac < -0.01 || frac > 1.01) continue;
      const x = Math.round(frac * w);
      // dark backing stroke stands in for the old per-div box-shadow
      ctx.globalAlpha = 0.9;
      ctx.fillStyle = 'rgba(0,0,0,0.9)';
      ctx.fillRect(x - tickW, 0, tickW * 3, h);
      ctx.globalAlpha = peakAlpha(p.snrDb);
      ctx.fillStyle = traceColor;
      ctx.fillRect(x - Math.floor(tickW / 2), 0, tickW, h);
    }
    ctx.globalAlpha = 1;
  }, [snap, traceColor]);

  if (!snapEnabled) return null;

  return (
    <canvas
      ref={canvasRef}
      className="pointer-events-none absolute bottom-0 left-0 z-[7] h-4 w-full"
      aria-hidden
    />
  );
}
