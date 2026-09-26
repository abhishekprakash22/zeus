// SPDX-License-Identifier: GPL-2.0-or-later

import { beforeEach, describe, expect, it } from 'vitest';
import { useWsprStore, type WsprSpotBatch } from './wspr-store';

const SLOT_MS = 120_000;

function batch(slot: number, messages: string[]): WsprSpotBatch {
  return {
    receiver: 0,
    slotStartUnixMs: slot * SLOT_MS,
    dialFreqMhz: 14.0956,
    spots: messages.map((message) => ({
      snrDb: -20,
      dtSec: 0.5,
      freqMhz: 14.0971,
      driftHz: 0,
      message,
    })),
  };
}

describe('wspr-store slots', () => {
  beforeEach(() => useWsprStore.getState().clear());

  it('keeps empty slots so the table can draw their separator', () => {
    const { ingest } = useWsprStore.getState();
    ingest(batch(1, ['EA5IUE IM76 23']));
    ingest(batch(2, []));
    const slots = useWsprStore.getState().slots;
    expect(slots.map((s) => s.slotStartUnixMs)).toEqual([2 * SLOT_MS, SLOT_MS]);
    expect(slots[0]!.rows).toHaveLength(0);
    expect(slots[1]!.rows[0]).toMatchObject({ callsign: 'EA5IUE', grid: 'IM76', powerDbm: 23 });
  });

  it('replaces a slot delivered twice instead of duplicating it', () => {
    const { ingest } = useWsprStore.getState();
    ingest(batch(1, []));
    ingest(batch(1, ['K1ABC FN42 37']));
    const slots = useWsprStore.getState().slots;
    expect(slots).toHaveLength(1);
    expect(slots[0]!.rows).toHaveLength(1);
  });

  it('drops whole old slots once past the spot cap', () => {
    const { ingest } = useWsprStore.getState();
    const many = Array.from({ length: 300 }, () => 'K1ABC FN42 37');
    ingest(batch(1, many));
    ingest(batch(2, many));
    const slots = useWsprStore.getState().slots;
    expect(slots.map((s) => s.slotStartUnixMs)).toEqual([2 * SLOT_MS]);
  });

  it('caps the number of slots', () => {
    const { ingest } = useWsprStore.getState();
    for (let i = 1; i <= 400; i++) ingest(batch(i, []));
    const slots = useWsprStore.getState().slots;
    expect(slots).toHaveLength(360);
    expect(slots[0]!.slotStartUnixMs).toBe(400 * SLOT_MS);
  });
});
