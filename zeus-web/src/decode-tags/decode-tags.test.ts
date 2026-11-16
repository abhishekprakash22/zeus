// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

import { describe, expect, it } from 'vitest';
import type { Ft8Row, Ft8SlotMark } from '../state/ft8-store';
import type { CwSkimChannel } from '../state/cw-skim-store';
import {
  cwTags,
  extractCwCallsign,
  ft8Tags,
  FT8_TAG_WINDOW_MS,
  isCallsign,
  layoutLanes,
  mergeTags,
  skimPitchToHz,
  spotTags,
  tagPriority,
  type DecodeTag,
} from './decode-tags';

const NOW = 1_800_000_000_000;
const SLOT = NOW - 10_000;

function row(text: string, freqHz: number, over: Partial<Ft8Row> = {}): Ft8Row {
  return {
    id: `${text}-${freqHz}`,
    receiver: 0,
    protocol: 'FT8',
    slotStartUnixMs: SLOT,
    snrDb: -10,
    dtSec: 0.1,
    freqHz,
    score: 1,
    text,
    ...over,
  };
}

const slot = (slotStartUnixMs = SLOT, dialHz = 14_074_000): Ft8SlotMark => ({
  id: String(slotStartUnixMs),
  receiver: 0,
  protocol: 'FT8',
  slotStartUnixMs,
  dialHz,
  count: 1,
});

describe('isCallsign', () => {
  it.each(['W1AW', 'DL1ABC', '3D2AG', 'E51ABC', '9A1A', 'VK2/G4ABC', 'K1ABC/P', 'JA1XYZ'])('accepts %s', (c) =>
    expect(isCallsign(c)).toBe(true),
  );
  it.each(['FN42', '5NN', '599', 'CQ', 'DE', 'TEST', 'K', 'TU'])('rejects %s', (c) => expect(isCallsign(c)).toBe(false));
});

describe('extractCwCallsign', () => {
  it('takes the call after DE', () => {
    expect(extractCwCallsign('CQ CQ DE DL1ABC DL1ABC K')).toBe('DL1ABC');
  });
  it('the last DE wins (the other station signing)', () => {
    expect(extractCwCallsign('DL1ABC DE W1AW 5NN TU  W1AW DE DL1ABC 5NN')).toBe('DL1ABC');
  });
  it('takes a call after a CQ run (contest style)', () => {
    expect(extractCwCallsign('CQ TEST K3LR')).toBe('K3LR');
  });
  it('accepts a call sent twice', () => {
    expect(extractCwCallsign('R5 K1ABC UR 599 K1ABC')).toBe('K1ABC');
  });
  it('ignores a single stray call-shaped fragment', () => {
    expect(extractCwCallsign('EE T5ETN IE TEE')).toBeNull();
  });
});

describe('ft8Tags', () => {
  it('places the sender at dial + audio offset and flags CQ and calls to me', () => {
    const tags = ft8Tags(
      [row('CQ DL1ABC JO62', 1200), row('K1ME W1AW FN31', 800)],
      [slot()],
      NOW,
      'k1me',
    );
    const dl = tags.find((t) => t.call === 'DL1ABC')!;
    expect(dl.hz).toBe(14_075_200);
    expect(dl.cq).toBe(true);
    const w1 = tags.find((t) => t.call === 'W1AW')!;
    expect(w1.me).toBe(true);
    expect(w1.hz).toBe(14_074_800);
  });

  it('drops decodes older than two slots, unknown dials and other receivers', () => {
    const old = NOW - FT8_TAG_WINDOW_MS - 1_000;
    const tags = ft8Tags(
      [
        row('CQ DL1ABC JO62', 1200, { slotStartUnixMs: old }),
        row('CQ G4XYZ IO91', 900, { slotStartUnixMs: NOW - 5_000 }),
        row('CQ JA1ABC PM95', 700, { receiver: 1 }),
      ],
      [slot(old), slot(SLOT)],
      NOW,
      null,
    );
    expect(tags).toEqual([]);
  });

  it('keeps the newest decode per call', () => {
    const newer = SLOT + 15_000;
    const tags = ft8Tags(
      [row('CQ DL1ABC JO62', 1200), row('W1AW DL1ABC -05', 1250, { slotStartUnixMs: newer })],
      [slot(), slot(newer)],
      NOW + 15_000,
      null,
    );
    expect(tags).toHaveLength(1);
    expect(tags[0]!.hz).toBe(14_075_250);
  });
});

