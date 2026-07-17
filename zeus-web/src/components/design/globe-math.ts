// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Licensed under the GNU General Public License v2.0-or-later. See the LICENSE
// file at the repository root for the full text.

import { greatCirclePath } from './geo';

export type LonLat = { lon: number; lat: number };
export type GlobeView = { lon: number; lat: number; zoom: number };
export type ProjectedPoint = { x: number; y: number; visible: boolean; cosc: number };

const RAD = Math.PI / 180;
const DEG = 180 / Math.PI;

export function normalizeLon(lon: number): number {
  return ((lon + 540) % 360) - 180;
}

export function clampView(view: GlobeView): GlobeView {
  return {
    lon: normalizeLon(view.lon),
    lat: Math.max(-85, Math.min(85, view.lat)),
    zoom: Math.max(0.8, Math.min(2.5, view.zoom)),
  };
}

export function projectOrthographic(point: LonLat, view: GlobeView): ProjectedPoint {
  const lambda = point.lon * RAD;
  const phi = point.lat * RAD;
  const lambda0 = view.lon * RAD;
  const phi0 = view.lat * RAD;
  const dLambda = lambda - lambda0;
  const sinPhi = Math.sin(phi);
  const cosPhi = Math.cos(phi);
  const sinPhi0 = Math.sin(phi0);
  const cosPhi0 = Math.cos(phi0);
  const cosc = sinPhi0 * sinPhi + cosPhi0 * cosPhi * Math.cos(dLambda);
  return {
    x: cosPhi * Math.sin(dLambda),
    y: cosPhi0 * sinPhi - sinPhi0 * cosPhi * Math.cos(dLambda),
    visible: cosc >= -1e-8,
    cosc,
  };
}

export function unprojectOrthographic(x: number, y: number, view: GlobeView): LonLat | null {
  const rho = Math.hypot(x, y);
  if (rho > 1 + 1e-8) return null;
  const lambda0 = view.lon * RAD;
  const phi0 = view.lat * RAD;
  if (rho < 1e-10) return { lon: normalizeLon(view.lon), lat: view.lat };
  const c = Math.asin(Math.min(1, rho));
  const sinC = Math.sin(c);
  const cosC = Math.cos(c);
  const lat = Math.asin(cosC * Math.sin(phi0) + (y * sinC * Math.cos(phi0)) / rho);
  const lon = lambda0 + Math.atan2(
    x * sinC,
    rho * Math.cos(phi0) * cosC - y * Math.sin(phi0) * sinC,
  );
  return { lon: normalizeLon(lon * DEG), lat: lat * DEG };
}

export function clipVisibleSegments(points: readonly LonLat[], view: GlobeView): LonLat[][] {
  const segments: LonLat[][] = [];
  let current: LonLat[] = [];
  for (const point of points) {
    if (projectOrthographic(point, view).visible) {
      current.push(point);
    } else if (current.length > 0) {
      if (current.length > 1) segments.push(current);
      current = [];
    }
  }
  if (current.length > 1) segments.push(current);
  return segments;
}

export function solarSubpoint(date: Date): LonLat {
  const startOfYear = Date.UTC(date.getUTCFullYear(), 0, 0);
  const dayOfYear = (Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()) - startOfYear) / 86_400_000;
  const hoursUtc = date.getUTCHours() + date.getUTCMinutes() / 60 + date.getUTCSeconds() / 3600;
  const gamma = ((2 * Math.PI) / 365) * (dayOfYear - 1 + (hoursUtc - 12) / 24);
  const eqTime =
    229.18 *
    (0.000075 +
      0.001868 * Math.cos(gamma) -
      0.032077 * Math.sin(gamma) -
      0.014615 * Math.cos(2 * gamma) -
      0.040849 * Math.sin(2 * gamma));
  const decl =
    0.006918 -
    0.399912 * Math.cos(gamma) +
    0.070257 * Math.sin(gamma) -
    0.006758 * Math.cos(2 * gamma) +
    0.000907 * Math.sin(2 * gamma) -
    0.002697 * Math.cos(3 * gamma) +
    0.00148 * Math.sin(3 * gamma);
  const lon = normalizeLon((720 - hoursUtc * 60 - eqTime) / 4);
  return { lat: decl * DEG, lon };
}

export function angularDistanceDeg(a: LonLat, b: LonLat): number {
  const phi1 = a.lat * RAD;
  const phi2 = b.lat * RAD;
  const dPhi = (b.lat - a.lat) * RAD;
  const dLambda = (b.lon - a.lon) * RAD;
  const h = Math.sin(dPhi / 2) ** 2
    + Math.cos(phi1) * Math.cos(phi2) * Math.sin(dLambda / 2) ** 2;
  return 2 * Math.asin(Math.min(1, Math.sqrt(h))) * DEG;
}

function toVec(p: LonLat): [number, number, number] {
  const phi = p.lat * RAD;
  const lambda = p.lon * RAD;
  return [Math.cos(phi) * Math.cos(lambda), Math.cos(phi) * Math.sin(lambda), Math.sin(phi)];
}

function fromVec(v: [number, number, number]): LonLat {
  const len = Math.hypot(v[0], v[1], v[2]) || 1;
  const x = v[0] / len;
  const y = v[1] / len;
  const z = v[2] / len;
  return { lon: normalizeLon(Math.atan2(y, x) * DEG), lat: Math.asin(z) * DEG };
}

export function frameViewForPoints(points: readonly LonLat[], fallback: GlobeView): GlobeView {
  if (points.length === 0) return clampView(fallback);
  let sum: [number, number, number] = [0, 0, 0];
  for (const p of points) {
    const v = toVec(p);
    sum = [sum[0] + v[0], sum[1] + v[1], sum[2] + v[2]];
  }
  if (Math.hypot(sum[0], sum[1], sum[2]) < 1e-6)
    sum = toVec(points[0]!);
  const center = fromVec(sum);
  const maxAngle = Math.max(1, ...points.map((p) => angularDistanceDeg(center, p)));
  const zoom = Math.max(0.9, Math.min(2.3, 82 / (maxAngle + 18)));
  return clampView({ lon: center.lon, lat: center.lat, zoom });
}

export function interpolateView(a: GlobeView, b: GlobeView, t: number): GlobeView {
  const tt = Math.max(0, Math.min(1, t));
  let dLon = normalizeLon(b.lon - a.lon);
  if (Math.abs(dLon) > 180) dLon = dLon > 0 ? dLon - 360 : dLon + 360;
  return clampView({
    lon: a.lon + dLon * tt,
    lat: a.lat + (b.lat - a.lat) * tt,
    zoom: a.zoom + (b.zoom - a.zoom) * tt,
  });
}

export function easeInOutCubic(t: number): number {
  const x = Math.max(0, Math.min(1, t));
  return x < 0.5 ? 4 * x * x * x : 1 - ((-2 * x + 2) ** 3) / 2;
}

export function greatCircleArc(a: LonLat, b: LonLat, steps = 96): LonLat[] {
  return greatCirclePath({ lat: a.lat, lon: a.lon }, { lat: b.lat, lon: b.lon }, steps)
    .map(([lat, lon]) => ({ lat, lon: normalizeLon(lon) }));
}
