// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { act, render } from '../components/meters/__tests__/harness';
import { RewindBar } from './RewindBar';
import { _resetTimeShiftForTest, EMPTY_STATUS, useTimeShiftStore } from './time-shift';

let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  _resetTimeShiftForTest();
  fetchMock = vi.fn(async () => ({ ok: true, json: async () => useTimeShiftStore.getState().status }) as Response);
  vi.stubGlobal('fetch', fetchMock);
});
afterEach(() => {
  _resetTimeShiftForTest();
  vi.unstubAllGlobals();
});

const on = { ...EMPTY_STATUS, enabled: true, capacitySeconds: 60, bufferedSeconds: 40, rateHz: 192_000 };
const button = (text: string) =>
  Array.from(document.querySelectorAll<HTMLButtonElement>('.rw-bar button')).find((b) => b.textContent?.includes(text))!;

describe('RewindBar', () => {
  it('is absent while rewind is off', () => {
    useTimeShiftStore.setState({ status: EMPTY_STATUS, hydrated: true });
    const { unmount } = render(createElement(RewindBar));
    expect(document.querySelector('.rw-bar')).toBeNull();
    unmount();
  });

  it('shows what is held and offers to go back; LIVE and pause are idle', () => {
    useTimeShiftStore.setState({ status: on, hydrated: true });
    const { unmount } = render(createElement(RewindBar));
    expect(document.querySelector('.rw-read')?.textContent).toBe('40 s');
    expect(button('⏪').disabled).toBe(false);
    expect(button('LIVE').disabled).toBe(true);
    expect(document.querySelector('.rw-badge')).toBeNull();
    unmount();
  });

  it('says plainly when the audio is a replay, and why it may be silent', async () => {
    useTimeShiftStore.setState({ status: { ...on, replaying: true, delaySeconds: 12.5 }, hydrated: true });
    const { unmount } = render(createElement(RewindBar));
    const badge = document.querySelector('.rw-badge')!;
    expect(badge.textContent).toContain('REPLAY');
    expect(badge.textContent).toContain('−12.5 s');
    expect(badge.textContent).toContain('not live');
    await act(async () => {
      useTimeShiftStore.setState({ status: { ...on, replaying: true, delaySeconds: 12.5, outOfWindow: true } });
    });
    expect(document.querySelector('.rw-badge')!.textContent).toContain('was not being received then');
    unmount();
  });

  it('the back key asks the radio for a replay 10 s back', async () => {
    useTimeShiftStore.setState({ status: on, hydrated: true });
    const { unmount } = render(createElement(RewindBar));
    await act(async () => {
      button('⏪').click();
      await Promise.resolve();
    });
    const call = fetchMock.mock.calls.find((c) => c[0] === '/api/time-shift/replay');
    expect(JSON.parse((call![1] as RequestInit).body as string)).toEqual({ delaySeconds: 10 });
    unmount();
  });

  it('LIVE returns to the present', async () => {
    useTimeShiftStore.setState({ status: { ...on, replaying: true, delaySeconds: 20 }, hydrated: true });
    const { unmount } = render(createElement(RewindBar));
    await act(async () => {
      button('LIVE').click();
      await Promise.resolve();
    });
    expect(fetchMock.mock.calls.some((c) => c[0] === '/api/time-shift/live')).toBe(true);
    unmount();
  });
});
