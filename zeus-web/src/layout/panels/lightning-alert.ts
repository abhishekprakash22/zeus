// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { distanceKm } from '../../components/design/geo';

export interface LightningAlertStrike {
  lat: number;
  lon: number;
  t: number;
}

export interface LightningAlertHome {
  lat: number;
  lon: number;
}

export function pruneLightningAlertHistory(history: LightningAlertStrike[], cutoffMs: number): void {
  let drop = 0;
  while (drop < history.length && history[drop]!.t < cutoffMs) drop += 1;
  if (drop > 0) history.splice(0, drop);
}

export function countLightningAlertStrikes(
  history: readonly LightningAlertStrike[],
  home: LightningAlertHome | null | undefined,
  radiusKm: number,
): number {
  if (!home || !Number.isFinite(radiusKm) || radiusKm <= 0) return 0;
  let count = 0;
  for (const strike of history) {
    if (!Number.isFinite(strike.lat) || !Number.isFinite(strike.lon)) continue;
    if (distanceKm(home.lat, home.lon, strike.lat, strike.lon) <= radiusKm) {
      count += 1;
    }
  }
  return count;
}
