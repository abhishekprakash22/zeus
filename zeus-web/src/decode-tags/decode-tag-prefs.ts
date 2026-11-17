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
// Which decode tags the operator shows — kept on the radio
// (GET/POST /api/ui/decode-tags) so the kiosk's throwaway browser profile
// can't lose it. Everything is on by default.

import { create } from 'zustand';

/**
 * Master availability of "decodes on the spectrum". OFF: the feature is
 * withheld from the product until it is mature — no TAGS button or deck key,
 * no settings switches, nothing drawn, and the plain spot labels stay as they
 * were. The overlay, model and radio endpoints remain in the tree so that
 * flipping this back on is the whole job of restoring it.
 */
export const DECODE_TAGS_AVAILABLE: boolean = false;

export type DecodeTagPrefs = {
  enabled: boolean;
  ft8: boolean;
  cw: boolean;
  spots: boolean;
  classifier: boolean;
};

export const DEFAULT_DECODE_TAG_PREFS: DecodeTagPrefs = {
  enabled: true,
  ft8: true,
  cw: true,
  spots: true,
  classifier: true,
};

export function normalizePrefs(raw: unknown): DecodeTagPrefs {
  const o = raw && typeof raw === 'object' ? (raw as Record<string, unknown>) : {};
  const b = (k: keyof DecodeTagPrefs) => (typeof o[k] === 'boolean' ? (o[k] as boolean) : DEFAULT_DECODE_TAG_PREFS[k]);
  return { enabled: b('enabled'), ft8: b('ft8'), cw: b('cw'), spots: b('spots'), classifier: b('classifier') };
}

type State = DecodeTagPrefs & {
  hydrated: boolean;
  hydrate: () => Promise<void>;
  set: (patch: Partial<DecodeTagPrefs>) => void;
};

let hydrating: Promise<void> | null = null;

export const useDecodeTagPrefs = create<State>((set, get) => ({
  ...DEFAULT_DECODE_TAG_PREFS,
  hydrated: false,
  hydrate: () => {
    if (get().hydrated) return Promise.resolve();
    hydrating ??= fetch('/api/ui/decode-tags')
      .then(async (r) => {
        if (r.ok) set({ ...normalizePrefs(await r.json()), hydrated: true });
      })
      .catch(() => {})
      .finally(() => {
        hydrating = null;
      });
    return hydrating;
  },
  set: (patch) => {
    const s = get();
    const next = normalizePrefs({ ...pickPrefs(s), ...patch });
    set({ ...next, hydrated: true });
    void fetch('/api/ui/decode-tags', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(next),
    }).catch(() => {});
  },
}));

function pickPrefs(s: DecodeTagPrefs): DecodeTagPrefs {
  return { enabled: s.enabled, ft8: s.ft8, cw: s.cw, spots: s.spots, classifier: s.classifier };
}

/** The spot overlay steps aside while decode tags draw the spots — which they
 *  only do when the feature is available at all. */
export function decodeTagsOwnSpots(
  p: Pick<DecodeTagPrefs, 'enabled' | 'spots'>,
  available: boolean = DECODE_TAGS_AVAILABLE,
): boolean {
  return available && p.enabled && p.spots;
}
