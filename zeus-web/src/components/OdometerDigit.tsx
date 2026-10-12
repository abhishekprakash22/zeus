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
// Odometer VFO digit (Effects ≥ Subtle, off under prefers-reduced-motion).
// Each digit is a clipped column of numerals that rolls to the new value:
// a quick tick for an encoder detent, a roll for an ordinary retune, and a
// full extra revolution on a big jump (band change). Tuning up rolls the
// digits up, tuning down rolls them down — 9→0 going up rolls forward
// through the carry, never backwards through 8…1.
//
// Layout never moves: an invisible copy of the numeral keeps the digit's
// width and baseline exactly as the static display had them; the rolling
// column is an aria-hidden overlay clipped to that box. CSS transforms only.

import { useLayoutEffect, useRef, useState } from 'react';

export type OdometerMotion = 'tick' | 'roll' | 'spin';

const COPIES = 5;
const MID = 20; // start of the middle copy (cells 20..29)
const CELLS = COPIES * 10;

export const ODOMETER_MS: Record<OdometerMotion, number> = { tick: 140, roll: 260, spin: 520 };

/**
 * Cells to move from prev to next: forward (up) when tuning up, backward
 * when tuning down, plus a full revolution on a spin. 0 when unchanged.
 */
export function odometerStep(prev: number, next: number, dir: 1 | -1, motion: OdometerMotion): number {
  if (prev === next) return 0;
  const base = dir > 0 ? (next - prev + 10) % 10 : -((prev - next + 10) % 10);
  return motion === 'spin' ? base + (dir > 0 ? 10 : -10) : base;
}

/** Classify a dial change for the roll style. */
export function odometerMotion(deltaHz: number, changedDigits: number): OdometerMotion {
  if (Math.abs(deltaHz) >= 100_000) return 'spin';
  return changedDigits <= 1 ? 'tick' : 'roll';
}

type Props = {
  value: number;
  dir: 1 | -1;
  motion: OdometerMotion;
};

export function OdometerDigit({ value, dir, motion }: Props) {
  const [pos, setPos] = useState(MID + value);
  const [ms, setMs] = useState(0);
  const prevValue = useRef(value);
  const posRef = useRef(MID + value);

  useLayoutEffect(() => {
    const prev = prevValue.current;
    if (prev === value) return;
    prevValue.current = value;
    const step = odometerStep(prev, value, dir, motion);
    let from = posRef.current;
    // Mid-flight retarget near the ends of the strip: jump (invisibly) to the
    // same numeral in the middle copy first.
    if (from + step < 0 || from + step > CELLS - 1) from = MID + (((from % 10) + 10) % 10);
    const to = from + step;
    posRef.current = to;
    setMs(ODOMETER_MS[motion]);
    setPos(to);
  }, [value, dir, motion]);

  const settle = () => {
    // Park on the middle copy without animation so the next roll has room.
    const home = MID + prevValue.current;
    posRef.current = home;
    setMs(0);
    setPos(home);
  };

  const cells = [];
  for (let i = 0; i < CELLS; i++) cells.push(<span key={i} className="odo-cell">{i % 10}</span>);

  return (
    <>
      <span className="odo-ghost">{value}</span>
      <span className="odo-win" aria-hidden>
        <span
          className="odo-col"
          data-testid="odo-col"
          style={{
            transform: `translateY(${-pos * 100}%)`,
            transition: ms > 0 ? `transform ${ms}ms cubic-bezier(.2,.8,.25,1.06)` : 'none',
          }}
          onTransitionEnd={settle}
        >
          {cells}
        </span>
      </span>
    </>
  );
}
