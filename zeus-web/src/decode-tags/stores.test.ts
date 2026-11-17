// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  _resetCallStatusForTest,
  parseStatusItem,
  requestCallStatus,
  statusFor,
  useCallStatusStore,
} from './call-status-store';
import { DECODE_TAGS_AVAILABLE, decodeTagsOwnSpots, normalizePrefs } from './decode-tag-prefs';

describe('call status cache', () => {
  let fetchMock: ReturnType<typeof vi.fn>;
  beforeEach(() => {
    vi.useFakeTimers();
    _resetCallStatusForTest();
    fetchMock = vi.fn(async (_url: string, init?: RequestInit) => {
      const body = JSON.parse(init!.body as string) as { items: { call: string; freqHz: number }[] };
      return {
        ok: true,
        json: async () => ({
          items: body.items.map((i) => ({ ...i, status: 'NewBand', entity: 'Germany', workedAnyBand: false })),
        }),
      } as Response;
    });
    vi.stubGlobal('fetch', fetchMock);
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('batches requests and caches per call and band', async () => {
    requestCallStatus([{ call: 'DL1ABC', hz: 14_074_500 }]);
    requestCallStatus([{ call: 'DL1ABC', hz: 14_074_500 }, { call: 'G4XYZ', hz: 14_075_000 }]);
    await vi.advanceTimersByTimeAsync(500);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const byKey = useCallStatusStore.getState().byKey;
    expect(statusFor(byKey, 'dl1abc', 14_200_000)?.status).toBe('NewBand'); // same band (20m)
    expect(statusFor(byKey, 'DL1ABC', 7_074_000)).toBeNull(); // other band
    // Cached: no second request.
    requestCallStatus([{ call: 'DL1ABC', hz: 14_074_500 }]);
    await vi.advanceTimersByTimeAsync(500);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('parses defensively', () => {
    expect(parseStatusItem({ call: 'W1AW', freqHz: 1, status: 'Bogus' })?.info.status).toBeNull();
    expect(parseStatusItem({ call: 5 })).toBeNull();
    expect(parseStatusItem(null)).toBeNull();
  });
});

describe('decode tag prefs', () => {
  it('fills defaults and keeps booleans', () => {
    expect(normalizePrefs({ cw: false, junk: 1 })).toEqual({
      enabled: true,
      ft8: true,
      cw: false,
      spots: true,
      classifier: true,
    });
    expect(normalizePrefs(null).enabled).toBe(true);
  });
  it('owns spot drawing only when available, enabled, and with spots on', () => {
    expect(decodeTagsOwnSpots({ enabled: true, spots: true }, true)).toBe(true);
    expect(decodeTagsOwnSpots({ enabled: false, spots: true }, true)).toBe(false);
    expect(decodeTagsOwnSpots({ enabled: true, spots: false }, true)).toBe(false);
    // Withheld: the plain spot labels are never displaced, whatever was saved.
    expect(decodeTagsOwnSpots({ enabled: true, spots: true }, false)).toBe(false);
  });

  it('is withheld from the product for now', () => {
    expect(DECODE_TAGS_AVAILABLE).toBe(false);
    expect(decodeTagsOwnSpots({ enabled: true, spots: true })).toBe(false);
  });
});
