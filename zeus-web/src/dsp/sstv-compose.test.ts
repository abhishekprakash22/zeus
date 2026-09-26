// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it } from 'vitest';
import { rgbaToRgbBase64, sstvHeaderHeight, zeusHeaderVersion } from './sstv-compose';

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

describe('header strip', () => {
  it('shows the release, not the build hash', () => {
    expect(zeusHeaderVersion('0.10.9-dev+047710f5997c8c5cf4cd4dfebe7c532b908e8648')).toBe('Zeus v0.10.9-dev');
    expect(zeusHeaderVersion('1.2.0')).toBe('Zeus v1.2.0');
    expect(zeusHeaderVersion(null)).toBe('Zeus');
  });

  it('stays a thin strip at every mode height', () => {
    expect(sstvHeaderHeight(240)).toBe(17);      // Robot
    expect(sstvHeaderHeight(256)).toBe(18);      // Martin / Scottie
    expect(sstvHeaderHeight(616)).toBe(44);      // PD 290
  });
});
