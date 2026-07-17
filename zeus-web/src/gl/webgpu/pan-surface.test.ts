// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';

import { panSurfaceSourceUForTest, panSurfaceViewSpanHzForTest } from './pan-surface';

describe('WebGPU 3D panadapter mapping', () => {
  it('keeps downsampled rows registered to the original frame span', () => {
    const sourceWidth = 2048;
    const textureWidth = 1024;
    const frameHzPerPixel = 10;
    const rowHzPerPixel = (sourceWidth * frameHzPerPixel) / textureWidth;
    const viewSpanHz = panSurfaceViewSpanHzForTest(frameHzPerPixel, sourceWidth, textureWidth);

    expect(viewSpanHz).toBe(20_480);
    expect(panSurfaceSourceUForTest(0.25, 0, viewSpanHz, 0, rowHzPerPixel, textureWidth)).toBeCloseTo(0.25);
    expect(panSurfaceSourceUForTest(0.50, 0, viewSpanHz, 0, rowHzPerPixel, textureWidth)).toBeCloseTo(0.50);
    expect(panSurfaceSourceUForTest(0.75, 0, viewSpanHz, 0, rowHzPerPixel, textureWidth)).toBeCloseTo(0.75);
  });

  it('scales source sampling around center during animated zoom', () => {
    const sourceWidth = 2048;
    const textureWidth = 1024;
    const frameHzPerPixel = 10;
    const zoomedHzPerPixel = 5;
    const rowHzPerPixel = (sourceWidth * frameHzPerPixel) / textureWidth;
    const viewSpanHz = panSurfaceViewSpanHzForTest(zoomedHzPerPixel, sourceWidth, textureWidth);

    expect(viewSpanHz).toBe(10_240);
    expect(panSurfaceSourceUForTest(0, 0, viewSpanHz, 0, rowHzPerPixel, textureWidth)).toBeCloseTo(0.25);
    expect(panSurfaceSourceUForTest(0.5, 0, viewSpanHz, 0, rowHzPerPixel, textureWidth)).toBeCloseTo(0.5);
    expect(panSurfaceSourceUForTest(1, 0, viewSpanHz, 0, rowHzPerPixel, textureWidth)).toBeCloseTo(0.75);
  });
});
