// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

import { describe, expect, it } from 'vitest';
import { classifyShape, digitalWindowAt, SignalClassifier, type ShapeFeatures } from './signal-classifier';

const f = (over: Partial<ShapeFeatures>): ShapeFeatures => ({
  widthHz: 100,
  hzPerPixel: 20,
  stationarity: 0.2,
  duty: 1,
  recentDuty: 1,
  transitions: 0,
  samples: 12,
  hzJitter: 0,
  fill: 1,
  digitalWindow: null,
  ...over,
});

describe('classifyShape', () => {
  it('needs enough history before guessing', () => {
    expect(classifyShape(f({ samples: 3 }))).toBeNull();
  });
  it('steady, always-on and narrow is a carrier', () => {
    expect(classifyShape(f({ stationarity: 0.9 }))).toBe('carrier');
  });
  it('narrow and keyed is CW', () => {
    expect(classifyShape(f({ duty: 0.5, transitions: 6 }))).toBe('cw');
  });
  it('narrow inside an FT8 window is FT8', () => {
    expect(classifyShape(f({ duty: 0.5, transitions: 4, digitalWindow: 'FT8' }))).toBe('ft8');
  });
  it('voice width is SSB', () => {
    expect(classifyShape(f({ widthHz: 2_600 }))).toBe('ssb');
  });
  it('a steady 6 kHz body is AM', () => {
    expect(classifyShape(f({ widthHz: 6_000, stationarity: 0.7 }))).toBe('am');
  });
  it('very wide is Wide', () => {
    expect(classifyShape(f({ widthHz: 20_000 }))).toBe('wide');
  });
  it('narrow is relative to the display resolution', () => {
    // At 190 Hz/bin a CW signal measures one or two bins wide.
    expect(classifyShape(f({ widthHz: 380, hzPerPixel: 190, duty: 0.4, transitions: 5 }))).toBe('cw');
  });
  it('a narrow signal that wanders in frequency is not CW (voice fragment)', () => {
    expect(classifyShape(f({ duty: 0.5, transitions: 6, hzJitter: 180 }))).toBeNull();
    expect(classifyShape(f({ stationarity: 0.9, hzJitter: 180 }))).toBeNull();
  });
  it('a sparse cluster of narrow signals is not SSB', () => {
    expect(classifyShape(f({ widthHz: 2_400, fill: 0.2 }))).toBeNull();
  });
  it('an in-between width gets no label', () => {
    expect(classifyShape(f({ widthHz: 900 }))).toBeNull();
  });
});

describe('digitalWindowAt', () => {
  it('knows the FT8 and FT4 windows', () => {
    expect(digitalWindowAt(14_075_500)).toBe('FT8');
    expect(digitalWindowAt(14_081_000)).toBe('FT4');
    expect(digitalWindowAt(14_200_000)).toBeNull();
  });
});

describe('SignalClassifier', () => {
  it('labels a steady carrier once the guess has held', () => {
    const c = new SignalClassifier();
    let out: ReturnType<SignalClassifier['update']> = [];
    for (let i = 0; i < 8; i++) out = c.update([{ hz: 7_100_000, widthHz: 40, stationarity: 0.95 }], i * 500, 20);
    expect(out).toHaveLength(1);
    expect(out[0]!.kind).toBe('carrier');
  });

  it('labels a keyed signal CW', () => {
    const c = new SignalClassifier();
    let out: ReturnType<SignalClassifier['update']> = [];
    for (let i = 0; i < 16; i++) {
      const on = i % 3 !== 0;
      out = c.update(on ? [{ hz: 7_025_000 + (i % 2) * 10, widthHz: 60, stationarity: 0.3 }] : [], i * 500, 20);
    }
    // The last update was "on" (15 % 3 === 0 → off; recheck on the next on).
    out = c.update([{ hz: 7_025_000, widthHz: 60, stationarity: 0.3 }], 16 * 500, 20);
    expect(out.map((s) => s.kind)).toEqual(['cw']);
  });

  it('voice peaks jumping around inside one passband never become CW', () => {
    const c = new SignalClassifier();
    const offsets = [0, 260, -180, 320, 90, -240, 200, -90, 300, -200, 140, 0, 280, -160, 220, 60];
    let out: ReturnType<SignalClassifier['update']> = [];
    offsets.forEach((off, i) => {
      const on = i % 4 !== 3; // speech pauses
      out = c.update(on ? [{ hz: 7_150_000 + off, widthHz: 80, stationarity: 0.2 }] : [], i * 500, 20);
    });
    expect(out.filter((s) => s.kind === 'cw' || s.kind === 'carrier')).toEqual([]);
  });

  it('a label does not outlive its signal on stray noise hits', () => {
    const c = new SignalClassifier();
    let t = 0;
    // A real keyed signal for 10 s…
    for (let i = 0; i < 20; i++, t += 500) {
      c.update(i % 3 !== 0 ? [{ hz: 3_844_500, widthHz: 40, stationarity: 0.3 }] : [], t, 7);
    }
    expect(c.update([{ hz: 3_844_500, widthHz: 40, stationarity: 0.3 }], t, 7).map((s) => s.kind)).toEqual(['cw']);
    t += 500;
    // …then it stops, and a noise spike lands near the old spot every few seconds.
    let out: ReturnType<SignalClassifier['update']> = [];
    for (let i = 0; i < 40; i++, t += 500) {
      out = c.update(i % 7 === 0 ? [{ hz: 3_844_500 + (i % 2 ? 20 : -20), widthHz: 14, stationarity: 0.1 }] : [], t, 7);
    }
    expect(out).toEqual([]);
  });

  it('forgets a signal that has gone', () => {
    const c = new SignalClassifier();
    for (let i = 0; i < 8; i++) c.update([{ hz: 7_100_000, widthHz: 40, stationarity: 0.95 }], i * 500, 20);
    expect(c.update([], 8 * 500 + 3_000, 20)).toEqual([]);
  });
});
