// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
// See LICENSE for the full GPL text.

import { create } from 'zustand';
import { fetchThemeSettings, updateThemeSettings } from '../api/themeSettings';
import { PAPER_DEFAULT_LIGHTNESS, PAPER_DEFAULT_TINT, PAPER_SURFACE_KEY, paperSurface, rgbOf } from '../theme/paper';
import { setDisplayFloor, setPaperFloor } from '../gl/colormap';
import {
  DARK_DEFAULT_DEPTH,
  LIGHT_DEFAULT_DEPTH,
  clampDepth,
  depthHex,
  depthOf,
  depthRgb,
} from '../theme/display-surface';

// Theme + per-token colour overrides for the Zeus UI.
//
// `theme` flips the global `data-theme` attribute on <html>, which selects
// either the default dark token set in tokens.css or the brushed-silver
// LIGHT overlay (`:root[data-theme="light"]`). The operator can additionally
// override individual CSS variables — e.g. nudge --accent from electric blue
// to teal — and those overrides apply across BOTH themes because they're
// injected via a runtime <style> tag that sets `:root { … }` after the
// theme-block in the stylesheet.
//
// Persistence is two-layered:
//   - localStorage acts as a fast-paint cache so the theme attribute lands
//     on <html> before the first paint (no HTTP wait, no light/dark flash).
//   - LiteDB (server) is the source of truth across devices — `hydrate()`
//     pulls it on mount and reconciles, and every mutation fires a
//     debounced PUT so a single setting follows the operator from desktop
//     to tablet without a per-browser reset.

export type ThemeId = 'dark' | 'light';

// Tokens we expose in the operator-facing colour-tweak UI. Two groups:
//   - ACCENT tokens — the "feel" tokens an operator notices: accent for
//     active controls, signal chain colours, warm halos on meters.
//   - SURFACE tokens — chassis / panel / line / text. These were historically
//     hidden because flipping one surface in isolation can break contrast
//     (e.g. a dark --bg-0 with light --fg-0 already painted by the theme).
//     They're exposed now because Brian asked for per-shack chassis tuning;
//     the panel renders them in a separate group with a warning that they
//     can break contrast if pushed too far from the theme defaults.
// Add a token here + a label in TOKEN_META if you want a new picker row.
export const TWEAKABLE_TOKENS = [
  '--accent',
  '--accent-bright',
  '--tx',
  '--power',
  '--amber',
  '--cyan',
  '--ok',
  '--orange',
  '--bg-0',
  '--bg-1',
  '--bg-2',
  '--bg-3',
  '--line',
  '--line-soft',
  '--line-strong',
  '--fg-0',
  '--fg-1',
  '--fg-2',
] as const;

export type TweakableToken = (typeof TWEAKABLE_TOKENS)[number];

type ThemeState = {
  theme: ThemeId;
  // Map of CSS-variable-name → hex colour (e.g. `--accent` → `#FF00FF`).
  // Only entries present here override the stylesheet default; deleting a
  // key restores the original token value from tokens.css.
  overrides: Partial<Record<TweakableToken, string>>;
  // True once `hydrate()` has reconciled local cache with the server. Stays
  // false on offline / first-launch failures so we don't keep flapping.
  hydrated: boolean;
  // The light theme's paper surface — one grey hex from the lightness slider
  // and tint (theme/paper.ts). Persisted with the overrides under
  // --paper-surface so /api/theme-settings keeps it (the kiosk's localStorage
  // does not survive a launch).
  paperSurface: string;
  setPaperSurface: (hex: string) => void;
  // Spectrum displays (panadapter, waterfall, filter mini-pan). Light: blue
  // (default) or paper; dark: always the blue path, where depth 0 = black.
  // Depths per theme along theme/display-surface.ts's path. Persisted with
  // the overrides as --light-displays / --display-bg-light / --display-bg-dark.
  lightDisplays: 'blue' | 'paper';
  displayDepthLight: number;
  displayDepthDark: number;
  setLightDisplays: (mode: 'blue' | 'paper') => void;
  setDisplayDepth: (theme: ThemeId, depth: number) => void;
  setTheme: (t: ThemeId) => void;
  setOverride: (token: TweakableToken, hex: string | null) => void;
  resetOverrides: () => void;
  // Pull the authoritative copy from /api/theme-settings and apply it if it
  // differs from the localStorage fast-paint cache. Called once by
  // ThemeApplier on mount; safe to invoke repeatedly.
  hydrate: () => Promise<void>;
};

