// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { DIGITAL_PROTECTED_RANGES, isInProtectedDigitalSegment } from './digital-segments';

describe('digital protected segments', () => {
  it('protects the 20 m FT8 sub-band around its dial frequency', () => {
    expect(isInProtectedDigitalSegment(14_074_000)).toBe(true); // dial
    expect(isInProtectedDigitalSegment(14_075_500)).toBe(true); // mid-segment audio
    expect(isInProtectedDigitalSegment(14_076_900)).toBe(true); // top of the 3 kHz
  });

  it('protects the 20 m FT4 sub-band', () => {
    expect(isInProtectedDigitalSegment(14_080_000)).toBe(true);
    expect(isInProtectedDigitalSegment(14_081_500)).toBe(true);
  });

  it('protects FT8 on every supported band', () => {
    for (const dial of [
      1_840_000, 3_573_000, 7_074_000, 10_136_000, 14_074_000, 18_100_000,
      21_074_000, 24_915_000, 28_074_000, 50_313_000, 144_174_000,
    ]) {
      expect(isInProtectedDigitalSegment(dial + 1_500)).toBe(true);
    }
  });

  it('leaves the rest of the band open', () => {
    expect(isInProtectedDigitalSegment(14_200_000)).toBe(false); // SSB calling area
    expect(isInProtectedDigitalSegment(14_060_000)).toBe(false); // below FT8
    expect(isInProtectedDigitalSegment(7_000_000)).toBe(false);
  });

  it('keeps the ranges sorted and well-formed', () => {
    for (let i = 0; i < DIGITAL_PROTECTED_RANGES.length; i++) {
      const r = DIGITAL_PROTECTED_RANGES[i]!;
      expect(r.highHz).toBeGreaterThan(r.lowHz);
      if (i > 0) expect(r.lowHz).toBeGreaterThanOrEqual(DIGITAL_PROTECTED_RANGES[i - 1]!.lowHz);
    }
  });
});
