// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// The NR5 / NR4 panels must follow tunables changed on the radio while they
// are open (a MIDI "Noise Reduction 5 Amount" / "Noise Reduction 4 Amount"
// knob, another client) — not only after the panel is reopened.

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { act, render } from '../meters/__tests__/harness';
import { NrSettingsSection } from './NrSettingsSection';
import { useConnectionStore } from '../../state/connection-store';

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(async () => ({ ok: false, status: 503, json: async () => ({}) }) as Response));
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function gaugeText(label: string): string | null | undefined {
  return document.querySelector(`[aria-label^="${label} "]`)?.textContent;
}

async function openPanel(mode: 'Nnr' | 'Sbnr') {
  const r = render(createElement(NrSettingsSection, { mode }));
  const title = document.querySelector('.nr-settings__title-btn') as HTMLButtonElement;
  if (title.getAttribute('aria-expanded') !== 'true') {
    await act(async () => title.click());
  }
  return r;
}

describe('NR settings panels follow the radio', () => {
  it('NR5 mask floor updates live when the radio value changes', async () => {
    const nr = useConnectionStore.getState().nr;
    useConnectionStore.setState({ nr: { ...nr, nnrMaskFloorDb: -25 } });
    const { unmount } = await openPanel('Nnr');
    expect(gaugeText('Mask floor')).toBe('-25');

    await act(async () => {
      useConnectionStore.setState({ nr: { ...useConnectionStore.getState().nr, nnrMaskFloorDb: -42 } });
    });
    expect(gaugeText('Mask floor')).toBe('-42');
    unmount();
  });

  it('NR4 reduction updates live when the radio value changes', async () => {
    const nr = useConnectionStore.getState().nr;
    useConnectionStore.setState({ nr: { ...nr, nr4ReductionAmount: 10 } });
    const { unmount } = await openPanel('Sbnr');
    const before = gaugeText('Reduction');
    await act(async () => {
      useConnectionStore.setState({ nr: { ...useConnectionStore.getState().nr, nr4ReductionAmount: 17 } });
    });
    const after = gaugeText('Reduction');
    expect(before).not.toBe(after);
    expect(after).toContain('17');
    unmount();
  });
});
