// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

import { describe, expect, it } from 'vitest';
import { isPlausibleCallsign, parseMemoryList } from './memories';
import { memoryNear } from '../state/memory-store';

describe('parseMemoryList', () => {
  it('keeps valid channels sorted and drops junk', () => {
    const list = parseMemoryList([
      { id: 'b', name: 'B', hz: 14_200_000, mode: 'USB', filterLowHz: 100, filterHighHz: 2800, createdUnixMs: 1 },
      { id: 'a', name: 'A', hz: 7_100_000, mode: 'LSB', filterLowHz: null, filterHighHz: null, createdUnixMs: 2 },
      { id: 'x', hz: 'nope', mode: 'USB' },
      { id: 'y', hz: 7_000_000, mode: 'WAT' },
      null,
      42,
    ]);
    expect(list.map((m) => m.id)).toEqual(['a', 'b']);
    expect(list[0]!.filterLowHz).toBeNull();
    expect(list[1]!.filterHighHz).toBe(2800);
  });

  it('returns empty for a non-array', () => {
    expect(parseMemoryList({ error: 'x' })).toEqual([]);
  });
});

describe('isPlausibleCallsign', () => {
  it.each(['W1AW', 'dl1abc/p', 'VK2/G4ABC', '3D2AG'])('accepts %s', (c) => expect(isPlausibleCallsign(c)).toBe(true));
  it.each(['', 'AB', 'NODIGIT', 'W1 AW', 'W1AW;'])('rejects %s', (c) => expect(isPlausibleCallsign(c)).toBe(false));
});

describe('memoryNear', () => {
  const mk = (id: string, hz: number) => ({ id, name: id, hz, mode: 'USB' as const, filterLowHz: null, filterHighHz: null, createdUnixMs: 0 });
  it('finds the nearest channel inside the radius', () => {
    const list = [mk('a', 14_000_000), mk('b', 14_000_300)];
    expect(memoryNear(list, 14_000_200, 200)?.id).toBe('b');
    expect(memoryNear(list, 14_001_000, 200)).toBeNull();
  });
});
