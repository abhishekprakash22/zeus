// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, type RenderResult } from './meters/__tests__/harness';
import { SettingsCheckPanel, verdictOf } from './SettingsCheckPanel';

let mounted: RenderResult | null = null;
afterEach(() => {
  mounted?.unmount();
  mounted = null;
  vi.unstubAllGlobals();
});

const flush = async () => {
  for (let i = 0; i < 5; i++) await Promise.resolve();
};

describe('settings check panel', () => {
  it('reads the verdict whether it arrives as a name or a number', () => {
    expect(verdictOf('Likely')).toBe('Likely');
    expect(verdictOf(2)).toBe('Likely');
    expect(verdictOf(1)).toBe('Check');
    expect(verdictOf('Ok')).toBe('Ok');
  });

  it('lists likely causes with their fix, and keeps the fine settings collapsed', async () => {
    const fetchMock = vi.fn(async (url: string) => {
      if (url.startsWith('/api/diagnostics/symptoms')) {
        return new Response(JSON.stringify([{ id: 'no-tx-power', label: 'No or low transmit power', group: 'Transmit' }]));
      }
      return new Response(JSON.stringify([
        { checkId: 'tx.drive', title: 'Drive', verdict: 'Likely', value: '5%', explanation: 'Low drive.', fix: 'Raise DRV.', manualRef: 'Transmitting' },
        { checkId: 'tx.filter', title: 'TX filter', verdict: 'Ok', value: '2800 Hz', explanation: 'x', fix: '—', manualRef: null },
      ]));
    });
    vi.stubGlobal('fetch', fetchMock);
    await act(async () => {
      mounted = render(createElement(SettingsCheckPanel));
      await flush();
    });
    const button = Array.from(mounted!.container.querySelectorAll('button')).find((b) => b.textContent === 'CHECK')!;
    await act(async () => {
      button.click();
      await flush();
    });
    expect(fetchMock).toHaveBeenCalledWith('/api/diagnostics/check?symptom=no-tx-power');
    const text = mounted!.container.textContent ?? '';
    expect(text).toContain('LIKELY CAUSE');
    expect(text).toContain('Raise DRV.');
    expect(text).toContain('Show the 1 setting that look fine');
    expect(mounted!.container.querySelector('[data-testid="check-ok"]')).toBeNull();
  });

  it('says so when nothing in the usual suspects is wrong', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) =>
      url.startsWith('/api/diagnostics/symptoms')
        ? new Response(JSON.stringify([{ id: 'rx-no-audio', label: 'No receive audio', group: 'Receive' }]))
        : new Response(JSON.stringify([{ checkId: 'rx.muted', title: 'RX1 mute', verdict: 0, value: 'not muted', explanation: '', fix: '', manualRef: null }]))));
    await act(async () => {
      mounted = render(createElement(SettingsCheckPanel));
      await flush();
    });
    const button = Array.from(mounted!.container.querySelectorAll('button')).find((b) => b.textContent === 'CHECK')!;
    await act(async () => {
      button.click();
      await flush();
    });
    expect(mounted!.container.textContent).toContain('Nothing in the usual suspects looks wrong');
  });
});
