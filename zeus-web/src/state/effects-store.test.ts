// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

/** @vitest-environment jsdom */

import { afterEach, describe, expect, it, vi } from 'vitest';
import '../components/meters/__tests__/harness';
import {
  DEFAULT_EFFECTS_LEVEL,
  effectFlags,
  fetchServerEffectsLevel,
  normalizeEffectsLevel,
  useEffectsStore,
} from './effects-store';

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('effects levels', () => {
  it('defaults to Subtle and reads junk as the default', () => {
    expect(DEFAULT_EFFECTS_LEVEL).toBe('subtle');
    expect(normalizeEffectsLevel('full')).toBe('full');
    expect(normalizeEffectsLevel('FULL')).toBe('subtle');
    expect(normalizeEffectsLevel(null)).toBe('subtle');
  });

  it('Off runs nothing', () => {
    expect(effectFlags('off', false)).toEqual({
      lutTrace: false, spotlight: false, spotlightDim: 0.3, odometer: false, phosphor: false,
    });
  });

  it('Subtle runs the cheap effects; phosphor stays opt-in', () => {
    const f = effectFlags('subtle', false);
    expect(f.lutTrace && f.spotlight && f.odometer).toBe(true);
    expect(f.spotlightDim).toBe(0.3);
    expect(f.phosphor).toBe(false);
  });

  it('Full adds phosphor and a deeper spotlight', () => {
    const f = effectFlags('full', false);
    expect(f.phosphor).toBe(true);
    expect(f.spotlightDim).toBe(0.4);
  });

  it('reduced motion stops only the rolling digits', () => {
    for (const l of ['subtle', 'full'] as const) {
      const f = effectFlags(l, true);
      expect(f.odometer).toBe(false);
      expect(f.lutTrace).toBe(true);
      expect(f.spotlight).toBe(true);
    }
    expect(effectFlags('full', true).phosphor).toBe(true);
  });
});

describe('effects persistence', () => {
  it('a choice lands in this browser and on the radio', () => {
    const fetchMock = vi.fn(async () => new Response('{}'));
    vi.stubGlobal('fetch', fetchMock);
    useEffectsStore.getState().setLevel('full');
    expect(useEffectsStore.getState().flags.phosphor).toBe(true);
    expect(localStorage.getItem('zeus.display.effectsLevel')).toBe('full');
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/ui/effects',
      expect.objectContaining({ method: 'POST', body: JSON.stringify({ level: 'full' }) }),
    );
  });

  it("adopting the radio's copy does not write it back", () => {
    const fetchMock = vi.fn(async () => new Response('{}'));
    vi.stubGlobal('fetch', fetchMock);
    useEffectsStore.getState().adoptLevel('off');
    expect(useEffectsStore.getState().level).toBe('off');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("reads the radio's level; none stored or unreachable is no copy", async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ level: 'full' }))));
    expect(await fetchServerEffectsLevel()).toBe('full');
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ level: null }))));
    expect(await fetchServerEffectsLevel()).toBeNull();
    vi.stubGlobal('fetch', vi.fn(async () => { throw new Error('offline'); }));
    expect(await fetchServerEffectsLevel()).toBeNull();
  });
});
