// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { act, render } from '../components/meters/__tests__/harness';
import { DecodeTagOverlay } from './DecodeTagOverlay';
import { SpotOverlay } from '../components/SpotOverlay';
import { useDecodeTagPrefs } from './decode-tag-prefs';
import { useDisplayStore } from '../state/display-store';
import { useFt8Store } from '../state/ft8-store';
import { useSpotStore } from '../state/spot-store';
import { useOperatorStore } from '../state/operator-store';
import { _resetCallStatusForTest, statusKey, useCallStatusStore } from './call-status-store';

const NOW = Date.now();
const SLOT = NOW - 5_000;

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(async () => ({ ok: false, json: async () => ({}) }) as Response));
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({
    width: 1000, height: 300, top: 0, left: 0, right: 1000, bottom: 300, x: 0, y: 0, toJSON: () => ({}),
  } as DOMRect);
  _resetCallStatusForTest();
  useDecodeTagPrefs.setState({ enabled: true, ft8: true, cw: true, spots: true, classifier: false, hydrated: true });
  useDisplayStore.setState({ centerHz: 14_075_000n, hzPerPixel: 10, panDb: new Float32Array(1000) });
  useOperatorStore.setState({ resolvedCall: 'K1ME' });
  useFt8Store.setState({
    rows: [
      { id: 'a', receiver: 0, protocol: 'FT8', slotStartUnixMs: SLOT, snrDb: -8, dtSec: 0, freqHz: 1000, score: 1, text: 'CQ DL1ABC JO62' },
      { id: 'b', receiver: 0, protocol: 'FT8', slotStartUnixMs: SLOT, snrDb: -12, dtSec: 0, freqHz: 2000, score: 1, text: 'K1ME JA1XYZ PM95' },
    ],
    slots: [{ id: 's', receiver: 0, protocol: 'FT8', slotStartUnixMs: SLOT, dialHz: 14_074_000, count: 2 }],
  });
  useSpotStore.setState({ spots: [{ callsign: 'VK2ABC', mode: 'SSB', freqHz: 14_078_000, argb: 0 }] });
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

function tag(call: string): HTMLElement | undefined {
  return Array.from(document.querySelectorAll<HTMLElement>('.dtag')).find((e) => e.textContent?.includes(call));
}

describe('DecodeTagOverlay', () => {
  it('tags FT8 senders and spots, with status colours from the log', async () => {
    useCallStatusStore.setState({
      byKey: {
        [statusKey('DL1ABC', 14_075_000)]: { status: 'NewBand', entity: 'Germany', workedAnyBand: false, at: Date.now() },
        [statusKey('VK2ABC', 14_078_000)]: { status: 'Worked', entity: 'Australia', workedAnyBand: true, at: Date.now() },
      },
    });
    const { unmount } = render(createElement(DecodeTagOverlay));
    await act(async () => {});
    expect(tag('DL1ABC')?.className).toContain('dtag--newband');
    expect(tag('DL1ABC')?.textContent).toContain('CQ');
    expect(tag('JA1XYZ')?.className).toContain('dtag--me'); // calling my call
    expect(tag('VK2ABC')?.className).toContain('dtag--worked');
    // DL1ABC at 14.075000 sits in the middle of the 1000 px pan.
    expect(tag('DL1ABC')?.style.left).toBe('500px');
    unmount();
  });

  it('draws nothing when switched off', async () => {
    useDecodeTagPrefs.setState({ enabled: false });
    const { unmount } = render(createElement(DecodeTagOverlay));
    await act(async () => {});
    expect(document.querySelectorAll('.dtag')).toHaveLength(0);
    unmount();
  });

  it('while the feature is withheld, the plain spot labels are untouched', async () => {
    // Saved prefs say "tags on, spots on" — the spot labels still draw,
    // because nothing else is drawing them.
    const a = render(createElement(SpotOverlay));
    expect(document.body.textContent).toContain('VK2ABC');
    a.unmount();
  });
});
