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
// The filter card's display view, per receiver. The operator's CHOICE is
// stored (on the radio, /api/ui/filter-view, plus localStorage for first
// paint); what is SHOWN is derived from it and the receiver's mode — CW pitch
// only exists in CW modes, so leaving CW shows the audio spectrum and coming
// back restores CW pitch without the choice ever being overwritten.

import { create } from 'zustand';

export type FilterView = 'pass' | 'aspec' | 'scope' | 'awf' | 'cw' | 'lvl';

export const FILTER_VIEWS: { id: FilterView; label: string; icon: string; cwOnly?: boolean; audio: boolean }[] = [
  { id: 'pass', label: 'Passband', icon: '▭', audio: false },
  { id: 'aspec', label: 'Audio spectrum', icon: '∿', audio: true },
  { id: 'scope', label: 'Scope', icon: '⌇', audio: true },
  { id: 'awf', label: 'Audio waterfall', icon: '▤', audio: true },
  { id: 'cw', label: 'CW pitch', icon: '◎', cwOnly: true, audio: true },
  { id: 'lvl', label: 'Levels', icon: '▮', audio: true },
];

const KEY = 'zeus.filterView';
const isView = (v: unknown): v is FilterView => FILTER_VIEWS.some((x) => x.id === v);
export const isCwMode = (mode: string | null | undefined) => mode === 'CWU' || mode === 'CWL';

/** The views offered for a mode (CW pitch only in CW). */
export function viewsFor(mode: string | null | undefined): FilterView[] {
  const cw = isCwMode(mode);
  return FILTER_VIEWS.filter((v) => !v.cwOnly || cw).map((v) => v.id);
}

/** What to show for a stored choice in this mode (CW pitch falls back to the audio spectrum). */
export function effectiveView(chosen: FilterView, mode: string | null | undefined): FilterView {
  return viewsFor(mode).includes(chosen) ? chosen : 'aspec';
}

/** Next / previous view in this mode's list, wrapping. */
export function stepView(current: FilterView, mode: string | null | undefined, dir: 1 | -1): FilterView {
  const list = viewsFor(mode);
  const i = Math.max(0, list.indexOf(current));
  return list[(i + dir + list.length) % list.length]!;
}

function readLocal(): Record<number, FilterView> {
  try {
    const raw = JSON.parse(localStorage.getItem(KEY) ?? '{}') as Record<string, unknown>;
    const out: Record<number, FilterView> = {};
    for (const [k, v] of Object.entries(raw)) if (isView(v)) out[Number(k)] = v;
    return out;
  } catch {
    return {};
  }
}

type State = {
  views: Record<number, FilterView>;
  hydrated: boolean;
  get: (rx: number) => FilterView;
  set: (rx: number, view: FilterView) => void;
  hydrate: () => Promise<void>;
};

export const useFilterViewStore = create<State>((set, get) => ({
  views: typeof localStorage === 'undefined' ? {} : readLocal(),
  hydrated: false,
  get: (rx) => get().views[rx] ?? 'pass',
  set: (rx, view) => {
    const views = { ...get().views, [rx]: view };
    set({ views });
    try {
      localStorage.setItem(KEY, JSON.stringify(views));
    } catch {
      /* private mode — the radio copy still persists */
    }
    void fetch('/api/ui/filter-view', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ rx, view }),
    }).catch(() => {});
  },
  hydrate: async () => {
    if (get().hydrated) return;
    try {
      const r = await fetch('/api/ui/filter-view');
      if (r.ok) {
        const map = (await r.json()) as Record<string, unknown>;
        const views = { ...get().views };
        for (const [k, v] of Object.entries(map)) if (isView(v)) views[Number(k)] = v;
        set({ views });
      }
    } catch {
      /* offline: keep the local copy */
    }
    set({ hydrated: true });
  },
}));
