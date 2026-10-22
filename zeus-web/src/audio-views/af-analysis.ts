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
// AF analysis feed (MsgType 0x3E, Zeus.Server.Hosting/AfAnalysisService.cs):
// the radio analyses each receiver's final audio (and the TX mic while keyed)
// and sends a 128-bin 0..4 kHz spectrum plus a 512-sample scope snapshot. This
// module decodes the frame, keeps the latest per source, and holds the lease
// (POST /api/audio-analysis/subscribe every 2 s) while any card needs it.

import { create } from 'zustand';
import { useEffect } from 'react';

export const MSG_TYPE_AF_ANALYSIS = 0x3e;
export const AF_TX_SOURCE = 0xff;
const HEADER_BYTES = 8;

export type AfFrame = {
  source: number;
  muted: boolean;
  rateHz: number;
  /** 0..255 = −120..0 dBFS, bins evenly spaced over 0..rateHz/2. */
  bins: Uint8Array;
  /** −127..127 = −1..+1, oldest first. */
  scope: Int8Array;
  rmsDbfs: number;
  peakDbfs: number;
  /** performance.now() when received. */
  at: number;
};

/** Decode one 0x3E frame (null when malformed). */
export function parseAfFrame(buf: ArrayBuffer, now = performance.now()): AfFrame | null {
  if (buf.byteLength < HEADER_BYTES) return null;
  const dv = new DataView(buf);
  if (dv.getUint8(0) !== MSG_TYPE_AF_ANALYSIS) return null;
  const binCount = dv.getUint8(5);
  const scopeCount = dv.getUint16(6, true);
  const need = HEADER_BYTES + binCount + scopeCount + 2;
  if (buf.byteLength < need) return null;
  return {
    source: dv.getUint8(1),
    muted: (dv.getUint8(2) & 1) !== 0,
    rateHz: dv.getUint16(3, true),
    bins: new Uint8Array(buf.slice(HEADER_BYTES, HEADER_BYTES + binCount)),
    scope: new Int8Array(buf.slice(HEADER_BYTES + binCount, HEADER_BYTES + binCount + scopeCount)),
    rmsDbfs: dv.getInt8(HEADER_BYTES + binCount + scopeCount),
    peakDbfs: dv.getInt8(HEADER_BYTES + binCount + scopeCount + 1),
    at: now,
  };
}

/** Byte → dBFS (inverse of the server's mapping). */
export const binDb = (b: number): number => (b / 255) * 120 - 120;

type State = {
  frames: Record<number, AfFrame>;
  set: (f: AfFrame) => void;
};

export const useAfAnalysisStore = create<State>((set) => ({
  frames: {},
  set: (f) => set((s) => ({ frames: { ...s.frames, [f.source]: f } })),
}));

export function handleAfAnalysisFrame(buf: ArrayBuffer): void {
  const f = parseAfFrame(buf);
  if (f) useAfAnalysisStore.getState().set(f);
}

/**
 * The peak of a spectrum as a frequency, with parabolic interpolation between
 * bins (≈ ±3 Hz at 31.25 Hz/bin) — the CW pitch view's measurement. Only bins
 * between loHz and hiHz are searched; null when the peak is not at least
 * `minRiseDb` above the median (no clear tone).
 */
export function peakFrequencyHz(f: AfFrame, loHz: number, hiHz: number, minRiseDb = 15): number | null {
  const n = f.bins.length;
  const hzPerBin = f.rateHz / 2 / n;
  const lo = Math.max(1, Math.floor(loHz / hzPerBin));
  const hi = Math.min(n - 2, Math.ceil(hiHz / hzPerBin));
  if (hi <= lo) return null;
  let k = lo;
  for (let i = lo; i <= hi; i++) if (f.bins[i]! > f.bins[k]!) k = i;
  const sorted = Array.from(f.bins.slice(lo, hi + 1)).sort((a, b) => a - b);
  const median = sorted[Math.floor(sorted.length / 2)]!;
  if (binDb(f.bins[k]!) - binDb(median) < minRiseDb) return null;
  const a = binDb(f.bins[k - 1]!);
  const b = binDb(f.bins[k]!);
  const c = binDb(f.bins[k + 1]!);
  const denom = a - 2 * b + c;
  const delta = denom === 0 ? 0 : (0.5 * (a - c)) / denom;
  return (k + Math.max(-0.5, Math.min(0.5, delta))) * hzPerBin;
}

// ---- Lease ----------------------------------------------------------------
// Ref-counted across cards: one POST every 2 s covers every source any mounted
// audio view currently needs; none needed → no POSTs, and the radio's leases
// lapse after 6 s.
const wanted = new Map<number, number>();
let timer: ReturnType<typeof setInterval> | null = null;

function renew(): void {
  const sources = [...wanted.entries()].filter(([, n]) => n > 0).map(([s]) => s);
  if (sources.length === 0) return;
  void fetch('/api/audio-analysis/subscribe', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ sources }),
  }).catch(() => {});
}

export function acquireAfLease(source: number): () => void {
  wanted.set(source, (wanted.get(source) ?? 0) + 1);
  renew();
  if (!timer) timer = setInterval(renew, 2000);
  return () => {
    const n = (wanted.get(source) ?? 1) - 1;
    if (n <= 0) wanted.delete(source);
    else wanted.set(source, n);
    if (wanted.size === 0 && timer) {
      clearInterval(timer);
      timer = null;
    }
  };
}

/** Hold the lease for `source` while `active`. */
export function useAfLease(source: number, active: boolean): void {
  useEffect(() => {
    if (!active) return;
    return acquireAfLease(source);
  }, [source, active]);
}
