// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  _resetTimeShiftForTest,
  EMPTY_STATUS,
  formatDelay,
  IDLE_POLL_MS,
  parseStatus,
  REPLAY_POLL_MS,
  useTimeShiftStore,
  type TimeShiftStatus,
} from './time-shift';

let radio: TimeShiftStatus;
let fetchMock: ReturnType<typeof vi.fn>;
const calls = (path: string) => fetchMock.mock.calls.filter((c) => c[0] === path);
const bodyOf = (c: unknown[]) => JSON.parse((c[1] as RequestInit).body as string) as Record<string, unknown>;

beforeEach(() => {
  vi.useFakeTimers();
  _resetTimeShiftForTest();
  radio = { ...EMPTY_STATUS, enabled: true, capacitySeconds: 60, bufferedSeconds: 42, rateHz: 192_000 };
  fetchMock = vi.fn(async (path: string, init?: RequestInit) => {
    const body = init?.body ? (JSON.parse(init.body as string) as Record<string, number | boolean>) : {};
    if (path === '/api/time-shift/config') radio = { ...radio, enabled: body.enabled === true, requestedSeconds: Number(body.seconds) };
    if (path === '/api/time-shift/replay') radio = { ...radio, replaying: true, paused: false, delaySeconds: Number(body.delaySeconds) };
    if (path === '/api/time-shift/pause') radio = { ...radio, paused: body.paused === true };
    if (path === '/api/time-shift/live') radio = { ...radio, replaying: false, paused: false, delaySeconds: 0 };
    return { ok: true, json: async () => radio } as Response;
  });
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  _resetTimeShiftForTest();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('time-shift store', () => {
  it('hydrates from the radio and refreshes while the buffer is on', async () => {
    await useTimeShiftStore.getState().hydrate();
    expect(useTimeShiftStore.getState().status.bufferedSeconds).toBe(42);
    radio = { ...radio, bufferedSeconds: 44 };
    await vi.advanceTimersByTimeAsync(IDLE_POLL_MS + 10);
    expect(useTimeShiftStore.getState().status.bufferedSeconds).toBe(44);
    expect(calls('/api/time-shift/renew')).toHaveLength(0); // nothing to keep alive
  });

  it('does not poll while rewind is off', async () => {
    radio = { ...EMPTY_STATUS };
    await useTimeShiftStore.getState().hydrate();
    const n = fetchMock.mock.calls.length;
    await vi.advanceTimersByTimeAsync(IDLE_POLL_MS * 3);
    expect(fetchMock.mock.calls.length).toBe(n);
  });

  it('starts a replay and renews its lease about once a second', async () => {
    await useTimeShiftStore.getState().hydrate();
    await useTimeShiftStore.getState().replay(15);
    expect(bodyOf(calls('/api/time-shift/replay')[0]!)).toEqual({ delaySeconds: 15 });
    expect(useTimeShiftStore.getState().status.replaying).toBe(true);
    await vi.advanceTimersByTimeAsync(REPLAY_POLL_MS * 3 + 50);
    expect(calls('/api/time-shift/renew').length).toBeGreaterThanOrEqual(3);
  });

  it('nudges within what is held, and nudging to zero goes live', async () => {
    await useTimeShiftStore.getState().hydrate();
    await useTimeShiftStore.getState().nudge(10); // from live: 10 s back
    expect(useTimeShiftStore.getState().status.delaySeconds).toBe(10);
    await useTimeShiftStore.getState().nudge(100); // capped at the 42 s held
    expect(useTimeShiftStore.getState().status.delaySeconds).toBe(42);
    await useTimeShiftStore.getState().replay(8);
    await useTimeShiftStore.getState().nudge(-10); // past live
    expect(calls('/api/time-shift/live')).toHaveLength(1);
    expect(useTimeShiftStore.getState().status.replaying).toBe(false);
  });

  it('pause and live go to the radio', async () => {
    await useTimeShiftStore.getState().hydrate();
    await useTimeShiftStore.getState().replay(5);
    await useTimeShiftStore.getState().pause(true);
    expect(useTimeShiftStore.getState().status.paused).toBe(true);
    await useTimeShiftStore.getState().live();
    expect(useTimeShiftStore.getState().status.replaying).toBe(false);
  });

  it('configure sends the switch and the length', async () => {
    await useTimeShiftStore.getState().hydrate();
    await useTimeShiftStore.getState().configure(true, 120);
    expect(bodyOf(calls('/api/time-shift/config')[0]!)).toEqual({ enabled: true, seconds: 120 });
    await useTimeShiftStore.getState().configure(false);
    expect(bodyOf(calls('/api/time-shift/config')[1]!)).toEqual({ enabled: false, seconds: 120 });
    expect(useTimeShiftStore.getState().status.enabled).toBe(false);
  });
});

describe('helpers', () => {
  it('parses defensively', () => {
    expect(parseStatus(null)).toEqual(EMPTY_STATUS);
    expect(parseStatus({ enabled: true, delaySeconds: 'x', message: 5 })).toMatchObject({
      enabled: true,
      delaySeconds: 0,
      message: null,
    });
  });
  it('formats the delay readout', () => {
    expect(formatDelay(0)).toBe('LIVE');
    expect(formatDelay(23.44)).toBe('−23.4 s');
    expect(formatDelay(75)).toBe('−1:15');
  });
});
