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

import { detectPeaks, getNoiseFloor, getSignalStationarity } from '../dsp/signal-estimator';
import type { ObservedSignal } from '../dsp/signal-classifier';

// A crest must stand this far over the local floor to count as a signal. The
// trace is barely averaged, so noise alone throws up 8 dB spikes several times
// a frame (and the old 8 dB bar let those keep dead tracks alive); at 13 dB a
// noise spike is a once-in-minutes event.
export const MIN_SNR_DB = 13;
/** A bin belongs to a signal while the smoothed level is this far over the floor. */
const OCCUPIED_MARGIN_DB = 5;
/** Smoothing span: voice energy is ragged bin to bin; over ~250 Hz it is a body. */
const SMOOTH_HZ = 250;
/** Dips shorter than this are inside one signal (gaps between voice formants),
 *  not the space between two. */
const BRIDGE_HZ = 400;
const MAX_HALF_WIDTH_HZ = 12_000;
const MAX_SIGNALS = 24;

/** Box-smooth a dB trace over `halfBins` each side (prefix sums; non-finite
 *  bins are skipped). */
export function smoothDb(panDb: Float32Array, halfBins: number): Float32Array {
  const n = panDb.length;
  if (halfBins <= 0) return panDb;
  const sum = new Float64Array(n + 1);
  const cnt = new Int32Array(n + 1);
  for (let i = 0; i < n; i++) {
    const v = panDb[i]!;
    const ok = Number.isFinite(v);
    sum[i + 1] = sum[i]! + (ok ? v : 0);
    cnt[i + 1] = cnt[i]! + (ok ? 1 : 0);
  }
  const out = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    const a = Math.max(0, i - halfBins);
    const b = Math.min(n, i + halfBins + 1);
    const c = cnt[b]! - cnt[a]!;
    out[i] = c > 0 ? (sum[b]! - sum[a]!) / c : Number.NaN;
  }
  return out;
}

/**
 * The occupied extent around `bin`: walk outward over the smoothed trace
 * while it stays above floor + margin, carrying on across dips shorter than
 * `bridgeBins`. Unlike a crest-following walk it does not stop at the first
 * rise, so one voice signal is one extent, not a row of fragments. Returns
 * inclusive [lo, hi] and the fraction of bins inside that are occupied.
 */
export function occupiedExtent(
  smooth: Float32Array,
  floor: Float32Array | null,
  fallbackFloorDb: number,
  bin: number,
  bridgeBins: number,
  radiusBins: number,
): { lo: number; hi: number; fill: number } {
  const n = smooth.length;
  const above = (i: number) => {
    const f = floor && floor.length === n && Number.isFinite(floor[i]!) ? floor[i]! : fallbackFloorDb;
    const v = smooth[i]!;
    return Number.isFinite(v) && v > f + OCCUPIED_MARGIN_DB;
  };
  const walk = (dir: 1 | -1): number => {
    let edge = bin;
    let gap = 0;
    for (let i = bin + dir, steps = 0; i >= 0 && i < n && steps < radiusBins; i += dir, steps++) {
      if (above(i)) {
        edge = i;
        gap = 0;
      } else if (++gap > bridgeBins) break;
    }
    return edge;
  };
  const lo = walk(-1);
  const hi = walk(1);
  let occ = 0;
  for (let i = lo; i <= hi; i++) if (above(i)) occ++;
  return { lo, hi, fill: occ / Math.max(1, hi - lo + 1) };
}

function medianDb(panDb: Float32Array): number {
  const v = Array.from(panDb).filter(Number.isFinite).sort((a, b) => a - b);
  return v.length > 0 ? v[Math.floor(v.length * 0.3)]! : -130;
}

export function observeSignals(panDb: Float32Array, centerHz: number, hzPerPixel: number): ObservedSignal[] {
  if (panDb.length < 8 || hzPerPixel <= 0) return [];
  const peaks = detectPeaks(panDb, centerHz, hzPerPixel, 0).filter((p) => p.snrDb >= MIN_SNR_DB);
  if (peaks.length === 0) return [];
  const floor = getNoiseFloor(0);
  const fallbackFloor = floor && floor.length === panDb.length ? 0 : medianDb(panDb);
  const stat = getSignalStationarity(0);
  const half = panDb.length / 2;
  const smooth = smoothDb(panDb, Math.round(SMOOTH_HZ / 2 / hzPerPixel));
  const bridge = Math.max(1, Math.round(BRIDGE_HZ / hzPerPixel));
  const radius = Math.max(2, Math.round(MAX_HALF_WIDTH_HZ / hzPerPixel));
  const taken: Array<[number, number]> = [];
  const out: ObservedSignal[] = [];
  for (const p of peaks) {
    if (out.length >= MAX_SIGNALS) break;
    const bin = Math.round((p.hz - centerHz) / hzPerPixel + half);
    if (bin < 0 || bin >= panDb.length) continue;
    if (taken.some(([lo, hi]) => bin >= lo && bin <= hi)) continue;
    const { lo, hi, fill } = occupiedExtent(smooth, floor, fallbackFloor, bin, bridge, radius);
    taken.push([lo, hi]);
    const s = stat && stat.length === panDb.length ? stat[bin]! : 0;
    out.push({
      // A narrow signal is where its crest is; a body is centred on its extent.
      hz: hi - lo <= 2 ? p.hz : centerHz + ((lo + hi) / 2 - half) * hzPerPixel,
      widthHz: (hi - lo + 1) * hzPerPixel,
      stationarity: Number.isFinite(s) ? s : 0,
      fill,
    });
  }
  return out;
}
