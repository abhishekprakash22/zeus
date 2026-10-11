// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

import { describe, expect, it } from 'vitest';
import {
  formatLoupeHz,
  isLoupeCancelZone,
  LOUPE_HEIGHT_PX,
  LOUPE_LIFT_PX,
  LOUPE_WIDTH_PX,
  loupeCommitHz,
  loupePanWindow,
  loupeWfWindow,
  placeLoupe,
  sampleRowDb,
  traceRgba,
  wfLutIndex,
} from './tuning-loupe';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { pushLoupeRow, type LoupeRow } from '../state/loupe-history';
import { useRxDbWindowStore } from '../state/rx-db-window-store';

describe('placeLoupe', () => {
  it('sits centred above the finger, clear of it', () => {
    const p = placeLoupe(640, 600, 1280, 800);
    expect(p.below).toBe(false);
    expect(p.left).toBe(640 - LOUPE_WIDTH_PX / 2);
    expect(p.top + LOUPE_HEIGHT_PX).toBe(600 - LOUPE_LIFT_PX);
  });

  it('drops below the finger when there is no room above', () => {
    const p = placeLoupe(640, 120, 1280, 800);
    expect(p.below).toBe(true);
    expect(p.top).toBe(120 + LOUPE_LIFT_PX);
  });

  it('stays inside the screen at the edges', () => {
    expect(placeLoupe(2, 600, 1280, 800).left).toBeGreaterThanOrEqual(0);
    const r = placeLoupe(1278, 600, 1280, 800);
    expect(r.left + LOUPE_WIDTH_PX).toBeLessThanOrEqual(1280);
  });
});

describe('isLoupeCancelZone', () => {
  const rect = { left: 0, right: 200, top: 0, bottom: 100 };
  it('tunes anywhere over the display and a little above or below it', () => {
    expect(isLoupeCancelZone(100, 50, rect)).toBe(false);
    expect(isLoupeCancelZone(100, -40, rect)).toBe(false);
    expect(isLoupeCancelZone(100, 140, rect)).toBe(false);
  });
  it('cancels off either side or well away vertically', () => {
    expect(isLoupeCancelZone(-1, 50, rect)).toBe(true);
    expect(isLoupeCancelZone(201, 50, rect)).toBe(true);
    expect(isLoupeCancelZone(100, 160, rect)).toBe(true);
  });
});

describe('loupeCommitHz', () => {
  it('lands on the tuning-step grid', () => {
    expect(loupeCommitHz(14_074_013, 10)).toBe(14_074_010);
    expect(loupeCommitHz(14_074_013, 1)).toBe(14_074_013);
    expect(loupeCommitHz(14_074_260, 500)).toBe(14_074_500);
  });
  it('rounds to whole Hz for a missing step and refuses junk', () => {
    expect(loupeCommitHz(7_000_000.6, 0)).toBe(7_000_001);
    expect(loupeCommitHz(Number.NaN, 10)).toBe(0);
    expect(loupeCommitHz(-5, 10)).toBe(0);
  });
});

describe('sampleRowDb', () => {
  const bins = Float32Array.from([-120, -100, -80, -100]);
  // 4 bins x 100 Hz centred on 1 000 000 Hz: bin i at 1e6 + (i - 1.5) * 100.
  it('reads a bin at its centre and interpolates between bins', () => {
    expect(sampleRowDb(bins, 1_000_000, 100, 1_000_050)).toBeCloseTo(-80);
    expect(sampleRowDb(bins, 1_000_000, 100, 1_000_000)).toBeCloseTo(-90);
  });
  it('is NaN off the row', () => {
    expect(Number.isNaN(sampleRowDb(bins, 1_000_000, 100, 999_000))).toBe(true);
    expect(Number.isNaN(sampleRowDb(bins, 1_000_000, 0, 1_000_000))).toBe(true);
  });
});

describe('loupe colour and scale follow the receiver displays', () => {
  it('waterfall: the shader mapping over the receiver window, not the slice floor..peak', () => {
    useDisplaySettingsStore.setState({ wfDbMin: -140, wfDbMax: -50, wfTxDbMin: -70, wfTxDbMax: 30 });
    useRxDbWindowStore.setState({ overrides: {} });
    const rx1 = loupeWfWindow(0, false);
    expect(rx1).toEqual({ dbMin: -140, dbMax: -50 });
    // A -120 dB noise floor lands at ~22% of the palette — the same colour
    // the waterfall gives it — instead of the palette's black bottom.
    expect(wfLutIndex(-120, rx1.dbMin, rx1.dbMax)).toBe(57);
    expect(wfLutIndex(-200, rx1.dbMin, rx1.dbMax)).toBe(0);
    expect(wfLutIndex(0, rx1.dbMin, rx1.dbMax)).toBe(255);
    expect(loupeWfWindow(0, true)).toEqual({ dbMin: -70, dbMax: 30 });
  });

  it('trace: the panadapter window per receiver, TX range while keyed', () => {
    useDisplaySettingsStore.setState({
      dbMin: -135, dbMax: -55, rx2DbMin: -128, rx2DbMax: -48, txDbMin: -80, txDbMax: 20,
    });
    expect(loupePanWindow(0, false)).toEqual({ dbMin: -135, dbMax: -55 });
    expect(loupePanWindow(1, false)).toEqual({ dbMin: -128, dbMax: -48 });
    expect(loupePanWindow(0, true)).toEqual({ dbMin: -80, dbMax: 20 });
  });

  it('draws the trace in the operator colour', () => {
    expect(traceRgba('#FFA028', 1)).toBe('rgba(255,160,40,1)');
    expect(traceRgba('junk', 0.5)).toBe('rgba(255,160,40,0.5)');
  });
});

describe('formatLoupeHz', () => {
  it('groups like the VFO', () => {
    expect(formatLoupeHz(14_074_010)).toBe('14.074.010');
    expect(formatLoupeHz(1_840_000)).toBe('1.840.000');
  });
});

describe('pushLoupeRow', () => {
  it('keeps the newest rows first and bounds the ring', () => {
    const rows: LoupeRow[] = [];
    for (let i = 0; i < 5; i++) pushLoupeRow(rows, { centerHz: i, hzPerPixel: 1, bins: new Float32Array(1) }, 3);
    expect(rows.map((r) => r.centerHz)).toEqual([4, 3, 2]);
  });
});
