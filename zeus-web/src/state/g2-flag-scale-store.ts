// SPDX-License-Identifier: GPL-2.0-or-later
//
// G2 receiver-flag scale (field request: the flag's text and mini S-meter
// were too small on the 8-inch panel). One scale for both flags, dragged from
// either flag's corner grip. Two vaults like the pane divider: localStorage
// for browsers with a durable profile, and the radio's /api/ui/flag-scale for
// the kiosk, whose browser profile is throwaway. The radio's copy wins at
// load, so remote browsers inherit the radio's size.

import { create } from 'zustand';

export const FLAG_SCALE_MIN = 1;
export const FLAG_SCALE_MAX = 2;
const LOCAL_KEY = 'zeus.g2.flagScale';

export function clampFlagScale(v: number): number {
  if (!Number.isFinite(v)) return FLAG_SCALE_MIN;
  return Math.min(FLAG_SCALE_MAX, Math.max(FLAG_SCALE_MIN, v));
}

function readLocal(): number {
  try {
    const raw = localStorage.getItem(LOCAL_KEY);
    return raw === null ? FLAG_SCALE_MIN : clampFlagScale(Number.parseFloat(raw));
  } catch {
    return FLAG_SCALE_MIN;
  }
}

type FlagScaleState = {
  scale: number;
  /** Live update while dragging; not persisted. */
  setScale: (v: number) => void;
  /** Persist the current scale (drag end). */
  commit: () => void;
  /** Pull the radio's copy once per page load. */
  seed: () => void;
};

let seeded = false;

export const useG2FlagScaleStore = create<FlagScaleState>((set, get) => ({
  scale: readLocal(),
  setScale: (v) => set({ scale: clampFlagScale(v) }),
  commit: () => {
    const scale = get().scale;
    try {
      localStorage.setItem(LOCAL_KEY, String(scale));
    } catch {
      // private mode / quota: the radio copy below still lands.
    }
    void fetch('/api/ui/flag-scale', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ scale }),
    }).catch(() => {});
  },
  seed: () => {
    if (seeded) return;
    seeded = true;
    void fetch('/api/ui/flag-scale')
      .then((r) => (r.ok ? r.json() : null))
      .then((body: { scale?: unknown } | null) => {
        const v = body?.scale;
        if (typeof v === 'number' && Number.isFinite(v)) set({ scale: clampFlagScale(v) });
      })
      .catch(() => {});
  },
}));
