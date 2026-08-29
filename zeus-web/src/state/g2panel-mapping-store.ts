// SPDX-License-Identifier: GPL-2.0-or-later
//
// G2-Ultra front-panel mapping store. Backs the "Button & encoder mapping"
// grid on the Radio Settings Front Panel card: the known control inventory
// (with default action names and the pinned MOX/TUNE flag), the action
// catalogs, the stored per-install overrides, and the press-to-identify stamp
// (the last raw button/encoder id the panel emitted — including ids outside
// the inventory, which is how the panel's PS button gets found and bound).
//
// Server-authoritative: hydrates from GET /api/radio/front-panel/mapping and
// re-syncs on every PUT/DELETE. The grid polls while it is open so the
// identify flash tracks live presses.

import { create } from 'zustand';

export interface G2PanelControl {
  id: number;
  label: string;
  defaultAction: string | null;
  pinned: boolean;
}

export interface G2PanelLastInput {
  kind: 'button' | 'encoder';
  id: number;
  ageMs: number;
}

export interface G2PanelMapping {
  buttons: G2PanelControl[];
  encoders: G2PanelControl[];
  buttonActions: string[];
  encoderActions: string[];
  /** inputId → action name. Empty = shipped defaults. */
  buttonOverrides: Record<number, string>;
  encoderOverrides: Record<number, string>;
  lastInput: G2PanelLastInput | null;
}

const EMPTY: G2PanelMapping = {
  buttons: [],
  encoders: [],
  buttonActions: [],
  encoderActions: [],
  buttonOverrides: {},
  encoderOverrides: {},
  lastInput: null,
};

function parseControls(raw: unknown): G2PanelControl[] {
  if (!Array.isArray(raw)) return [];
  return raw.flatMap((c) => {
    const r = (c && typeof c === 'object' ? c : {}) as Record<string, unknown>;
    if (typeof r.id !== 'number' || typeof r.label !== 'string') return [];
    return [{
      id: r.id,
      label: r.label,
      defaultAction: typeof r.defaultAction === 'string' ? r.defaultAction : null,
      pinned: r.pinned === true,
    }];
  });
}

function parseOverrides(raw: unknown): Record<number, string> {
  const out: Record<number, string> = {};
  if (raw && typeof raw === 'object') {
    for (const [k, v] of Object.entries(raw as Record<string, unknown>)) {
      const id = Number(k);
      if (Number.isInteger(id) && typeof v === 'string') out[id] = v;
    }
  }
  return out;
}

function parseStrings(raw: unknown): string[] {
  return Array.isArray(raw) ? raw.filter((s): s is string => typeof s === 'string') : [];
}

function parse(raw: unknown): G2PanelMapping {
  const r = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
  const li = (r.lastInput && typeof r.lastInput === 'object'
    ? r.lastInput : null) as Record<string, unknown> | null;
  const lastInput: G2PanelLastInput | null =
    li && (li.kind === 'button' || li.kind === 'encoder')
      && typeof li.id === 'number' && typeof li.ageMs === 'number'
      ? { kind: li.kind, id: li.id, ageMs: li.ageMs }
      : null;
  return {
    buttons: parseControls(r.buttons),
    encoders: parseControls(r.encoders),
    buttonActions: parseStrings(r.buttonActions),
    encoderActions: parseStrings(r.encoderActions),
    buttonOverrides: parseOverrides(r.buttonOverrides),
    encoderOverrides: parseOverrides(r.encoderOverrides),
    lastInput,
  };
}

export async function fetchG2PanelMapping(signal?: AbortSignal): Promise<G2PanelMapping> {
  const res = await fetch('/api/radio/front-panel/mapping', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/front-panel/mapping → ${res.status}`);
  return parse(await res.json());
}

interface G2PanelMappingState {
  mapping: G2PanelMapping;
  loaded: boolean;
  inflight: boolean;
  error: string | null;
  /** Hydrate / refresh (also the identify poll tick). */
  load: () => Promise<void>;
  /** Set (action) or clear (null) one override. */
  setOverride: (kind: 'button' | 'encoder', id: number, action: string | null) => Promise<void>;
  /** Delete every override — back to the shipped defaults. */
  resetAll: () => Promise<void>;
  __resetForTests: () => void;
}

export const useG2PanelMappingStore = create<G2PanelMappingState>((set) => ({
  mapping: EMPTY,
  loaded: false,
  inflight: false,
  error: null,

  load: async () => {
    try {
      const mapping = await fetchG2PanelMapping();
      set({ mapping, loaded: true, error: null });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : String(err) });
    }
  },

  setOverride: async (kind, id, action) => {
    set({ inflight: true, error: null });
    try {
      const res = await fetch('/api/radio/front-panel/mapping', {
        method: 'PUT',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ kind, id, action }),
      });
      if (!res.ok) {
        const body = (await res.json().catch(() => null)) as { error?: string } | null;
        throw new Error(body?.error ?? `PUT /api/radio/front-panel/mapping → ${res.status}`);
      }
      set({ mapping: parse(await res.json()), loaded: true, inflight: false });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : String(err), inflight: false });
    }
  },

  resetAll: async () => {
    set({ inflight: true, error: null });
    try {
      const res = await fetch('/api/radio/front-panel/mapping', { method: 'DELETE' });
      if (!res.ok) throw new Error(`DELETE /api/radio/front-panel/mapping → ${res.status}`);
      set({ mapping: parse(await res.json()), loaded: true, inflight: false });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : String(err), inflight: false });
    }
  },

  __resetForTests: () =>
    set({ mapping: EMPTY, loaded: false, inflight: false, error: null }),
}));
