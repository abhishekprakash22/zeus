// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

import { describe, expect, it } from 'vitest';
import {
  clampCenter,
  formatDialHz,
  radialItems,
  ringOffsets,
  RING_RADIUS_PX,
  spotFrequencyHz,
  type RadialEnv,
} from './radial-items';

const env: RadialEnv = { locked: false, hasRx2: true, clusterConnected: true, memoryHit: null };
const rx1 = { rxIndex: 0, panRxIndex: 0, carrierHz: 14_074_000 };

describe('radialItems', () => {
  it('offers split, RX2, notch, store, spot on RX1', () => {
    expect(radialItems(rx1, env).map((i) => i.id)).toEqual(['split', 'rx2', 'notch', 'memory', 'spot']);
  });

  it('drops RX1-only actions on a secondary receiver pane', () => {
    expect(radialItems({ rxIndex: 1, panRxIndex: 1, carrierHz: null }, env).map((i) => i.id)).toEqual([
      'memory',
      'spot',
    ]);
  });

  it('hides RX2 when the radio has one receiver', () => {
    expect(radialItems(rx1, { ...env, hasRx2: false }).map((i) => i.id)).not.toContain('rx2');
  });

  it('disables dial-moving actions under VFO lock but keeps notch / memory', () => {
    const items = radialItems(rx1, { ...env, locked: true });
    const byId = Object.fromEntries(items.map((i) => [i.id, i.disabled]));
    expect(byId).toMatchObject({ split: true, rx2: true, notch: false, memory: false });
  });

  it('turns Store into Forget over a stored channel', () => {
    const items = radialItems(rx1, {
      ...env,
      memoryHit: { id: 'a', name: 'net', hz: 14_074_000, mode: 'USB', filterLowHz: null, filterHighHz: null, createdUnixMs: 0 },
    });
    expect(items.map((i) => i.id)).toContain('forget');
    expect(items.map((i) => i.id)).not.toContain('memory');
  });

  it('offers a rewind step only while the rewind buffer holds something', () => {
    expect(radialItems(rx1, env).map((i) => i.id)).not.toContain('rewind');
    const item = radialItems(rx1, { ...env, rewindSeconds: 42 }).find((i) => i.id === 'rewind');
    expect(item?.label).toBe('−10 s');
    // Less than 10 s held: go back as far as there is.
    expect(radialItems(rx1, { ...env, rewindSeconds: 4.6 }).find((i) => i.id === 'rewind')?.label).toBe('−4 s');
    // Not on a secondary receiver's pane (the buffer is RX1's).
    expect(
      radialItems({ rxIndex: 1, panRxIndex: 1, carrierHz: null }, { ...env, rewindSeconds: 42 }).map((i) => i.id),
    ).not.toContain('rewind');
  });

  it('offers the hover-to-listen toggle on RX1, lit when on', () => {
    expect(radialItems(rx1, env).map((i) => i.id)).not.toContain('listen'); // not offered unless the state is known
    const off = radialItems(rx1, { ...env, hoverListen: false }).find((i) => i.id === 'listen');
    expect(off).toMatchObject({ label: 'Listen', on: false, disabled: false });
    expect(radialItems(rx1, { ...env, hoverListen: true }).find((i) => i.id === 'listen')?.on).toBe(true);
    // It stays available under VFO lock (it moves no dial), and sits before Spot.
    const ids = radialItems(rx1, { ...env, hoverListen: true, locked: true }).map((i) => i.id);
    expect(ids.indexOf('listen')).toBe(ids.indexOf('spot') - 1);
    // Not on a secondary receiver's pane.
    expect(
      radialItems({ rxIndex: 1, panRxIndex: 1, carrierHz: null }, { ...env, hoverListen: true }).map((i) => i.id),
    ).not.toContain('listen');
  });

  it('disables Spot without a cluster connection', () => {
    expect(radialItems(rx1, { ...env, clusterConnected: false }).find((i) => i.id === 'spot')?.disabled).toBe(true);
  });
});

describe('ring geometry', () => {
  it('starts at 12 o\'clock and runs clockwise at the ring radius', () => {
    const o = ringOffsets(4);
    expect(o[0]).toEqual({ dx: 0, dy: -RING_RADIUS_PX });
    expect(o[1]).toEqual({ dx: RING_RADIUS_PX, dy: 0 });
  });

  it('pulls the centre in from screen edges', () => {
    const c = clampCenter(2, 2, 1280, 800);
    expect(c.x).toBeGreaterThan(RING_RADIUS_PX);
    expect(c.y).toBeGreaterThan(RING_RADIUS_PX);
    expect(clampCenter(640, 400, 1280, 800)).toEqual({ x: 640, y: 400 });
  });
});

describe('formatting and spot frequency', () => {
  it('groups the dial readout', () => {
    expect(formatDialHz(14_074_250)).toBe('14.074 250');
    expect(formatDialHz(7_000_005)).toBe('7.000 005');
  });

  it('spots CW on the carrier and voice on the dial', () => {
    expect(spotFrequencyHz({ mode: 'CWU', carrierHz: 14_025_600, tuneHz: 14_025_000 })).toBe(14_025_600);
    expect(spotFrequencyHz({ mode: 'USB', carrierHz: 14_250_900, tuneHz: 14_250_000 })).toBe(14_250_000);
    expect(spotFrequencyHz({ mode: 'CWL', carrierHz: null, tuneHz: 7_025_000 })).toBe(7_025_000);
  });
});
