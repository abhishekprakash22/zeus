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
// Decodes on the spectrum — the model. Builds one list of callsign tags at
// RF frequencies from the three sources the radio already produces (FT8/FT4
// decodes, the CW skimmer's channels, cluster/TCI spots), merges a call heard
// on more than one, and lays the tags out in lanes so neighbours never
// overlap. Pure; the overlay renders what this returns.

import type { Ft8Row, Ft8SlotMark } from '../state/ft8-store';
import type { CwSkimChannel } from '../state/cw-skim-store';
import type { SpotData } from '../state/spot-store';
import { tryParseSender } from '../dsp/ft8-sender';

export type TagSource = 'ft8' | 'cw' | 'spot';

/** Logbook status from the radio (POST /api/log/spectrum-status). */
export type CallStatus = 'NewDxcc' | 'NewBand' | 'Needed' | 'Worked';

export type DecodeTag = {
  /** Stable React key. */
  key: string;
  call: string;
  hz: number;
  sources: TagSource[];
  /** FT8: the sender is calling CQ. */
  cq: boolean;
  /** FT8: the message is addressed to my call. */
  me: boolean;
  /** Text for the tooltip (last FT8 message, CW text tail, spot comment). */
  detail: string;
  /** Newest evidence time (ms). */
  seenAt: number;
};

/** How long FT8/FT4 decodes stay on the spectrum: two slots. */
export const FT8_TAG_WINDOW_MS = 30_000;
export const FT4_TAG_WINDOW_MS = 15_000;
/** A skimmer channel's call shows while the channel is decoding, and this
 *  long after its last character. */
export const CW_TAG_HOLD_MS = 20_000;

const CALL_RE = /^(?:[A-Z0-9]{1,4}\/)?(?:[A-Z]{1,2}|[0-9][A-Z]|[A-Z][0-9])[0-9]{1,2}[A-Z]{1,4}(?:\/[A-Z0-9]{1,4})?$/;

/** A plausible amateur callsign: prefix, digit, 1-4 letter suffix, optional
 *  /prefix or /suffix. Rejects grids (FN42), reports (5NN, 599) and prosigns. */
export function isCallsign(token: string): boolean {
  const t = token.toUpperCase();
  if (t.length < 3 || t.length > 15) return false;
  return CALL_RE.test(t);
}

/**
 * The callsign a CW stream identifies, from its recent text: the last call
 * after DE, else the call after a CQ run, else a call sent at least twice.
 * Garbled copy rarely forms the same call twice, so a single stray match
 * that is neither after DE nor after CQ is ignored.
 */
export function extractCwCallsign(text: string): string | null {
  const tail = text.toUpperCase().slice(-160);
  const tokens = tail.split(/[^A-Z0-9/]+/).filter((t) => t.length > 0);
  let afterDe: string | null = null;
  let afterCq: string | null = null;
  const counts = new Map<string, number>();
  for (let i = 0; i < tokens.length; i++) {
    const t = tokens[i]!;
    if (!isCallsign(t)) continue;
    counts.set(t, (counts.get(t) ?? 0) + 1);
    const prev = tokens[i - 1];
    if (prev === 'DE') afterDe = t;
    else if (prev === 'CQ' || prev === 'TEST') afterCq = t;
  }
  if (afterDe) return afterDe;
  if (afterCq) return afterCq;
  let best: string | null = null;
  let bestN = 1;
  for (const [c, n] of counts) {
    if (n > bestN) {
      best = c;
      bestN = n;
    }
  }
  return best;
}

/** FT8/FT4 decodes as tags: sender at slot dial + audio offset, newest per call. */
export function ft8Tags(
  rows: readonly Ft8Row[],
  slots: readonly Ft8SlotMark[],
  nowMs: number,
  myCall: string | null | undefined,
  receiver = 0,
): DecodeTag[] {
  const dialBySlot = new Map<string, number>();
  for (const s of slots) dialBySlot.set(`${s.receiver}|${s.slotStartUnixMs}`, s.dialHz);
  const me = myCall?.trim().toUpperCase() || null;
  const out = new Map<string, DecodeTag>();
  for (const r of rows) {
    if (r.receiver !== receiver) continue;
    const window = r.protocol === 'FT4' ? FT4_TAG_WINDOW_MS : FT8_TAG_WINDOW_MS;
    if (nowMs - r.slotStartUnixMs > window) continue;
    const dial = dialBySlot.get(`${r.receiver}|${r.slotStartUnixMs}`) ?? 0;
    if (dial <= 0) continue;
    const sender = tryParseSender(r.text);
    if (!sender) continue;
    const call = sender.call.toUpperCase();
    const first = r.text.trim().split(/\s+/)[0]?.toUpperCase() ?? '';
    const prev = out.get(call);
    if (prev && prev.seenAt >= r.slotStartUnixMs) continue;
    out.set(call, {
      key: `ft8:${call}`,
      call,
      hz: dial + r.freqHz,
      sources: ['ft8'],
      cq: first === 'CQ',
      me: me != null && first === me,
      detail: `${r.protocol} ${r.snrDb >= 0 ? '+' : ''}${r.snrDb} dB  ${r.text}`,
      seenAt: r.slotStartUnixMs,
    });
  }
  return Array.from(out.values());
}