const THEME_KEY = 'zeus.theme';
const OVERRIDES_KEY = 'zeus.theme.overrides';
const PAPER_KEY = 'zeus.theme.paper';
const DISPLAYS_KEY = 'zeus.theme.displays';
export const LIGHT_DISPLAYS_KEY = '--light-displays';
export const DISPLAY_BG_LIGHT_KEY = '--display-bg-light';
export const DISPLAY_BG_DARK_KEY = '--display-bg-dark';
// The server keeps "--name": "#rrggbb" pairs only, so the mode is a colour:
// black = blue displays, white = paper displays.
const MODE_BLUE = '#000000';
const MODE_PAPER = '#FFFFFF';

type DisplayPrefs = { lightDisplays: 'blue' | 'paper'; displayDepthLight: number; displayDepthDark: number };
const DEFAULT_DISPLAYS: DisplayPrefs = {
  lightDisplays: 'blue',
  displayDepthLight: LIGHT_DEFAULT_DEPTH,
  displayDepthDark: DARK_DEFAULT_DEPTH,
};

function readDisplays(): DisplayPrefs {
  try {
    if (typeof localStorage === 'undefined') return DEFAULT_DISPLAYS;
    const raw = JSON.parse(localStorage.getItem(DISPLAYS_KEY) ?? 'null') as Partial<DisplayPrefs> | null;
    if (!raw) return DEFAULT_DISPLAYS;
    return {
      lightDisplays: raw.lightDisplays === 'paper' ? 'paper' : 'blue',
      displayDepthLight: clampDepth(Number(raw.displayDepthLight ?? LIGHT_DEFAULT_DEPTH), 'light'),
      displayDepthDark: clampDepth(Number(raw.displayDepthDark ?? DARK_DEFAULT_DEPTH), 'dark'),
    };
  } catch {
    return DEFAULT_DISPLAYS;
  }
}

function writeDisplays(d: DisplayPrefs): void {
  try {
    if (typeof localStorage !== 'undefined') localStorage.setItem(DISPLAYS_KEY, JSON.stringify(d));
  } catch {
    /* quota / private mode — accept silently */
  }
}

/** The display prefs as theme-settings override entries. Exported for tests. */
export function displayOverrideEntries(d: DisplayPrefs): Record<string, string> {
  return {
    [LIGHT_DISPLAYS_KEY]: d.lightDisplays === 'paper' ? MODE_PAPER : MODE_BLUE,
    [DISPLAY_BG_LIGHT_KEY]: depthHex(d.displayDepthLight),
    [DISPLAY_BG_DARK_KEY]: depthHex(d.displayDepthDark),
  };
}

/** Display prefs back from theme-settings overrides (missing keys → current). */
export function displaysFromOverrides(o: Record<string, string>, cur: DisplayPrefs): DisplayPrefs {
  const mode = o[LIGHT_DISPLAYS_KEY]?.toUpperCase();
  return {
    lightDisplays: mode === MODE_PAPER ? 'paper' : mode === MODE_BLUE ? 'blue' : cur.lightDisplays,
    displayDepthLight: o[DISPLAY_BG_LIGHT_KEY] ? clampDepth(depthOf(o[DISPLAY_BG_LIGHT_KEY]), 'light') : cur.displayDepthLight,
    displayDepthDark: o[DISPLAY_BG_DARK_KEY] ? clampDepth(depthOf(o[DISPLAY_BG_DARK_KEY]), 'dark') : cur.displayDepthDark,
  };
}

/**
 * Point the palettes' floor at the active display surface, synchronously with
 * the store change (renderers re-upload in response): light + blue → that
 * blue; dark with a depth → that blue; dark at 0, or paper → none.
 */
