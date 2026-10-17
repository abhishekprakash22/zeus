// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it } from 'vitest';
import { isFreeDvEngaged } from './freedv-engaged';

describe('isFreeDvEngaged', () => {
  it('RX1 in FreeDV engages it', () => {
    expect(isFreeDvEngaged('FREEDV', 'USB', false)).toBe(true);
  });
  it('RX2 in FreeDV engages it only while RX2 is on', () => {
    expect(isFreeDvEngaged('USB', 'FREEDV', true)).toBe(true);
    expect(isFreeDvEngaged('USB', 'FREEDV', false)).toBe(false);
  });
  it('no receiver in FreeDV leaves it off', () => {
    expect(isFreeDvEngaged('LSB', 'CWU', true)).toBe(false);
  });
});