/** Skimmer pitch → absolute RF, from the receiver's mode, dial and CW pitch. */
export function skimPitchToHz(mode: string, vfoHz: number, cwPitchHz: number, pitchHz: number): number | null {
  switch (mode) {
    case 'CWU':
      return vfoHz + (pitchHz - cwPitchHz);
    case 'CWL':
      return vfoHz + (cwPitchHz - pitchHz);
    case 'USB':
    case 'DIGU':
      return vfoHz + pitchHz;
    case 'LSB':
    case 'DIGL':
      return vfoHz - pitchHz;
    default:
      return null;
  }
}

/** CW skimmer channels that have identified a call. */
export function cwTags(
  channels: Readonly<Record<number, CwSkimChannel>>,
  nowMs: number,
  mode: string,
  vfoHz: number,
  cwPitchHz: number,
): DecodeTag[] {
  const out = new Map<string, DecodeTag>();
  for (const ch of Object.values(channels)) {
    if (!ch.active && nowMs - ch.lastCharAt > CW_TAG_HOLD_MS) continue;
    const call = extractCwCallsign(ch.text);
    if (!call) continue;
    const hz = skimPitchToHz(mode, vfoHz, cwPitchHz, ch.pitchHz);
    if (hz == null) continue;
    const prev = out.get(call);
    if (prev && prev.seenAt >= ch.lastCharAt) continue;
    out.set(call, {
      key: `cw:${call}`,
      call,
      hz,
      sources: ['cw'],
      cq: /\bCQ\b/.test(ch.text.toUpperCase().slice(-80)),
      me: false,
      detail: `CW ${Math.round(ch.snrDb)} dB  …${ch.text.slice(-40).trim()}`,
      seenAt: ch.lastCharAt,
    });
  }
  return Array.from(out.values());
}

/** Cluster / TCI spots as tags. */
export function spotTags(spots: readonly SpotData[], nowMs: number): DecodeTag[] {
  const out: DecodeTag[] = [];
  for (const s of spots) {
    const call = s.callsign.trim().toUpperCase();
    if (!call) continue;
    out.push({
      key: `spot:${call}:${s.freqHz}`,
      call,
      hz: s.freqHz,
      sources: ['spot'],
      cq: false,
      me: false,
      detail: `Spot ${s.mode}${s.comment ? ` — ${s.comment}` : ''}`,
      seenAt: nowMs,
    });
  }
  return out;
}

/** One tag per call heard within MERGE_HZ: the decoded source's frequency
 *  wins over a spot's (a decode is where the signal is now), sources merge. */
export const MERGE_HZ = 1_000;

export function mergeTags(...lists: DecodeTag[][]): DecodeTag[] {
  const merged: DecodeTag[] = [];
  for (const list of lists) {
    for (const t of list) {
      const same = merged.find((m) => m.call === t.call && Math.abs(m.hz - t.hz) <= MERGE_HZ);
      if (!same) {
        merged.push({ ...t, sources: [...t.sources] });
        continue;
      }
      for (const s of t.sources) if (!same.sources.includes(s)) same.sources.push(s);
      same.cq ||= t.cq;
      same.me ||= t.me;
      // Keep the decoded position over a spot's; else the newer one.
      const decoded = (x: DecodeTag) => x.sources.some((s) => s !== 'spot');
      if (!decoded(same) || (decoded(t) && t.seenAt > same.seenAt)) {
        same.hz = t.hz;
        same.detail = t.detail;
        same.seenAt = Math.max(same.seenAt, t.seenAt);
      }
    }
  }
  return merged;
}

/** Draw order / survival order when lanes are full: the most useful first. */
export function tagPriority(t: DecodeTag, status: CallStatus | null | undefined): number {
  if (t.me) return 0;
  switch (status) {
    case 'NewDxcc':
      return 1;
    case 'NewBand':
      return 2;
    case 'Needed':
      return t.cq ? 3 : 4;
    case 'Worked':
      return 6;
    default:
      return 5;
  }
}

export type PlacedLabel<T> = { item: T; x: number; lane: number; width: number };

/**
 * Greedy lane layout: items are placed in priority order into the first lane
 * where their box clears every box already there (with a gap). Items that fit
 * no lane are dropped — on a crowded band the lower-priority labels give way.
 */
export function layoutLanes<T>(
  items: readonly { item: T; x: number; width: number; priority: number }[],
  lanes: number,
  gapPx = 4,
): PlacedLabel<T>[] {
  const order = [...items].sort((a, b) => a.priority - b.priority || a.x - b.x);
  const occupied: Array<Array<[number, number]>> = Array.from({ length: lanes }, () => []);
  const placed: PlacedLabel<T>[] = [];
  for (const it of order) {
    const lo = it.x - it.width / 2 - gapPx / 2;
    const hi = it.x + it.width / 2 + gapPx / 2;
    for (let lane = 0; lane < lanes; lane++) {
      const row = occupied[lane]!;
      if (row.every(([a, b]) => hi <= a || lo >= b)) {
        row.push([lo, hi]);
        placed.push({ item: it.item, x: it.x, lane, width: it.width });
        break;
      }
    }
  }
  return placed;
}