function syncDisplayFloor(theme: ThemeId, d: DisplayPrefs): void {
  if (theme === 'light') {
    setDisplayFloor(d.lightDisplays === 'blue' ? depthRgb(d.displayDepthLight) : null);
  } else {
    setDisplayFloor(d.displayDepthDark > 0 ? depthRgb(d.displayDepthDark) : null);
  }
}
export const DEFAULT_PAPER_SURFACE = paperSurface(PAPER_DEFAULT_LIGHTNESS, PAPER_DEFAULT_TINT);

// The paper palette's floor moves with the surface synchronously, before any
// renderer re-uploads its LUT in response to the store change.
function syncPaperFloor(hex: string): void {
  const rgb = rgbOf(hex);
  if (rgb) setPaperFloor(rgb);
}

function readPaper(): string {
  try {
    if (typeof localStorage === 'undefined') return DEFAULT_PAPER_SURFACE;
    const raw = localStorage.getItem(PAPER_KEY);
    return isHexColor(raw) ? raw.toUpperCase() : DEFAULT_PAPER_SURFACE;
  } catch {
    return DEFAULT_PAPER_SURFACE;
  }
}

function writePaper(hex: string): void {
  try {
    if (typeof localStorage !== 'undefined') localStorage.setItem(PAPER_KEY, hex);
  } catch {
    /* quota / private mode — accept silently */
  }
}

function isThemeId(v: unknown): v is ThemeId {
  return v === 'dark' || v === 'light';
}

function isHexColor(v: unknown): v is string {
  return typeof v === 'string' && /^#[0-9A-Fa-f]{6}$/.test(v);
}

function readTheme(): ThemeId {
  try {
    if (typeof localStorage === 'undefined') return 'dark';
    const raw = localStorage.getItem(THEME_KEY);
    return isThemeId(raw) ? raw : 'dark';
  } catch {
    return 'dark';
  }
}

function writeTheme(t: ThemeId): void {
  try {
    if (typeof localStorage !== 'undefined') localStorage.setItem(THEME_KEY, t);
  } catch {
    /* quota / private mode — accept silently */
  }
}

function readOverrides(): Partial<Record<TweakableToken, string>> {
  try {
    if (typeof localStorage === 'undefined') return {};
    const raw = localStorage.getItem(OVERRIDES_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw) as Partial<Record<string, unknown>>;
    const out: Partial<Record<TweakableToken, string>> = {};
    for (const k of TWEAKABLE_TOKENS) {
      const v = parsed[k];
      if (isHexColor(v)) out[k] = v.toUpperCase();
    }
    return out;
  } catch {
    return {};
  }
}

function writeOverrides(o: Partial<Record<TweakableToken, string>>): void {
  try {
    if (typeof localStorage !== 'undefined')
      localStorage.setItem(OVERRIDES_KEY, JSON.stringify(o));
  } catch {
    /* quota / private mode — accept silently */
  }
}

// Debounced PUT to /api/theme-settings — collapses the colour-picker drag
// stream into ~one write every 300 ms while still flushing the final value.
// Fire-and-forget; persistence failures are logged but never block the UI.
let pushTimer: ReturnType<typeof setTimeout> | null = null;
function schedulePush(state: {
  theme: ThemeId;
  overrides: Partial<Record<TweakableToken, string>>;
  paperSurface?: string;
  displays?: DisplayPrefs;
}) {
  if (pushTimer) clearTimeout(pushTimer);
  pushTimer = setTimeout(() => {
    pushTimer = null;
    // The paper surface rides along as one extra override key — the server
    // keeps any "--name": "#rrggbb" pair, so no new endpoint is needed.
    const payload: Record<string, string> = { ...(state.overrides as Record<string, string>) };
    if (state.paperSurface) payload[PAPER_SURFACE_KEY] = state.paperSurface;
    if (state.displays) Object.assign(payload, displayOverrideEntries(state.displays));
    updateThemeSettings({ theme: state.theme, overrides: payload })
      .catch((err) => {
        // Server unreachable / 5xx — keep the local change, retry on next mutation.
        // Cast to keep this file lint-clean without pulling a logger.

        console.warn('theme-store: PUT /api/theme-settings failed', err);
      });
  }, 300);
}

function displaysOf(s: DisplayPrefs): DisplayPrefs {
  return {
    lightDisplays: s.lightDisplays,
    displayDepthLight: s.displayDepthLight,
    displayDepthDark: s.displayDepthDark,
  };
}

