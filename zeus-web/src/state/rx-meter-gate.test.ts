// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it } from 'vitest';
import { createRxMeterGate, RX_METER_UNKEY_HOLDOFF_MS } from './rx-meter-gate';

const off = { moxOn: false, tunOn: false, twoToneOn: false };

describe('rx meter gate', () => {
  it('accepts when never keyed', () => {
    const g = createRxMeterGate(() => 0);
    g.noteKeyState(off);
    expect(g.accepts()).toBe(true);
  });

  it('drops while MOX, TUNE or two-tone is on', () => {
    let t = 0;
    const g = createRxMeterGate(() => t);
    for (const k of [{ ...off, moxOn: true }, { ...off, tunOn: true }, { ...off, twoToneOn: true }]) {
      g.noteKeyState(k);
      expect(g.accepts()).toBe(false);
      g.noteKeyState(off);
      t += RX_METER_UNKEY_HOLDOFF_MS;
    }
  });

  it('holds off after unkey, then resumes', () => {
    let t = 1000;
    const g = createRxMeterGate(() => t);
    g.noteKeyState({ ...off, moxOn: true });
    t += 3000;
    g.noteKeyState(off);
    t += RX_METER_UNKEY_HOLDOFF_MS - 1;
    expect(g.accepts()).toBe(false);
    t += 1;
    expect(g.accepts()).toBe(true);
  });

  it('MOX to TUNE handover is not an unkey', () => {
    let t = 0;
    const g = createRxMeterGate(() => t);
    g.noteKeyState({ ...off, moxOn: true });
    g.noteKeyState({ ...off, tunOn: true });
    t += RX_METER_UNKEY_HOLDOFF_MS * 4;
    expect(g.accepts()).toBe(false);
  });
});
