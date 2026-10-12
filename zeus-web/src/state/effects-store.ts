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
//
// Visual effects level — one operator choice (Off / Subtle / Full) that
// decides which display effects run:
//
//   Off     the classic look: single-colour trace, plain passband tint,
//           static VFO digits.
//   Subtle  (default) palette-coloured trace + fill, passband spotlight
//           (outside the filter dims 30%), rolling odometer VFO digits.
//   Full    Subtle + phosphor persistence on the panadapter, spotlight 40%.
//
// Phosphor persistence costs one extra GL pass per panadapter, so it lives
// only in Full until it has been measured on the Pi under ?renderstats=1
// (budget: about 2 ms of gl-span). prefers-reduced-motion turns the rolling
// digits off at every level; nothing else here moves.
//
// Remembered in two vaults like the G2 pane prefs: localStorage for durable
// browser profiles and the radio's /api/ui/effects for the kiosk, whose
// browser profile does not survive a launch. The radio's copy wins.

import { create } from 'zustand';
import { setRenderStatsTag } from '../gl/render-stats';

export type EffectsLevel = 'off' | 'subtle' | 'full';
export const EFFECTS_LEVELS: readonly EffectsLevel[] = ['off', 'subtle', 'full'];
export const DEFAULT_EFFECTS_LEVEL: EffectsLevel = 'subtle';

const STORAGE_KEY = 'zeus.display.effectsLevel';

export type EffectFlags = {
  /** Trace + fill coloured through the waterfall palette. */
  lutTrace: boolean;
  /** Waterfall dims outside the receive filter. */
  spotlight: boolean;
  /** Dim factor outside the filter (0..1). */
  spotlightDim: number;
  /** Rolling VFO digits. */
  odometer: boolean;
  /** Panadapter density / persistence display. */
  phosphor: boolean;
};

export function normalizeEffectsLevel(raw: unknown): EffectsLevel {
  return raw === 'off' || raw === 'subtle' || raw === 'full' ? raw : DEFAULT_EFFECTS_LEVEL;
}

export function effectFlags(level: EffectsLevel, reducedMotion: boolean): EffectFlags {
  const on = level !== 'off';
  return {
    lutTrace: on,
    spotlight: on,
    spotlightDim: level === 'full' ? 0.4 : 0.3,
    odometer: on && !reducedMotion,
    phosphor: level === 'full',
  };
}

function readLocal(): EffectsLevel {
  try {
    if (typeof localStorage === 'undefined') return DEFAULT_EFFECTS_LEVEL;
    return normalizeEffectsLevel(localStorage.getItem(STORAGE_KEY));
  } catch {
    return DEFAULT_EFFECTS_LEVEL;
  }
}

function readReducedMotion(): boolean {
  try {
    return typeof window !== 'undefined' &&
      typeof window.matchMedia === 'function' &&
      window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  } catch {
    return false;
  }
}

type EffectsState = {
  level: EffectsLevel;
  reducedMotion: boolean;
  flags: EffectFlags;
  setLevel: (level: EffectsLevel) => void;
  /** Apply a level that came from the radio (no write-back). */
  adoptLevel: (level: EffectsLevel) => void;
  setReducedMotion: (reduced: boolean) => void;
};

const initialLevel = readLocal();
const initialReduced = readReducedMotion();

export const useEffectsStore = create<EffectsState>((set, get) => ({
  level: initialLevel,
  reducedMotion: initialReduced,
  flags: effectFlags(initialLevel, initialReduced),
  setLevel: (level) => {
    const l = normalizeEffectsLevel(level);
    set({ level: l, flags: effectFlags(l, get().reducedMotion) });
    try {
      localStorage.setItem(STORAGE_KEY, l);
    } catch {
      /* the radio's copy below still lands */
    }
    if (typeof fetch === 'function') {
      void fetch('/api/ui/effects', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ level: l }),
      }).catch(() => {});
    }
  },
  adoptLevel: (level) => {
    const l = normalizeEffectsLevel(level);
    set({ level: l, flags: effectFlags(l, get().reducedMotion) });
    try {
      localStorage.setItem(STORAGE_KEY, l);
    } catch {
      /* ignore */
    }
  },
  setReducedMotion: (reduced) => set({ reducedMotion: reduced, flags: effectFlags(get().level, reduced) }),
}));

let started = false;

/**
 * Once per app: adopt the radio's stored level and follow the OS reduced-
 * motion preference live. Safe to call repeatedly.
 */
export function startEffectsSync(): void {
  if (started) return;
  started = true;
  setRenderStatsTag(() => {
    const st = useEffectsStore.getState();
    const f = st.flags;
    const on = [f.lutTrace && 'palette-trace', f.spotlight && 'spotlight', f.odometer && 'odometer', f.phosphor && 'phosphor']
      .filter(Boolean)
      .join(' ');
    return `effects ${st.level}${st.reducedMotion ? ' (reduced-motion)' : ''}: ${on || 'none'}`;
  });
  try {
    const mq = window.matchMedia?.('(prefers-reduced-motion: reduce)');
    mq?.addEventListener?.('change', (e) => useEffectsStore.getState().setReducedMotion(e.matches));
  } catch {
    /* no matchMedia */
  }
  void fetchServerEffectsLevel().then((l) => {
    if (l !== null) useEffectsStore.getState().adoptLevel(l);
  });
}

/** The radio's stored level, or null when it has none / is unreachable. */
export async function fetchServerEffectsLevel(): Promise<EffectsLevel | null> {
  try {
    const r = await fetch('/api/ui/effects');
    if (!r.ok) return null;
    const body = (await r.json()) as { level?: unknown } | null;
    if (!body || body.level == null) return null;
    return normalizeEffectsLevel(body.level);
  } catch {
    return null;
  }
}
