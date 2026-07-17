// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { describe, expect, it } from 'vitest';
import {
  countLightningAlertStrikes,
  pruneLightningAlertHistory,
  type LightningAlertStrike,
} from './lightning-alert';

describe('lightning alert proximity counting', () => {
  it('rechecks stored strikes against the current radius', () => {
    const home = { lat: 0, lon: 0 };
    const history: LightningAlertStrike[] = [
      { lat: 0.04, lon: 0, t: 1 }, // ~4.4 km
      { lat: 0.18, lon: 0, t: 2 }, // ~20 km
      { lat: 0.44, lon: 0, t: 3 }, // ~49 km
    ];

    expect(countLightningAlertStrikes(history, home, 100)).toBe(3);
    expect(countLightningAlertStrikes(history, home, 15)).toBe(1);
  });

  it('prunes strikes outside the rolling alert window', () => {
    const history: LightningAlertStrike[] = [
      { lat: 0, lon: 0, t: 1_000 },
      { lat: 0, lon: 0, t: 2_000 },
      { lat: 0, lon: 0, t: 3_000 },
    ];

    pruneLightningAlertHistory(history, 2_000);

    expect(history.map((strike) => strike.t)).toEqual([2_000, 3_000]);
  });
});