describe('cw tags', () => {
  it('maps skimmer pitch to RF per mode', () => {
    expect(skimPitchToHz('CWU', 14_025_000, 600, 700)).toBe(14_025_100);
    expect(skimPitchToHz('CWL', 7_025_000, 600, 700)).toBe(7_024_900);
    expect(skimPitchToHz('AM', 7_025_000, 600, 700)).toBeNull();
  });

  it('tags channels that identified a call', () => {
    const ch = (id: number, pitchHz: number, text: string): CwSkimChannel => ({
      id,
      pitchHz,
      snrDb: 18,
      active: true,
      decodable: true,
      text,
      lastCharAt: NOW - 1_000,
    });
    const tags = cwTags({ 1: ch(1, 650, 'CQ CQ DE OK1ABC OK1ABC K'), 2: ch(2, 900, 'EEE TTT') }, NOW, 'CWU', 14_030_000, 600);
    expect(tags).toHaveLength(1);
    expect(tags[0]).toMatchObject({ call: 'OK1ABC', hz: 14_030_050, cq: true });
  });
});

describe('mergeTags', () => {
  const t = (call: string, hz: number, source: DecodeTag['sources'][number], seenAt = NOW): DecodeTag => ({
    key: `${source}:${call}`,
    call,
    hz,
    sources: [source],
    cq: false,
    me: false,
    detail: source,
    seenAt,
  });

  it('merges one call heard by a decoder and spotted, at the decoded frequency', () => {
    const m = mergeTags([t('DL1ABC', 14_025_100, 'cw')], spotTags([{ callsign: 'DL1ABC', mode: 'CW', freqHz: 14_025_000, argb: 0 }], NOW));
    expect(m).toHaveLength(1);
    expect(m[0]!.sources).toEqual(['cw', 'spot']);
    expect(m[0]!.hz).toBe(14_025_100);
  });

  it('keeps the same call on two frequencies apart', () => {
    expect(mergeTags([t('DL1ABC', 14_025_000, 'cw')], [t('DL1ABC', 14_030_000, 'spot')])).toHaveLength(2);
  });
});

describe('priority and lanes', () => {
  const base: DecodeTag = { key: 'k', call: 'X1X', hz: 0, sources: ['ft8'], cq: false, me: false, detail: '', seenAt: 0 };

  it('ranks calling-me, new DXCC, new band, needed CQ, needed, unknown, worked', () => {
    const order = [
      tagPriority({ ...base, me: true }, 'Worked'),
      tagPriority(base, 'NewDxcc'),
      tagPriority(base, 'NewBand'),
      tagPriority({ ...base, cq: true }, 'Needed'),
      tagPriority(base, 'Needed'),
      tagPriority(base, null),
      tagPriority(base, 'Worked'),
    ];
    expect([...order].sort((a, b) => a - b)).toEqual(order);
  });

  it('stacks overlapping labels into lanes and drops the least important', () => {
    const items = [
      { item: 'worked', x: 100, width: 60, priority: 6 },
      { item: 'dxcc', x: 110, width: 60, priority: 1 },
      { item: 'needed', x: 105, width: 60, priority: 4 },
      { item: 'far', x: 400, width: 60, priority: 4 },
    ];
    const placed = layoutLanes(items, 2);
    const lane = Object.fromEntries(placed.map((p) => [p.item, p.lane]));
    expect(lane).toEqual({ dxcc: 0, needed: 1, far: 0 });
  });
});
