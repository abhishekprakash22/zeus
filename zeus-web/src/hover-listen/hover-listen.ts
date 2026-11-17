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
// Hover to listen — the browser side. The spectrum gesture hook reports where
// the pointer is aiming (mouse hover, or the touch tuning loupe); after a
// short dwell this asks the radio to preview that frequency and keeps the
// request alive while the pointer stays. Pointer positions go to the radio at
// most every SEND_MS (so a remote link carries a few small requests a second,
// never a stream), and the radio's lease lapses on its own if they stop.

import { create } from 'zustand';

/** Pointer must stay on the spectrum this long before audio starts. */
export const DWELL_MS = 250;
/** Fastest the aim is sent to the radio while the pointer moves. */
export const SEND_MS = 200;
/** A stationary pointer renews the radio's 1.5 s lease this often. */
export const KEEPALIVE_MS = 600;

type State = {
  /** Operator's on/off choice (kept on the radio). Off by default. */
  enabled: boolean;
  hydrated: boolean;
  /** Frequency being previewed right now, or null. */
  activeHz: number | null;
  /** Why the last aim can't be heard (e.g. outside the receiver's window). */
  reason: string | null;
  hydrate: () => Promise<void>;
  setEnabled: (on: boolean) => void;
};

let hydrating: Promise<void> | null = null;

export const useHoverListenStore = create<State>((set, get) => ({
  enabled: false,
  hydrated: false,
  activeHz: null,
  reason: null,
  hydrate: () => {
    if (get().hydrated) return Promise.resolve();
    hydrating ??= fetch('/api/ui/hover-listen')
      .then(async (r) => {
        if (!r.ok) return;
        const body = (await r.json()) as { enabled?: unknown };
        set({ enabled: body.enabled === true, hydrated: true });
      })
      .catch(() => {})
      .finally(() => {
        hydrating = null;
      });
    return hydrating;
  },
  setEnabled: (on) => {
    set({ enabled: on, hydrated: true });
    if (!on) hoverListenStop();
    void fetch('/api/ui/hover-listen', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ enabled: on }),
    }).catch(() => {});
  },
}));

let aimHz: number | null = null;
let engaged = false;
let dwellTimer: ReturnType<typeof setTimeout> | null = null;
let sendTimer: ReturnType<typeof setTimeout> | null = null;
let keepTimer: ReturnType<typeof setInterval> | null = null;
let lastSentAt = 0;
let generation = 0;

function clearTimers(): void {
  if (dwellTimer) clearTimeout(dwellTimer);
  if (sendTimer) clearTimeout(sendTimer);
  if (keepTimer) clearInterval(keepTimer);
  dwellTimer = null;
  sendTimer = null;
  keepTimer = null;
}

function send(): void {
  sendTimer = null;
  const hz = aimHz;
  if (!engaged || hz == null) return;
  lastSentAt = Date.now();
  const gen = generation;
  void fetch('/api/hover-preview', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ hz: Math.round(hz) }),
  })
    .then(async (r) => {
      if (gen !== generation) return; // stopped (or restarted) since this was sent
      if (!r.ok) {
        useHoverListenStore.setState({ activeHz: null, reason: null });
        return;
      }
      const body = (await r.json()) as { active?: unknown; reason?: unknown };
      if (gen !== generation) return;
      useHoverListenStore.setState({
        activeHz: body.active === true ? hz : null,
        reason: typeof body.reason === 'string' ? body.reason : null,
      });
    })
    .catch(() => {
      if (gen === generation) useHoverListenStore.setState({ activeHz: null, reason: null });
    });
}

function engage(): void {
  dwellTimer = null;
  if (aimHz == null) return;
  engaged = true;
  send();
  keepTimer = setInterval(() => {
    if (Date.now() - lastSentAt >= KEEPALIVE_MS - 50) send();
  }, KEEPALIVE_MS);
}

/**
 * The pointer is aiming at `hz`. `immediate` skips the dwell (the touch loupe
 * is already a deliberate press-and-hold).
 */
export function hoverListenAim(hz: number, immediate = false): void {
  if (!useHoverListenStore.getState().enabled || !Number.isFinite(hz) || hz <= 0) return;
  const moved = aimHz == null || Math.abs(aimHz - hz) >= 1;
  aimHz = hz;
  if (!engaged) {
    if (immediate) {
      if (dwellTimer) clearTimeout(dwellTimer);
      engage();
    } else {
      dwellTimer ??= setTimeout(engage, DWELL_MS);
    }
    return;
  }
  if (!moved || sendTimer) return;
  const wait = SEND_MS - (Date.now() - lastSentAt);
  if (wait <= 0) send();
  else sendTimer = setTimeout(send, wait);
}

/** The pointer left, pressed, or the gesture ended: stop the preview now. */
export function hoverListenStop(): void {
  const wasEngaged = engaged;
  clearTimers();
  aimHz = null;
  engaged = false;
  generation += 1;
  if (useHoverListenStore.getState().activeHz != null || useHoverListenStore.getState().reason != null) {
    useHoverListenStore.setState({ activeHz: null, reason: null });
  }
  if (wasEngaged) {
    void fetch('/api/hover-preview/stop', { method: 'POST' }).catch(() => {});
  }
}

/** Test hook. */
export function _resetHoverListenForTest(): void {
  clearTimers();
  aimHz = null;
  engaged = false;
  lastSentAt = 0;
  generation += 1;
  useHoverListenStore.setState({ enabled: false, hydrated: false, activeHz: null, reason: null });
}
