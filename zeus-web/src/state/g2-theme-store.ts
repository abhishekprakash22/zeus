// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// G2 theme — one store for the palette that used to live in three copies
// (G2Drawer's SETUP sheet, the mobile ThemeRow, and nowhere on desktop —
// the field bug: an iPad wide enough for the desktop shell had no picker
// and never even applied a theme persisted by the phone). The theme is an
// app-level look, not a drawer feature: it is applied at module load so
// the persisted choice paints on every shell before any picker mounts,
// and it survives the drawer unmounting. Rules unchanged: TX/danger red
// is NEVER themed, and the spectrum/waterfall are canvas-drawn — themes
// recolor the chrome, not the RF.

import { create } from 'zustand';

export const G2_THEMES = [
  { id: 'zeus', label: 'ZEUS BLUE', accent: '#4aa3df' },
  { id: 'amber', label: 'AMBER', accent: '#ffb545' },
  { id: 'nightred', label: 'NIGHT RED', accent: '#ff5a4d' },
  { id: 'phosphor', label: 'PHOSPHOR', accent: '#4ade80' },
  { id: 'ice', label: 'ICE', accent: '#8ad8ff' },
] as const;
export type G2ThemeId = (typeof G2_THEMES)[number]['id'];

const THEME_KEY = 'zeus.g2.theme';

function readTheme(): G2ThemeId {
  try {
    const v = localStorage.getItem(THEME_KEY);
    return G2_THEMES.some((t) => t.id === v) ? (v as G2ThemeId) : 'zeus';
  } catch {
    return 'zeus';
  }
}

function applyTheme(theme: G2ThemeId): void {
  if (typeof document === 'undefined') return;
  if (theme === 'zeus') delete document.body.dataset.g2Theme;
  else document.body.dataset.g2Theme = theme;
}

export type G2ThemeState = {
  theme: G2ThemeId;
  setTheme: (theme: G2ThemeId) => void;
};

export const useG2ThemeStore = create<G2ThemeState>((set) => ({
  theme: readTheme(),
  setTheme: (theme) => {
    applyTheme(theme);
    try {
      localStorage.setItem(THEME_KEY, theme);
    } catch {
      /* private mode */
    }
    set({ theme });
  },
}));

// Apply the persisted theme immediately — before React mounts anything —
// so a theme picked on one visit (or one shell) is on the glass from the
// first paint of the next, on every shell including desktop.
applyTheme(useG2ThemeStore.getState().theme);
