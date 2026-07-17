// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import {
  WIDEBAND_CENTER_HZ,
  WIDEBAND_SPAN_HZ,
  clampWidebandHzPerPixel,
  formatWidebandRangeLabel,
  formatWidebandZoomRatio,
  isWidebandDisplayGeometry,
  resolveSpectrumViewport,
  zoomWidebandViewport,
} from './wideband-view';

describe('wideband view geometry', () => {
  it('detects full 0-60 MHz display frames but not ordinary DDC frames', () => {
    expect(
      isWidebandDisplayGeometry({
        width: 2048,
        centerHz: WIDEBAND_CENTER_HZ,
        hzPerPixel: WIDEBAND_SPAN_HZ / 2048,
      }),
    ).toBe(true);

    expect(
      isWidebandDisplayGeometry({
        width: 2048,
        centerHz: 14_200_000,
        hzPerPixel: 192_000 / 2048,
      }),
    ).toBe(false);
  });

  it('resolves a local viewport only for wideband source frames', () => {
    const sourceHzPerPixel = WIDEBAND_SPAN_HZ / 2048;
    const wide = resolveSpectrumViewport({
      width: 2048,
      sourceCenterHz: WIDEBAND_CENTER_HZ,
      sourceHzPerPixel,
      viewCenterHz: 14_200_000,
      viewHzPerPixel: sourceHzPerPixel / 32,
    });

    expect(wide?.wideband).toBe(true);
    expect(wide?.centerHz).toBe(14_200_000);
    expect(wide?.hzPerPixel).toBe(sourceHzPerPixel / 32);

    const narrow = resolveSpectrumViewport({
      width: 2048,
      sourceCenterHz: 14_200_000,
      sourceHzPerPixel: 192_000 / 2048,
      viewCenterHz: 7_000_000,
      viewHzPerPixel: 1,
    });

    expect(narrow?.wideband).toBe(false);
    expect(narrow?.centerHz).toBe(14_200_000);
  });

  it('keeps the pointer frequency anchored across local zoom steps', () => {
    const width = 2048;
    const sourceHzPerPixel = WIDEBAND_SPAN_HZ / width;
    const currentCenterHz = WIDEBAND_CENTER_HZ;
    const pointerFraction = 0.25;
    const currentAnchorHz = currentCenterHz + (pointerFraction - 0.5) * WIDEBAND_SPAN_HZ;

    const next = zoomWidebandViewport({
      width,
      sourceHzPerPixel,
      currentHzPerPixel: sourceHzPerPixel,
      currentCenterHz,
      pointerFraction,
      zoomDelta: 1,
    });

    expect(next).not.toBeNull();
    const nextSpanHz = width * next!.hzPerPixel;
    const nextAnchorHz = next!.centerHz + (pointerFraction - 0.5) * nextSpanHz;
    expect(nextAnchorHz).toBeCloseTo(currentAnchorHz, 6);
    expect(next!.hzPerPixel).toBeLessThan(sourceHzPerPixel);
  });

  it('clamps local zoom depth and recenters when zooming back to full band', () => {
    const width = 2048;
    const sourceHzPerPixel = WIDEBAND_SPAN_HZ / width;
    expect(clampWidebandHzPerPixel(sourceHzPerPixel, sourceHzPerPixel / 1_000_000)).toBe(
      sourceHzPerPixel / 4096,
    );

    const next = zoomWidebandViewport({
      width,
      sourceHzPerPixel,
      currentHzPerPixel: sourceHzPerPixel / 2,
      currentCenterHz: 12_000_000,
      pointerFraction: 0.1,
      zoomDelta: -10,
    });

    expect(next?.hzPerPixel).toBe(sourceHzPerPixel);
    expect(next?.centerHz).toBe(WIDEBAND_CENTER_HZ);
  });

  it('formats full-band and zoomed ranges compactly', () => {
    const sourceHzPerPixel = WIDEBAND_SPAN_HZ / 2048;
    expect(formatWidebandRangeLabel(0, WIDEBAND_SPAN_HZ)).toBe('0-60 MHz');
    expect(formatWidebandRangeLabel(14_074_000, 14_350_000)).toBe('14.074-14.35 MHz');
    expect(formatWidebandZoomRatio(sourceHzPerPixel, sourceHzPerPixel)).toBe('1x');
    expect(formatWidebandZoomRatio(sourceHzPerPixel, sourceHzPerPixel / 32)).toBe('32x');
  });
});
