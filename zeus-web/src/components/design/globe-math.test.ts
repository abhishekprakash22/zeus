// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import {
  angularDistanceDeg,
  clipVisibleSegments,
  frameViewForPoints,
  greatCircleArc,
  projectOrthographic,
  solarSubpoint,
  unprojectOrthographic,
  type GlobeView,
} from './globe-math';

describe('globe math', () => {
  it('round-trips visible orthographic points', () => {
    const view: GlobeView = { lon: -72, lat: 41, zoom: 1 };
    const point = { lon: -3, lat: 52 };
    const projected = projectOrthographic(point, view);
    expect(projected.visible).toBe(true);
    const restored = unprojectOrthographic(projected.x, projected.y, view);
    expect(restored).not.toBeNull();
    expect(restored!.lon).toBeCloseTo(point.lon, 5);
    expect(restored!.lat).toBeCloseTo(point.lat, 5);
  });

  it('clips points on the far hemisphere', () => {
    const view: GlobeView = { lon: 0, lat: 0, zoom: 1 };
    const segments = clipVisibleSegments(
      [
        { lon: -20, lat: 0 },
        { lon: 0, lat: 0 },
        { lon: 20, lat: 0 },
        { lon: 170, lat: 0 },
        { lon: -170, lat: 0 },
      ],
      view,
    );
    expect(segments).toHaveLength(1);
    expect(segments[0]).toHaveLength(3);
  });

  it('places the sun near the equator at March equinox noon UTC', () => {
    const sub = solarSubpoint(new Date('2026-03-20T12:00:00Z'));
    expect(Math.abs(sub.lat)).toBeLessThan(1);
    expect(Math.abs(sub.lon)).toBeLessThan(5);
  });

  it('places the sun north at June solstice', () => {
    const sub = solarSubpoint(new Date('2026-06-21T12:00:00Z'));
    expect(sub.lat).toBeGreaterThan(22);
    expect(sub.lat).toBeLessThan(24.5);
  });

  it('samples a short great-circle arc between endpoints', () => {
    const arc = greatCircleArc({ lat: 41, lon: -72 }, { lat: 52, lon: -3 }, 8);
    expect(arc[0]).toMatchObject({ lat: 41, lon: -72 });
    expect(arc.at(-1)!.lat).toBeCloseTo(52, 6);
    expect(arc.at(-1)!.lon).toBeCloseTo(-3, 6);
    expect(arc.length).toBe(9);
  });

  it('frames two points near their spherical midpoint', () => {
    const a = { lat: 41, lon: -72 };
    const b = { lat: 52, lon: -3 };
    const view = frameViewForPoints([a, b], { lon: 0, lat: 0, zoom: 1 });
    expect(angularDistanceDeg(view, a)).toBeLessThan(45);
    expect(angularDistanceDeg(view, b)).toBeLessThan(45);
    expect(view.zoom).toBeGreaterThan(1);
  });
});
