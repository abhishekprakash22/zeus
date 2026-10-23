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
// G2 floating cards' layout — each card's box and which cards are closed —
// kept on the RADIO (/api/ui/g2-cards) as well as in localStorage. The kiosk
// browser profile is throwaway, so localStorage alone forgot every card
// position and every closed card on each launch (field report: the filter
// display does not remember its position). localStorage stays as the
// first-paint cache; the radio's copy is adopted when it arrives.

export type CardBox = { x: number; y: number; w: number; h: number };
type Layout = { boxes: Record<string, CardBox>; hidden: string[] };

const BOXES_KEY = 'zeus.g2.cards';
const HIDDEN_KEY = 'zeus.g2.hiddenCards';
export const G2_CARDS_EVENT = 'zeus-g2-cards';

const finiteBox = (b: unknown): b is CardBox =>
  !!b &&
  typeof b === 'object' &&
  ['x', 'y', 'w', 'h'].every((k) => Number.isFinite((b as Record<string, unknown>)[k]));

function readLocal(): Layout {
  const out: Layout = { boxes: {}, hidden: [] };
  try {
    const raw = JSON.parse(localStorage.getItem(BOXES_KEY) ?? '{}') as Record<string, unknown>;
    for (const [k, v] of Object.entries(raw ?? {})) if (finiteBox(v)) out.boxes[k] = v;
  } catch {
    /* corrupt cache: start clean */
  }
  try {
    const h = JSON.parse(localStorage.getItem(HIDDEN_KEY) ?? '[]') as unknown;
    if (Array.isArray(h)) out.hidden = h.filter((x): x is string => typeof x === 'string');
  } catch {
    /* corrupt cache: start clean */
  }
  return out;
}

function writeLocal(l: Layout): void {
  try {
    localStorage.setItem(BOXES_KEY, JSON.stringify(l.boxes));
    localStorage.setItem(HIDDEN_KEY, JSON.stringify(l.hidden));
  } catch {
    /* private mode: the radio copy still persists */
  }
}

let pushTimer: ReturnType<typeof setTimeout> | null = null;
function pushSoon(): void {
  if (pushTimer) clearTimeout(pushTimer);
  pushTimer = setTimeout(() => {
    pushTimer = null;
    void fetch('/api/ui/g2-cards', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(readLocal()),
    }).catch(() => {});
  }, 400);
}

export function getCardBox(id: string): CardBox | null {
  return readLocal().boxes[id] ?? null;
}

export function setCardBox(id: string, box: CardBox): void {
  const l = readLocal();
  l.boxes[id] = box;
  writeLocal(l);
  pushSoon();
}

export function getHiddenCards(): string[] {
  return readLocal().hidden;
}

export function setHiddenCards(hidden: string[]): void {
  const l = readLocal();
  l.hidden = [...new Set(hidden)];
  writeLocal(l);
  pushSoon();
}

/** Forget every card position (closed cards stay closed) — here and on the radio. */
export function resetCardBoxes(): void {
  const l = readLocal();
  l.boxes = {};
  writeLocal(l);
  if (pushTimer) clearTimeout(pushTimer);
  pushTimer = null;
  // keepalive: RESET CARDS reloads the page straight after, which would
  // otherwise cancel this request.
  void fetch('/api/ui/g2-cards', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(l),
    keepalive: true,
  }).catch(() => {});
}

let hydration: Promise<void> | null = null;

/**
 * Adopt the radio's copy once per page load (a G2 launch starts with an
 * empty localStorage), then tell mounted cards. Idempotent.
 */
export function hydrateCardLayout(): Promise<void> {
  hydration ??= (async () => {
    try {
      const r = await fetch('/api/ui/g2-cards');
      if (!r.ok) return;
      const body = (await r.json()) as { boxes?: Record<string, unknown>; hidden?: unknown };
      const local = readLocal();
      const boxes: Record<string, CardBox> = { ...local.boxes };
      for (const [k, v] of Object.entries(body.boxes ?? {})) if (finiteBox(v)) boxes[k] = v;
      const hidden = Array.isArray(body.hidden)
        ? body.hidden.filter((x): x is string => typeof x === 'string')
        : local.hidden;
      writeLocal({ boxes, hidden });
      if (typeof window !== 'undefined') window.dispatchEvent(new Event(G2_CARDS_EVENT));
    } catch {
      /* offline: keep the local copy */
    }
  })();
  return hydration;
}

/** Test seam: forget that hydration ran. */
export function resetCardLayoutHydrationForTests(): void {
  hydration = null;
}
