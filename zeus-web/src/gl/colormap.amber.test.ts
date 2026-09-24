import { describe, expect, it } from 'vitest';
import { COLORMAPS, lutFor } from './colormap';

describe('amber palette', () => {
  it('is a first-class waterfall palette', () => {
    expect(COLORMAPS.map((c) => c.id)).toContain('amber');
  });
  it('is dark at the floor, monotonic in brightness, white at the top', () => {
    const lut = lutFor('amber');
    const lum = (i: number) => 0.2126 * lut[i * 4]! + 0.7152 * lut[i * 4 + 1]! + 0.0722 * lut[i * 4 + 2]!;
    expect(lum(0)).toBeLessThan(2);
    expect(lum(255)).toBeGreaterThan(250);
    let prev = -1;
    for (let i = 0; i < 256; i += 4) {
      const l = lum(i);
      expect(l).toBeGreaterThanOrEqual(prev - 0.5);
      prev = l;
    }
  });
});

describe('greyscale and rainbow palettes', () => {
  it('are first-class waterfall palettes', () => {
    const ids = COLORMAPS.map((c) => c.id);
    expect(ids).toContain('grey');
    expect(ids).toContain('rainbow');
  });
  it('greyscale is neutral and monotonic', () => {
    const lut = lutFor('grey');
    let prev = -1;
    for (let i = 0; i < 256; i += 4) {
      const r = lut[i * 4]!, g = lut[i * 4 + 1]!, b = lut[i * 4 + 2]!;
      expect(Math.abs(r - g)).toBeLessThanOrEqual(12);
      expect(Math.abs(g - b)).toBeLessThanOrEqual(12);
      expect(r).toBeGreaterThanOrEqual(prev - 1);
      prev = r;
    }
    expect(lut[255 * 4]).toBe(255);
  });
  it('rainbow has a bright floor, not a dark one', () => {
    // At 40% of the range — where the other palettes are still near black —
    // rainbow is already a strong blue. That is the "very bright" request.
    const lut = lutFor('rainbow');
    const i = Math.round(0.40 * 255) * 4;
    expect(lut[i + 2]!).toBeGreaterThan(200);
    expect(lut[255 * 4]).toBe(255);
  });
});
