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
// What the classifier sees on one RX1 frame: each detected signal's centre,
// occupied width (crest out to where it sinks into the local floor) and level
// steadiness. Skirts of one wide signal collapse onto its strongest crest.

import {
  detectPeaks,
  getNoiseFloor,
  getSignalStationarity,
  measureSignalExtent,
} from '../dsp/signal-estimator';
import type { ObservedSignal } from '../dsp/signal-classifier';

const MIN_SNR_DB = 8;
const EDGE_MARGIN_DB = 3;
const MAX_HALF_WIDTH_HZ = 12_000;
const MAX_SIGNALS = 24;

export function observeSignals(panDb: Float32Array, centerHz: number, hzPerPixel: number): ObservedSignal[] {
  if (panDb.length < 8 || hzPerPixel <= 0) return [];
  const peaks = detectPeaks(panDb, centerHz, hzPerPixel, 0).filter((p) => p.snrDb >= MIN_SNR_DB);
  const floor = getNoiseFloor(0);
  const stat = getSignalStationarity(0);
  const half = panDb.length / 2;
  const radius = Math.max(2, Math.round(MAX_HALF_WIDTH_HZ / hzPerPixel));
  const taken: Array<[number, number]> = [];
  const out: ObservedSignal[] = [];
  for (const p of peaks) {
    if (out.length >= MAX_SIGNALS) break;
    const bin = Math.round((p.hz - centerHz) / hzPerPixel + half);
    if (bin < 0 || bin >= panDb.length) continue;
    if (taken.some(([lo, hi]) => bin >= lo && bin <= hi)) continue;
    const [lo, hi] = measureSignalExtent(panDb, floor, bin, EDGE_MARGIN_DB, radius);
    taken.push([lo, hi]);
    const s = stat && stat.length === panDb.length ? stat[bin]! : 0;
    out.push({
      hz: centerHz + ((lo + hi) / 2 - half) * hzPerPixel,
      widthHz: (hi - lo + 1) * hzPerPixel,
      stationarity: Number.isFinite(s) ? s : 0,
    });
  }
  return out;
}
