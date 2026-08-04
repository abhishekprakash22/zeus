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

export interface CwDecodeState {
  enabled: boolean;
  panelOpen: boolean;
  status: CwDecoderStatus;
  error: string | null;
  transcript: string;
  lastCharAt: number;
  setEnabled: (on: boolean) => void;
  setPanelOpen: (open: boolean) => void;
  setStatus: (s: CwDecoderStatus, error?: string | null) => void;
  appendChars: (text: string) => void;
  clear: () => void;
}

const MAX_TRANSCRIPT = 4000;

export const useCwDecodeStore = create<CwDecodeState>((set) => ({
  enabled: false,
  panelOpen: false,
  status: 'off',
  error: null,
  transcript: '',
  lastCharAt: 0,
  // ON opens the panel; OFF closes it too — the transport button is a true
  // toggle (press = on+window, press again = fully off).
  setEnabled: (on) =>
    set((s) => ({ enabled: on, panelOpen: on, status: on ? s.status : 'off' })),
  setPanelOpen: (open) => set({ panelOpen: open }),
  setStatus: (status, error = null) => set({ status, error }),
  appendChars: (text) =>
    set((s) => ({
      transcript: (s.transcript + text).slice(-MAX_TRANSCRIPT),
      lastCharAt: Date.now(),
    })),
  clear: () => set({ transcript: '' }),
}));
