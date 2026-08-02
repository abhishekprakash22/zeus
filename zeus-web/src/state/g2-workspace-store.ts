// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// G2 front-panel workspace frame — a display option that constrains the
// workspace canvas to the ANAN-G2's built-in touch display (1280×800) so
// layouts arranged on a large external monitor are WYSIWYG for the front
// panel: what fits in the frame is exactly what the G2's own screen will
// show. Letterboxed and outlined when the browser window is larger; a
// no-op when the window is already ≤ the frame (i.e. on the G2 itself).
//
// Persisted per workstation in localStorage (like the fullscreen
// preference): it is a property of the SCREEN you are designing on, not of
// the radio or the layout, so it deliberately does not ride zeus-prefs.db.

import { create } from 'zustand';

export const G2_FRAME_W = 1280;
export const G2_FRAME_H = 800;

const KEY = 'zeus.workspace.g2Frame';

function readInitial(): boolean {
  try { return localStorage.getItem(KEY) === '1'; } catch { return false; }
}

interface G2WorkspaceState {
  g2Frame: boolean;
  setG2Frame: (on: boolean) => void;
}

export const useG2WorkspaceStore = create<G2WorkspaceState>((set) => ({
  g2Frame: readInitial(),
  setG2Frame: (on) => {
    try { localStorage.setItem(KEY, on ? '1' : '0'); } catch { /* private mode */ }
    set({ g2Frame: on });
  },
}));