const initialTheme = readTheme();
const initialDisplays = readDisplays();
syncDisplayFloor(initialTheme, initialDisplays);

export const useThemeStore = create<ThemeState>((set, get) => ({
  theme: initialTheme,
  ...initialDisplays,
  setLightDisplays: (mode) => {
    const next = { ...displaysOf(get()), lightDisplays: mode };
    writeDisplays(next);
    syncDisplayFloor(get().theme, next);
    set({ lightDisplays: mode });
    schedulePush({ theme: get().theme, overrides: get().overrides, paperSurface: get().paperSurface, displays: next });
  },
  setDisplayDepth: (theme, depth) => {
    const d = clampDepth(depth, theme);
    const next = {
      ...displaysOf(get()),
      ...(theme === 'light' ? { displayDepthLight: d } : { displayDepthDark: d }),
    };
    writeDisplays(next);
    syncDisplayFloor(get().theme, next);
    set(theme === 'light' ? { displayDepthLight: d } : { displayDepthDark: d });
    schedulePush({ theme: get().theme, overrides: get().overrides, paperSurface: get().paperSurface, displays: next });
  },
  overrides: readOverrides(),
  hydrated: false,
  paperSurface: (() => {
    const p = readPaper();
    syncPaperFloor(p);
    return p;
  })(),
  setPaperSurface: (hex) => {
    if (!isHexColor(hex)) return;
    const v = hex.toUpperCase();
    if (v === get().paperSurface) return;
    writePaper(v);
    syncPaperFloor(v);
    set({ paperSurface: v });
    schedulePush({ theme: get().theme, overrides: get().overrides, paperSurface: v, displays: displaysOf(get()) });
  },
  setTheme: (theme) => {
    writeTheme(theme);
    syncDisplayFloor(theme, displaysOf(get()));
    set({ theme });
    schedulePush({ theme, overrides: get().overrides, paperSurface: get().paperSurface, displays: displaysOf(get()) });
  },
  setOverride: (token, hex) => {
    const next = { ...get().overrides };
    if (hex == null) {
      delete next[token];
    } else {
      if (!isHexColor(hex)) return;
      next[token] = hex.toUpperCase();
    }
    writeOverrides(next);
    set({ overrides: next });
    schedulePush({ theme: get().theme, overrides: next, paperSurface: get().paperSurface, displays: displaysOf(get()) });
  },
  resetOverrides: () => {
    writeOverrides({});
    set({ overrides: {} });
    schedulePush({ theme: get().theme, overrides: {}, paperSurface: get().paperSurface, displays: displaysOf(get()) });
  },
  hydrate: async () => {
    if (get().hydrated) return;
    try {
      const server = await fetchThemeSettings();
      // Project the server overrides into the typed TweakableToken keyspace —
      // anything the client doesn't know about is dropped (forward-compat:
      // newer server, older client). Same shape readOverrides() returns.
      const serverOverrides: Partial<Record<TweakableToken, string>> = {};
      for (const k of TWEAKABLE_TOKENS) {
        const v = server.overrides[k];
        if (isHexColor(v)) serverOverrides[k] = v.toUpperCase();
      }
      // Apply server values to local state + cache. If they match what we
      // already had, this is a no-op — but we mark hydrated either way so
      // subsequent mutations push through schedulePush.
      const serverPaper = server.overrides[PAPER_SURFACE_KEY];
      const paper = isHexColor(serverPaper) ? serverPaper.toUpperCase() : get().paperSurface;
      writeTheme(server.theme);
      writeOverrides(serverOverrides);
      writePaper(paper);
      syncPaperFloor(paper);
      const displays = displaysFromOverrides(server.overrides, displaysOf(get()));
      writeDisplays(displays);
      syncDisplayFloor(server.theme, displays);
      set({ theme: server.theme, overrides: serverOverrides, paperSurface: paper, ...displays, hydrated: true });
    } catch (err) {
      // Backend unreachable — stay on the localStorage cache. Mark hydrated
      // so we don't loop forever; the next mutation will attempt a PUT and
      // recover the link. Surface for debugging.

      console.warn('theme-store: hydrate failed, using localStorage cache', err);
      set({ hydrated: true });
    }
  },
}));
