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
