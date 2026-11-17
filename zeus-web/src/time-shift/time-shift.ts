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
// Rewind — the browser side. Mirrors the radio's I/Q time-shift status,
// switches the buffer on and off, and drives a replay: start / move, pause,
// back to live. While a replay runs this renews its lease about once a
// second (the radio drops back to live on its own if the renewals stop).

import { create } from 'zustand';

export type TimeShiftStatus = {
  enabled: boolean;
  requestedSeconds: number;
  capacitySeconds: number;
  bufferedSeconds: number;
  rateHz: number;
  memoryBytes: number;
  replaying: boolean;
  paused: boolean;
  delaySeconds: number;
  outOfWindow: boolean;
  message: string | null;
};

export const EMPTY_STATUS: TimeShiftStatus = {
  enabled: false,
  requestedSeconds: 60,
  capacitySeconds: 0,
  bufferedSeconds: 0,
  rateHz: 0,
  memoryBytes: 0,
  replaying: false,
  paused: false,
  delaySeconds: 0,
  outOfWindow: false,
  message: null,
};

/** Buffer lengths offered in the UI (seconds). */
export const TIME_SHIFT_LENGTHS = [30, 60, 120, 300] as const;
/** Status refresh while the buffer is on but nothing is replaying. */
export const IDLE_POLL_MS = 2_000;
/** Lease renewal (and status refresh) while replaying. */
export const REPLAY_POLL_MS = 1_000;

export function parseStatus(raw: unknown): TimeShiftStatus {
  const o = raw && typeof raw === 'object' ? (raw as Record<string, unknown>) : {};
  const num = (k: string, d = 0) => (typeof o[k] === 'number' && Number.isFinite(o[k] as number) ? (o[k] as number) : d);
  const bool = (k: string) => o[k] === true;
  return {
    enabled: bool('enabled'),
    requestedSeconds: num('requestedSeconds', 60),
    capacitySeconds: num('capacitySeconds'),
    bufferedSeconds: num('bufferedSeconds'),
    rateHz: num('rateHz'),
    memoryBytes: num('memoryBytes'),
    replaying: bool('replaying'),
    paused: bool('paused'),
    delaySeconds: num('delaySeconds'),
    outOfWindow: bool('outOfWindow'),
    message: typeof o.message === 'string' ? o.message : null,
  };
}

type State = {
  status: TimeShiftStatus;
  hydrated: boolean;
  hydrate: () => Promise<void>;
  configure: (enabled: boolean, seconds?: number) => Promise<void>;
  /** Start a replay this many seconds back, or move a running one. */
  replay: (delaySeconds: number) => Promise<void>;
  /** Step a running replay further back (+) or toward live (−), or start one. */
  nudge: (deltaSeconds: number) => Promise<void>;
  pause: (paused: boolean) => Promise<void>;
  live: () => Promise<void>;
};

async function call(path: string, body?: unknown): Promise<TimeShiftStatus | null> {
  try {
    const res = await fetch(path, {
      method: body === undefined && path === '/api/time-shift' ? 'GET' : 'POST',
      headers: body === undefined ? undefined : { 'content-type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!res.ok) return null;
    return parseStatus(await res.json());
  } catch {
    return null;
  }
}

let pollTimer: ReturnType<typeof setTimeout> | null = null;
let hydrating: Promise<void> | null = null;

function apply(status: TimeShiftStatus | null): void {
  if (status) useTimeShiftStore.setState({ status, hydrated: true });
  schedulePoll();
}

function schedulePoll(): void {
  if (pollTimer) clearTimeout(pollTimer);
  pollTimer = null;
  const s = useTimeShiftStore.getState().status;
  if (!s.enabled) return;
  pollTimer = setTimeout(
    () => {
      pollTimer = null;
      const now = useTimeShiftStore.getState().status;
      void call(now.replaying ? '/api/time-shift/renew' : '/api/time-shift').then(apply);
    },
    s.replaying ? REPLAY_POLL_MS : IDLE_POLL_MS,
  );
}

export const useTimeShiftStore = create<State>((set, get) => ({
  status: EMPTY_STATUS,
  hydrated: false,
  hydrate: () => {
    if (get().hydrated) return Promise.resolve();
    hydrating ??= call('/api/time-shift')
      .then((s) => {
        if (s) set({ status: s, hydrated: true });
        schedulePoll();
      })
      .finally(() => {
        hydrating = null;
      });
    return hydrating;
  },
  configure: async (enabled, seconds) => {
    const cur = get().status;
    const body = { enabled, seconds: seconds ?? cur.requestedSeconds };
    // Reflect the switch at once; the radio's answer follows.
    set({ status: { ...cur, enabled, requestedSeconds: body.seconds, replaying: enabled && cur.replaying } });
    apply(await call('/api/time-shift/config', body));
  },
  replay: async (delaySeconds) => {
    apply(await call('/api/time-shift/replay', { delaySeconds: Math.max(0, delaySeconds) }));
  },
  nudge: async (deltaSeconds) => {
    const s = get().status;
    const from = s.replaying ? s.delaySeconds : 0;
    const to = Math.min(Math.max(0, from + deltaSeconds), Math.max(0, s.bufferedSeconds));
    if (to <= 0.05) await get().live();
    else await get().replay(to);
  },
  pause: async (paused) => {
    apply(await call('/api/time-shift/pause', { paused }));
  },
  live: async () => {
    apply(await call('/api/time-shift/live', {}));
  },
}));

/** "−23.4 s" for the readout; "LIVE" at zero. */
export function formatDelay(seconds: number): string {
  if (!(seconds > 0.05)) return 'LIVE';
  if (seconds >= 60) {
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds - m * 60);
    return `−${m}:${String(s).padStart(2, '0')}`;
  }
  return `−${seconds.toFixed(1)} s`;
}

/** Test hook. */
export function _resetTimeShiftForTest(): void {
  if (pollTimer) clearTimeout(pollTimer);
  pollTimer = null;
  hydrating = null;
  useTimeShiftStore.setState({ status: EMPTY_STATUS, hydrated: false });
}
