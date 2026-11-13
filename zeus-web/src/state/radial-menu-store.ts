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

// The spectrum radial menu's open state. The gesture hook (right-click, pen
// barrel, two-finger tap) captures everything about the press point and opens
// it here; SpectrumRadialMenu renders whatever is open. One menu app-wide.

import { create } from 'zustand';
import type { RxMode } from '../api/client';

export type RadialContext = {
  /** Screen point the menu centres on. */
  clientX: number;
  clientY: number;
  /** Frequency under the pointer, unsnapped. */
  rawHz: number;
  /** Where "Tune" lands: the snapped signal's mode-aware tuning frequency, or
   *  the click grid over bare noise. */
  tuneHz: number;
  /** True when tuneHz came from a signal (the menu says so). */
  snapped: boolean;
  /** Nearest carrier/tone peak, for "Notch here"; null over noise. */
  carrierHz: number | null;
  /** Display scale at the press, for hit-testing memory markers. */
  hzPerPx: number;
  /** Receiver the press tunes (0 = RX1) and the pane it was made on. */
  rxIndex: number;
  panRxIndex: number;
  /** That receiver's mode at the press. */
  mode: RxMode;
  /** Tune that receiver to tuneHz through the surface's own click path. */
  tune: () => void;
};

type RadialState = {
  open: RadialContext | null;
  close: () => void;
};

export const useRadialMenuStore = create<RadialState>((set) => ({
  open: null,
  close: () => set({ open: null }),
}));

export function openSpectrumRadial(ctx: RadialContext): void {
  useRadialMenuStore.setState({ open: ctx });
}
