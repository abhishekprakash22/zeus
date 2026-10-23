// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  G2_CARDS_EVENT,
  getCardBox,
  getHiddenCards,
  hydrateCardLayout,
  resetCardBoxes,
  resetCardLayoutHydrationForTests,
  setCardBox,
  setHiddenCards,
} from './g2-card-layout';

const radio = { boxes: { 'filter-a': { x: 420, y: 60, w: 560, h: 300 } }, hidden: ['filter-b'] };
const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
  void url;
  if (!init) return new Response(JSON.stringify(radio));
  return new Response('{}');
});
const posts = () => fetchMock.mock.calls.filter(([, i]) => (i as RequestInit | undefined)?.method === 'POST');

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  localStorage.clear();
  fetchMock.mockClear();
  vi.stubGlobal('fetch', fetchMock);
  resetCardLayoutHydrationForTests();
});
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('G2 card layout', () => {
  it("a fresh kiosk launch adopts the radio's positions and closed cards, and tells the cards", async () => {
    expect(getCardBox('filter-a')).toBeNull(); // empty localStorage, as on every G2 launch
    const heard = vi.fn();
    window.addEventListener(G2_CARDS_EVENT, heard);
    await hydrateCardLayout();
    window.removeEventListener(G2_CARDS_EVENT, heard);
    expect(getCardBox('filter-a')).toEqual({ x: 420, y: 60, w: 560, h: 300 });
    expect(getHiddenCards()).toEqual(['filter-b']);
    expect(heard).toHaveBeenCalledTimes(1);
  });

  it('moving a card saves it locally and on the radio (debounced)', () => {
    setCardBox('smeter', { x: -24, y: 60, w: 300, h: 260 });
    setCardBox('smeter', { x: -30, y: 70, w: 300, h: 260 });
    expect(getCardBox('smeter')).toEqual({ x: -30, y: 70, w: 300, h: 260 });
    expect(posts()).toHaveLength(0);
    vi.advanceTimersByTime(500);
    expect(posts()).toHaveLength(1);
    const body = JSON.parse(posts()[0]![1]!.body as string);
    expect(body.boxes.smeter).toEqual({ x: -30, y: 70, w: 300, h: 260 });
  });

  it('closing a card is remembered on the radio too', () => {
    setHiddenCards(['smeter', 'smeter']);
    vi.advanceTimersByTime(500);
    expect(JSON.parse(posts()[0]![1]!.body as string).hidden).toEqual(['smeter']);
  });

  it('reset forgets positions here and on the radio at once, and survives the reload', () => {
    setCardBox('filter-a', { x: 1, y: 2, w: 300, h: 200 });
    setHiddenCards(['smeter']);
    resetCardBoxes();
    expect(getCardBox('filter-a')).toBeNull();
    expect(getHiddenCards()).toEqual(['smeter']);
    const last = posts().at(-1)![1] as RequestInit;
    expect(JSON.parse(last.body as string).boxes).toEqual({});
    expect(last.keepalive).toBe(true);
  });

  it('ignores junk boxes from the radio', async () => {
    fetchMock.mockImplementationOnce(async () =>
      new Response(JSON.stringify({ boxes: { bad: { x: 'a', y: 1, w: 1, h: 1 } }, hidden: [3, 'ok'] })),
    );
    await hydrateCardLayout();
    expect(getCardBox('bad')).toBeNull();
    expect(getHiddenCards()).toEqual(['ok']);
  });
});
