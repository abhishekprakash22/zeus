// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// VFO lock — client-only flag that suppresses outbound `setVfo` calls so a
// user can pin the radio on a frequency without accidental retunes from
// touch gestures, scrolls, or band picks. Lives in its own store so
// `api/client.ts` (which has no dependency on `connection-store`) can read
// the gate without introducing a circular import.

import { create } from 'zustand';

export type VfoLockState = {
  locked: boolean;
  toggle: () => void;
  setLocked: (locked: boolean) => void;
  // Display-levels lock (mobile field request): the dB scales on the
  // panadapter and waterfall are vertical-drag gain shifters, and on a
  // phone a scroll that starts over either scale re-levels the display by
  // accident. Independent of the VFO lock so tuning stays live while the
  // levels are pinned. Persisted; the mobile shell seeds it ON the first
  // time it mounts, desktop never reads it unless a button is wired.
  levelsLocked: boolean;
  toggleLevels: () => void;
  setLevelsLocked: (locked: boolean) => void;
};

const LEVELS_KEY = 'zeus.display.levelsLock';
function readLevelsLock(): boolean | null {
  try {
    const v = localStorage.getItem(LEVELS_KEY);
    return v == null ? null : v === '1';
  } catch { return null; }
}
function writeLevelsLock(on: boolean): void {
  try { localStorage.setItem(LEVELS_KEY, on ? '1' : '0'); } catch { /* private mode */ }
}
/** True when the operator has never set the levels lock (mobile seeds ON). */
export function levelsLockUnset(): boolean { return readLevelsLock() == null; }

export const useVfoLockStore = create<VfoLockState>((set) => ({
  locked: false,
  toggle: () => set((s) => ({ locked: !s.locked })),
  setLocked: (locked) => set({ locked }),
  levelsLocked: readLevelsLock() ?? false,
  toggleLevels: () => set((s) => { writeLevelsLock(!s.levelsLocked); return { levelsLocked: !s.levelsLocked }; }),
  setLevelsLocked: (locked) => { writeLevelsLock(locked); set({ levelsLocked: locked }); },
}));
