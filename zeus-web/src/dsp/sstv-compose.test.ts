// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it } from 'vitest';
import { rgbaToRgbBase64 } from './sstv-compose';

describe('rgbaToRgbBase64', () => {
  it('drops alpha and keeps pixel order', () => {
    const rgba = new Uint8ClampedArray([1, 2, 3, 255, 4, 5, 6, 0]);
    const bytes = Array.from(atob(rgbaToRgbBase64(rgba)), (c) => c.charCodeAt(0));
    expect(bytes).toEqual([1, 2, 3, 4, 5, 6]);
  });

  it('handles pictures larger than one String.fromCharCode chunk', () => {
    const n = 800 * 616;                                   // PD 290
    const rgba = new Uint8ClampedArray(n * 4).map((_, i) => i % 251);
    expect(atob(rgbaToRgbBase64(rgba)).length).toBe(n * 3);
  });
});
