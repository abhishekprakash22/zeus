// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Neural CW decoder state (DeepCW). The controller feeds the worker from the
// RX audio bus; decoded characters land here. `enabled` gates the whole
// pipeline (worker not even spawned while off) — the user-facing on/off.

import { create } from 'zustand';

export type CwDecoderStatus = 'off' | 'loading' | 'running' | 'error';

export interface CwRecentChar {
  id: number;
  ch: string;
  at: number;
}

export interface CwDecodeState {
  enabled: boolean;
  overlayEnabled: boolean;
  panelOpen: boolean;
  status: CwDecoderStatus;
  error: string | null;
  transcript: string;
  recentChars: CwRecentChar[];
  lastCharAt: number;
  setEnabled: (on: boolean) => void;
  setOverlayEnabled: (on: boolean) => void;
  setPanelOpen: (open: boolean) => void;
  setStatus: (s: CwDecoderStatus, error?: string | null) => void;
  appendChars: (text: string) => void;
  clear: () => void;
}

const MAX_TRANSCRIPT = 4000;
const MAX_RECENT = 18;
let nextCharId = 1;

export const useCwDecodeStore = create<CwDecodeState>((set) => ({
  enabled: false,
  overlayEnabled: true,
  panelOpen: false,
  status: 'off',
  error: null,
  transcript: '',
  recentChars: [],
  lastCharAt: 0,
  // ON opens the panel; OFF closes it too — the transport button is a true
  // toggle (press = on+window, press again = fully off).
  setOverlayEnabled: (on) => set({ overlayEnabled: on }),
  setEnabled: (on) =>
    set((s) => ({ enabled: on, panelOpen: on, status: on ? s.status : 'off' })),
  setPanelOpen: (open) => set({ panelOpen: open }),
  setStatus: (status, error = null) => set({ status, error }),
  appendChars: (text) =>
    set((s) => {
      const now = Date.now();
      const added = Array.from(text).map((ch) => ({ id: nextCharId++, ch, at: now }));
      return {
        transcript: (s.transcript + text).slice(-MAX_TRANSCRIPT),
        recentChars: [...s.recentChars, ...added].slice(-MAX_RECENT),
        lastCharAt: now,
      };
    }),
  clear: () => set({ transcript: '' }),
}));
