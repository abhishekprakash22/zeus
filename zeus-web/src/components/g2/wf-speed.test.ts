// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

/** @vitest-environment jsdom */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { fetchServerWfSpeed, nextWfSpeed, normalizeWfSpeed, readLocalWfSpeed, saveWfSpeed } from './wf-speed';

afterEach(() => {
  localStorage.clear();
  vi.unstubAllGlobals();
});

describe('SPD cycle', () => {
  it('steps ×½ → ×1 → ×2 → ×4 → ×½', () => {
    expect(nextWfSpeed(0.5)).toBe(1);
    expect(nextWfSpeed(1)).toBe(2);
    expect(nextWfSpeed(2)).toBe(4);
    expect(nextWfSpeed(4)).toBe(0.5);
  });

  it('reads stale or hand-edited values back as ×1', () => {
    expect(normalizeWfSpeed('2')).toBe(2);
    expect(normalizeWfSpeed(0.5)).toBe(0.5);
    expect(normalizeWfSpeed(3)).toBe(1);
    expect(normalizeWfSpeed('junk')).toBe(1);
    expect(normalizeWfSpeed(null)).toBe(1);
  });
});

describe('SPD persistence', () => {
  it('saves to this browser and to the radio, per receiver', () => {
    const fetchMock = vi.fn(async () => new Response('{}'));
    vi.stubGlobal('fetch', fetchMock);
    saveWfSpeed(1, 4);
    expect(readLocalWfSpeed(1)).toBe(4);
    expect(readLocalWfSpeed(0)).toBe(1);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/ui/wf-speed',
      expect.objectContaining({ method: 'POST', body: JSON.stringify({ rx: 1, factor: 4 }) }),
    );
  });

  it("restores from the radio's copy when the browser has none (kiosk restart)", async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ '0': 2, '1': 0.5 }))));
    expect(await fetchServerWfSpeed(0)).toBe(2);
    expect(await fetchServerWfSpeed(1)).toBe(0.5);
    expect(await fetchServerWfSpeed(2)).toBeNull();
  });

  it('treats an unreachable radio as no copy', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new Error('offline'); }));
    expect(await fetchServerWfSpeed(0)).toBeNull();
  });
});
