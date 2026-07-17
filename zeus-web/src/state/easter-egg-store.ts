// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
// See LICENSE for the full GPL text.

import { create } from 'zustand';

// Hidden HARDWARE diagnostics surface and QRM test tools.
//
// The HARDWARE settings folder (board/DSP diagnostics) is hidden by default
// and unlocked as an easter egg: tapping the header brand-mark lightning bolt
// HARDWARE_UNLOCK_CLICKS times reveals it and opens the hidden TX testing tools
// popout. State is intentionally in-memory only — it re-locks on every launch,
// so a fresh session never lists the folder and an accidental discovery never
// permanently exposes it.

export const HARDWARE_UNLOCK_CLICKS = 4;

type EasterEggState = {
  hardwareUnlocked: boolean;
  signalJammerPopoverOpen: boolean;
  boltClicks: number;
  // Counts a brand-mark bolt tap; unlocks HARDWARE once the click threshold is
  // reached. Once unlocked, the bolt toggles the TX testing tools popout.
  registerBoltClick: () => void;
  closeSignalJammerPopover: () => void;
  toggleSignalJammerPopover: () => void;
};

export const useEasterEggStore = create<EasterEggState>((set) => ({
  hardwareUnlocked: false,
  signalJammerPopoverOpen: false,
  boltClicks: 0,
  registerBoltClick: () =>
    set((s) => {
      if (s.hardwareUnlocked) {
        return { signalJammerPopoverOpen: !s.signalJammerPopoverOpen };
      }
      const boltClicks = s.boltClicks + 1;
      return boltClicks >= HARDWARE_UNLOCK_CLICKS
        ? { boltClicks: 0, hardwareUnlocked: true, signalJammerPopoverOpen: true }
        : { boltClicks };
    }),
  closeSignalJammerPopover: () => set({ signalJammerPopoverOpen: false }),
  toggleSignalJammerPopover: () =>
    set((s) => ({ signalJammerPopoverOpen: !s.signalJammerPopoverOpen })),
}));
