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

// Memory channels in the browser: a mirror of the radio-side list, hydrated on
// first use and replaced by every server response (the server is the truth,
// so two clients storing at once converge on the next read).

import { create } from 'zustand';
import { deleteMemory, fetchMemories, postMemory, type MemoryChannel } from '../api/memories';
import { setFilter, setMode, setVfo, type RadioStateDto, type RxMode } from '../api/client';
import { useConnectionStore } from './connection-store';
import { useVfoLockStore } from './vfo-lock-store';

type MemoryState = {
  channels: MemoryChannel[];
  hydrated: boolean;
  hydrate: () => Promise<void>;
  store: (m: { name?: string; hz: number; mode: RxMode; filterLowHz?: number | null; filterHighHz?: number | null }) => Promise<boolean>;
  remove: (id: string) => Promise<boolean>;
};

let inflight: Promise<void> | null = null;

export const useMemoryStore = create<MemoryState>((set, get) => ({
  channels: [],
  hydrated: false,
  hydrate: () => {
    if (get().hydrated) return Promise.resolve();
    inflight ??= fetchMemories()
      .then((channels) => set({ channels, hydrated: true }))
      .catch(() => {
        /* offline / older radio: stay empty, retry on next use */
      })
      .finally(() => {
        inflight = null;
      });
    return inflight;
  },
  store: async (m) => {
    try {
      set({ channels: await postMemory(m), hydrated: true });
      return true;
    } catch {
      return false;
    }
  },
  remove: async (id) => {
    try {
      set({ channels: await deleteMemory(id), hydrated: true });
      return true;
    } catch {
      return false;
    }
  },
}));

/** The memory channel drawn within `radiusHz` of hz (nearest), if any. */
export function memoryNear(channels: readonly MemoryChannel[], hz: number, radiusHz: number): MemoryChannel | null {
  let best: MemoryChannel | null = null;
  let bestD = Infinity;
  for (const m of channels) {
    const d = Math.abs(m.hz - hz);
    if (d <= radiusHz && d < bestD) {
      best = m;
      bestD = d;
    }
  }
  return best;
}

function apply(s: RadioStateDto): void {
  useConnectionStore.getState().applyState(s);
}

/** Recall a channel onto RX1: mode first (it resets the filter), then the
 *  stored filter, then the dial. */
export async function recallMemory(m: MemoryChannel): Promise<void> {
  if (useVfoLockStore.getState().locked) return;
  try {
    if (useConnectionStore.getState().mode !== m.mode) apply(await setMode(m.mode));
    if (m.filterLowHz != null && m.filterHighHz != null) apply(await setFilter(m.filterLowHz, m.filterHighHz));
    useConnectionStore.setState({ vfoHz: m.hz });
    apply(await setVfo(m.hz));
  } catch {
    /* the next state frame reconciles */
  }
}
