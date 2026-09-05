// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useEffect, useState } from 'react';
import { getPsAmpView, type PsAmpViewDto } from '../api/client';
import { useTxStore } from '../state/tx-store';

const W = 400;
const H = 180;
const PAD = 8;

/** Map arrays into an SVG polyline/points string. x is 0..1; y scaled by
 *  the caller-supplied range. */
function toPoints(
  xs: number[],
  ys: number[],
  yMin: number,
  yMax: number,
): string {
  const span = yMax - yMin || 1;
  const out: string[] = [];
  for (let i = 0; i < xs.length && i < ys.length; i++) {
    const xv = xs[i] ?? 0;
    const yv = ys[i] ?? 0;
    const px = PAD + xv * (W - 2 * PAD);
    const py = H - PAD - ((yv - yMin) / span) * (H - 2 * PAD);
    out.push(`${px.toFixed(1)},${py.toFixed(1)}`);
  }
  return out.join(' ');
}

function range(ys: number[], fallbackLo: number, fallbackHi: number): [number, number] {
  const finite = ys.filter((v) => Number.isFinite(v));
  if (finite.length === 0) return [fallbackLo, fallbackHi];
  let lo = Math.min(...finite);
  let hi = Math.max(...finite);
  if (hi - lo < 1e-6) {
    lo -= 0.5;
    hi += 0.5;
  }
  const margin = (hi - lo) * 0.08;
  return [lo - margin, hi + margin];
}

/**
 * AMP VIEW — PureSignal 3's own picture of the amplifier: the measured
 * gain and phase transfer (scatter, from calcc's per-bucket averages) with
 * the smoothed correction curves it is applying, straight from WDSP
 * GetPSDisp. Read-only telemetry; polls only while PS is armed and the
 * card is mounted. Empty until the first completed fit.
 */
export function PsAmpViewCard() {
  const psEnabled = useTxStore((s) => s.psEnabled);
  const [view, setView] = useState<PsAmpViewDto | null>(null);

  useEffect(() => {
    if (!psEnabled) {
      setView(null);
      return;
    }
    const ctl = new AbortController();
    let alive = true;
    const tick = async () => {
      try {
        const v = await getPsAmpView(ctl.signal);
        if (alive) setView(v);
      } catch {
        /* transient — keep the last frame */
      }
    };
    void tick();
    const timer = setInterval(() => void tick(), 1000);
    return () => {
      alive = false;
      ctl.abort();
      clearInterval(timer);
    };
  }, [psEnabled]);

  const hasData = (view?.x.length ?? 0) > 0;
  const [gLo, gHi] = range(view?.gainY ?? [], 0, 1);
  const [pLo, pHi] = range(view?.phaseDegY ?? [], -10, 10);

  return (
    <div className="ps-card">
      <h4>
        <svg className="ps-ic-sm" viewBox="0 0 12 12">
          <path d="M1 10 C4 10 5 2 11 2" fill="none" />
          <path d="M1 10 L11 10" opacity="0.4" />
        </svg>
        Amp view
        <span className="ps-card-hint">what the calibrator measured</span>
      </h4>

      {!psEnabled ? (
        <div className="ps-ampview-empty">Arm PS and key up to populate.</div>
      ) : !hasData ? (
        <div className="ps-ampview-empty">
          Waiting for the first completed calibration fit…
        </div>
      ) : (
        <>
          <div
            className="ps-ampview-plot"
            title="Measured amplifier gain vs input amplitude (dots) and the magnitude correction curve PS is applying (line). A flat trace is a linear amp; sag at the right edge is compression PS is correcting."
          >
            <svg viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none">
              <polyline
                points={toPoints(view!.x, view!.gainY, gLo, gHi)}
                fill="none"
                stroke="var(--accent, #4aa3df)"
                strokeWidth="0"
              />
              {view!.x.map((x, i) => {
                const px = PAD + x * (W - 2 * PAD);
                const py =
                  H - PAD - (((view!.gainY[i] ?? gLo) - gLo) / (gHi - gLo)) * (H - 2 * PAD);
                return (
                  <circle key={i} cx={px} cy={py} r="1.3" fill="var(--accent, #4aa3df)" />
                );
              })}
              <polyline
                points={toPoints(view!.magCorX, view!.magCorY, gLo, gHi)}
                fill="none"
                stroke="#e0b34a"
                strokeWidth="1.5"
              />
            </svg>
            <div className="ps-ampview-cap">
              GAIN · measured (dots) + magnitude correction (amber)
            </div>
          </div>

          <div
            className="ps-ampview-plot"
            title={`Measured phase vs input amplitude in degrees, relative to the fit's phase reference (${view!.phaseRefDeg.toFixed(1)}°), with the phase correction curve. AM-PM conversion shows as a bend.`}
          >
            <svg viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none">
              {view!.x.map((x, i) => {
                const px = PAD + x * (W - 2 * PAD);
                const py =
                  H -
                  PAD -
                  (((view!.phaseDegY[i] ?? pLo) - pLo) / (pHi - pLo)) * (H - 2 * PAD);
                return <circle key={i} cx={px} cy={py} r="1.3" fill="#7fd18a" />;
              })}
              <polyline
                points={toPoints(view!.phaseCorX, view!.phaseCorY, pLo, pHi)}
                fill="none"
                stroke="#e0b34a"
                strokeWidth="1.5"
              />
            </svg>
            <div className="ps-ampview-cap">
              PHASE · measured (green, °) + phase correction (amber) · ref{' '}
              {view!.phaseRefDeg.toFixed(1)}°
            </div>
          </div>
        </>
      )}
    </div>
  );
}
