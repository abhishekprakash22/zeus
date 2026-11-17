// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  _resetHoverListenForTest,
  DWELL_MS,
  hoverListenAim,
  hoverListenStop,
  KEEPALIVE_MS,
  SEND_MS,
  useHoverListenStore,
} from './hover-listen';

let fetchMock: ReturnType<typeof vi.fn>;
let answer: { active: boolean; reason: string | null };

const previewCalls = () => fetchMock.mock.calls.filter((c) => c[0] === '/api/hover-preview');
const stopCalls = () => fetchMock.mock.calls.filter((c) => c[0] === '/api/hover-preview/stop');
const sentHz = () => previewCalls().map((c) => (JSON.parse((c[1] as RequestInit).body as string) as { hz: number }).hz);

beforeEach(() => {
  vi.useFakeTimers();
  _resetHoverListenForTest();
  answer = { active: true, reason: null };
  fetchMock = vi.fn(async () => ({ ok: true, json: async () => answer }) as Response);
  vi.stubGlobal('fetch', fetchMock);
  useHoverListenStore.setState({ enabled: true, hydrated: true });
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('hover to listen controller', () => {
  it('does nothing while switched off', async () => {
    useHoverListenStore.setState({ enabled: false });
    hoverListenAim(14_200_000);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('waits out the dwell, then previews where the pointer ended up', async () => {
    hoverListenAim(14_200_000);
    await vi.advanceTimersByTimeAsync(DWELL_MS - 50);
    hoverListenAim(14_201_000);
    expect(previewCalls()).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(60);
    expect(sentHz()).toEqual([14_201_000]);
    expect(useHoverListenStore.getState().activeHz).toBe(14_201_000);
  });

  it('a pointer that leaves before the dwell never touches the radio', async () => {
    hoverListenAim(14_200_000);
    hoverListenStop();
    await vi.advanceTimersByTimeAsync(1_000);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('rate-limits a moving pointer to one request per send interval', async () => {
    hoverListenAim(14_200_000);
    await vi.advanceTimersByTimeAsync(DWELL_MS);
    for (let i = 1; i <= 10; i++) {
      hoverListenAim(14_200_000 + i * 100);
      await vi.advanceTimersByTimeAsync(10);
    }
    // 100 ms of motion: the engage send, nothing more yet.
    expect(previewCalls()).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(SEND_MS);
    expect(sentHz()).toEqual([14_200_000, 14_201_000]); // latest aim, not every step
  });

  it('renews the lease while the pointer rests', async () => {
    hoverListenAim(14_200_000);
    await vi.advanceTimersByTimeAsync(DWELL_MS);
    await vi.advanceTimersByTimeAsync(KEEPALIVE_MS * 3 + 20);
    expect(previewCalls().length).toBeGreaterThanOrEqual(4);
    expect(new Set(sentHz())).toEqual(new Set([14_200_000]));
  });

  it('stop ends the preview on the radio and clears the badge', async () => {
    hoverListenAim(14_200_000);
    await vi.advanceTimersByTimeAsync(DWELL_MS);
    hoverListenStop();
    expect(stopCalls()).toHaveLength(1);
    expect(useHoverListenStore.getState().activeHz).toBeNull();
    const before = previewCalls().length;
    await vi.advanceTimersByTimeAsync(KEEPALIVE_MS * 3);
    expect(previewCalls()).toHaveLength(before); // no keep-alive after stop
  });

  it('the loupe skips the dwell', async () => {
    hoverListenAim(7_100_000, true);
    await vi.advanceTimersByTimeAsync(0);
    expect(sentHz()).toEqual([7_100_000]);
  });

  it('shows why a spot cannot be previewed', async () => {
    answer = { active: false, reason: "outside the receiver's window" };
    hoverListenAim(14_900_000, true);
    await vi.advanceTimersByTimeAsync(0);
    expect(useHoverListenStore.getState().activeHz).toBeNull();
    expect(useHoverListenStore.getState().reason).toBe("outside the receiver's window");
  });

  it('an answer that arrives after stop does not resurrect the badge', async () => {
    let release: (() => void) | null = null;
    fetchMock.mockImplementationOnce(
      () =>
        new Promise<Response>((resolve) => {
          release = () => resolve({ ok: true, json: async () => answer } as Response);
        }),
    );
    hoverListenAim(14_200_000, true);
    hoverListenStop();
    release!();
    await vi.advanceTimersByTimeAsync(0);
    expect(useHoverListenStore.getState().activeHz).toBeNull();
  });

  it('switching it off stops a live preview', async () => {
    hoverListenAim(14_200_000, true);
    await vi.advanceTimersByTimeAsync(0);
    useHoverListenStore.getState().setEnabled(false);
    expect(stopCalls()).toHaveLength(1);
    expect(useHoverListenStore.getState().activeHz).toBeNull();
  });
});
