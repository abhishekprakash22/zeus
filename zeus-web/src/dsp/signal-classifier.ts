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
// Light signal classifier for the spectrum: a best guess at what an unlabelled
// signal is, from what the display already measures — occupied width, how
// steady its level is (the estimator's stationarity map), and how it comes
// and goes over the last ~12 s (keying). No demodulation; labels are hints,
// shown only once a guess has held for two updates in a row.
//
//   width ≥ 8 kHz                         → Wide   (OTH radar, broadband QRM)
//   narrow, inside an FT8 / FT4 window    → FT8 / FT4
//   narrow, always present, steady        → Carrier
//   narrow, keyed on and off              → CW
//   4–8 kHz, steady crest                 → AM
//   1.5–3.5 kHz                           → SSB

import { DIGITAL_BANDS } from './digital-segments';

export type SignalKind = 'carrier' | 'cw' | 'ssb' | 'am' | 'ft8' | 'ft4' | 'wide';

export const SIGNAL_KIND_LABEL: Record<SignalKind, string> = {
  carrier: 'Carrier',
  cw: 'CW',
  ssb: 'SSB',
  am: 'AM',
  ft8: 'FT8',
  ft4: 'FT4',
  wide: 'Wide',
};

export type ShapeFeatures = {
  widthHz: number;
  /** Display resolution, so "narrow" means a few bins at any zoom. */
  hzPerPixel: number;
  /** 0..1, 1 = level steady frame to frame. */
  stationarity: number;
  /** Fraction of recent updates the signal was present. */
  duty: number;
  /** Present↔absent changes over the window. */
  transitions: number;
  /** Updates observed so far. */
  samples: number;
  digitalWindow: 'FT8' | 'FT4' | null;
};

export const MIN_SAMPLES = 6;

/** The FT8 / FT4 audio window (dial .. dial + 3 kHz) a frequency falls in. */
export function digitalWindowAt(hz: number): 'FT8' | 'FT4' | null {
  for (const b of DIGITAL_BANDS) {
    if (hz >= b.ft8Hz && hz <= b.ft8Hz + 3_000) return 'FT8';
    if (b.ft4Hz != null && hz >= b.ft4Hz && hz <= b.ft4Hz + 3_000) return 'FT4';
  }
  return null;
}

export function classifyShape(f: ShapeFeatures): SignalKind | null {
  if (f.samples < MIN_SAMPLES) return null;
  const narrowMax = Math.max(150, 2.5 * f.hzPerPixel);
  if (f.widthHz >= 8_000) return 'wide';
  if (f.widthHz <= narrowMax) {
    if (f.digitalWindow === 'FT8') return 'ft8';
    if (f.digitalWindow === 'FT4') return 'ft4';
    if (f.duty >= 0.95 && f.transitions <= 1) return f.stationarity >= 0.5 ? 'carrier' : null;
    if (f.transitions >= 2 && f.duty >= 0.15) return 'cw';
    return null;
  }
  if (f.widthHz >= 4_000) return f.stationarity >= 0.5 ? 'am' : null;
  if (f.widthHz >= 1_500 && f.widthHz <= 3_500) return 'ssb';
  return null;
}

export type ObservedSignal = { hz: number; widthHz: number; stationarity: number };

type Track = {
  hz: number;
  widthHz: number;
  stationarity: number;
  history: boolean[];
  lastSeen: number;
  candidate: SignalKind | null;
  kind: SignalKind | null;
};

export type ClassifiedSignal = { hz: number; widthHz: number; kind: SignalKind };

const HISTORY = 24; // ~12 s at the overlay's 500 ms update
const DROP_AFTER_MS = 15_000;
const SHOW_FOR_MS = 2_500; // a keyed signal keeps its label across gaps

/** Follows signals across updates (by frequency) and keeps a stable label. */
export class SignalClassifier {
  private tracks: Track[] = [];

  reset(): void {
    this.tracks = [];
  }

  update(observed: readonly ObservedSignal[], nowMs: number, hzPerPixel: number): ClassifiedSignal[] {
    const matched = new Set<Track>();
    for (const o of observed) {
      const tol = Math.max(60, 2 * hzPerPixel, 0.3 * o.widthHz);
      let best: Track | null = null;
      let bestD = Infinity;
      for (const t of this.tracks) {
        if (matched.has(t)) continue;
        const d = Math.abs(t.hz - o.hz);
        if (d <= tol && d < bestD) {
          best = t;
          bestD = d;
        }
      }
      if (!best) {
        best = { hz: o.hz, widthHz: o.widthHz, stationarity: o.stationarity, history: [], lastSeen: nowMs, candidate: null, kind: null };
        this.tracks.push(best);
      } else {
        best.hz = 0.7 * best.hz + 0.3 * o.hz;
        best.widthHz = 0.6 * best.widthHz + 0.4 * o.widthHz;
        best.stationarity = 0.7 * best.stationarity + 0.3 * o.stationarity;
      }
      best.lastSeen = nowMs;
      best.history.push(true);
      matched.add(best);
    }
    for (const t of this.tracks) if (!matched.has(t)) t.history.push(false);
    for (const t of this.tracks) if (t.history.length > HISTORY) t.history.splice(0, t.history.length - HISTORY);
    this.tracks = this.tracks.filter((t) => nowMs - t.lastSeen <= DROP_AFTER_MS);

    const out: ClassifiedSignal[] = [];
    for (const t of this.tracks) {
      const present = t.history.filter(Boolean).length;
      let transitions = 0;
      for (let i = 1; i < t.history.length; i++) if (t.history[i] !== t.history[i - 1]) transitions++;
      const guess = classifyShape({
        widthHz: t.widthHz,
        hzPerPixel,
        stationarity: t.stationarity,
        duty: t.history.length > 0 ? present / t.history.length : 0,
        transitions,
        samples: t.history.length,
        digitalWindow: digitalWindowAt(t.hz),
      });
      // A label must hold for two updates in a row to appear or change.
      if (guess === t.candidate) t.kind = guess;
      t.candidate = guess;
      if (t.kind && nowMs - t.lastSeen <= SHOW_FOR_MS) out.push({ hz: t.hz, widthHz: t.widthHz, kind: t.kind });
    }
    return out;
  }
}
