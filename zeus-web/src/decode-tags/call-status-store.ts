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
// Logbook status for the calls on the spectrum, asked of the radio in
// batches (POST /api/log/spectrum-status) and cached per call and band for a
// minute. The overlay asks for everything it shows; only calls not cached
// (or stale) go over the wire, at most every BATCH_MS.

import { create } from 'zustand';
import { freqHzToBand } from '../state/spots-store';
import type { CallStatus } from './decode-tags';

export type CallStatusInfo = {
  status: CallStatus | null;
  entity: string | null;
  workedAnyBand: boolean;
  at: number;
};

export const STATUS_TTL_MS = 60_000;
const BATCH_MS = 400;
const MAX_BATCH = 200;

export function statusKey(call: string, hz: number): string {
  return `${call.toUpperCase()}|${freqHzToBand(hz) ?? Math.round(hz / 1e6)}`;
}

type State = {
  byKey: Record<string, CallStatusInfo>;
};

export const useCallStatusStore = create<State>(() => ({ byKey: {} }));

const pending = new Map<string, { call: string; hz: number }>();
const inflight = new Set<string>();
let timer: ReturnType<typeof setTimeout> | null = null;

const STATUSES: readonly CallStatus[] = ['NewDxcc', 'NewBand', 'Needed', 'Worked'];

/** Parse one server item; junk yields null. */
export function parseStatusItem(raw: unknown): { call: string; freqHz: number; info: Omit<CallStatusInfo, 'at'> } | null {
  if (!raw || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  if (typeof o.call !== 'string' || typeof o.freqHz !== 'number') return null;
  const status = typeof o.status === 'string' && (STATUSES as readonly string[]).includes(o.status) ? (o.status as CallStatus) : null;
  return {
    call: o.call,
    freqHz: o.freqHz,
    info: {
      status,
      entity: typeof o.entity === 'string' ? o.entity : null,
      workedAnyBand: o.workedAnyBand === true,
    },
  };
}

async function flush(): Promise<void> {
  timer = null;
  const batch = Array.from(pending.entries()).slice(0, MAX_BATCH);
  for (const [k] of batch) {
    pending.delete(k);
    inflight.add(k);
  }
  if (pending.size > 0) schedule();
  if (batch.length === 0) return;
  try {
    const res = await fetch('/api/log/spectrum-status', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ items: batch.map(([, v]) => ({ call: v.call, freqHz: Math.round(v.hz) })) }),
    });
    if (!res.ok) return;
    const body = (await res.json()) as { items?: unknown };
    if (!Array.isArray(body.items)) return;
    const now = Date.now();
    const next: Record<string, CallStatusInfo> = {};
    // The radio answers under the call as asked (it drops anything that is
    // not a call), so each answer keys straight back into the cache.
    for (const raw of body.items) {
      const p = parseStatusItem(raw);
      if (p) next[statusKey(p.call, p.freqHz)] = { ...p.info, at: now };
    }
    useCallStatusStore.setState((s) => ({ byKey: { ...s.byKey, ...next } }));
  } catch {
    /* radio unreachable: tags stay neutral, retried on the next request */
  } finally {
    for (const [k] of batch) inflight.delete(k);
  }
}

function schedule(): void {
  timer ??= setTimeout(() => void flush(), BATCH_MS);
}

/** Ask for the status of these calls (only uncached / stale ones are sent). */
export function requestCallStatus(items: readonly { call: string; hz: number }[], nowMs = Date.now()): void {
  const cache = useCallStatusStore.getState().byKey;
  for (const it of items) {
    const k = statusKey(it.call, it.hz);
    const c = cache[k];
    if (c && nowMs - c.at < STATUS_TTL_MS) continue;
    if (inflight.has(k) || pending.has(k)) continue;
    pending.set(k, { call: it.call, hz: it.hz });
  }
  if (pending.size > 0) schedule();
}

export function statusFor(byKey: Record<string, CallStatusInfo>, call: string, hz: number): CallStatusInfo | null {
  return byKey[statusKey(call, hz)] ?? null;
}

/** Test hook. */
export function _resetCallStatusForTest(): void {
  pending.clear();
  inflight.clear();
  if (timer) clearTimeout(timer);
  timer = null;
  useCallStatusStore.setState({ byKey: {} });
}
