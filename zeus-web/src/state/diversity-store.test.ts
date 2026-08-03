// SPDX-License-Identifier: GPL-2.0-or-later
// Diversity store — payload shape, drag-stream throttling, memory round-trip.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  buildDiversityPayload,
  useDiversityStore,
  _resetDiversityThrottleForTests,
} from './diversity-store';

function mockFetch() {
  const calls: unknown[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((_url: string, init?: RequestInit) => {
      calls.push(JSON.parse(String(init?.body ?? '{}')));
      return Promise.resolve(new Response('{}'));
    }),
  );
  return calls;
}

describe('diversity store', () => {
  beforeEach(() => {
    localStorage.clear();
    _resetDiversityThrottleForTests();
    useDiversityStore.setState({
      enabled: false,
      gain: 1,
      phaseDeg: 0,
      sourceRx: 1,
      mems: [null, null, null, null],
    });
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('payload clamps gain and wraps phase', () => {
    expect(
      buildDiversityPayload({ enabled: true, gain: 3, phaseDeg: -30, sourceRx: 1 }),
    ).toEqual({ enabled: true, gain: 2, phaseDeg: 330, sourceRx: 1 });
  });

  it('drag stream coalesces through the throttle (leading + trailing only)', () => {
    vi.useFakeTimers();
    const calls = mockFetch();
    const s = useDiversityStore.getState();
    for (let i = 0; i <= 90; i += 5) s.setWeight(1, i); // 19 rapid updates
    expect(calls.length).toBe(1); // leading send immediately
    vi.advanceTimersByTime(500); // trailing sends drain
    expect(calls.length).toBeLessThanOrEqual(4);
    const last = calls[calls.length - 1] as { phaseDeg: number };
    expect(last.phaseDeg).toBe(90); // latest weight wins
  });

  it('enable posts immediately with the full weight', () => {
    const calls = mockFetch();
    useDiversityStore.getState().setEnabled(true);
    expect(calls).toEqual([{ enabled: true, gain: 1, phaseDeg: 0, sourceRx: 1 }]);
  });

  it('memories persist, recall restores the weight and enables', () => {
    const calls = mockFetch();
    const s = useDiversityStore.getState();
    useDiversityStore.setState({ gain: 0.82, phaseDeg: 227 });
    s.saveMem(0, -31.7, '0.740');
    expect(JSON.parse(localStorage.getItem('zeus.diversity.mems')!)[0].phaseDeg).toBe(227);
    useDiversityStore.setState({ gain: 1, phaseDeg: 0, enabled: false });
    useDiversityStore.getState().recallMem(0);
    const st = useDiversityStore.getState();
    expect(st.enabled).toBe(true);
    expect(st.phaseDeg).toBe(227);
    expect(st.gain).toBe(0.82);
    expect(calls[calls.length - 1]).toMatchObject({ enabled: true, phaseDeg: 227 });
    st.clearMem(0);
    expect(JSON.parse(localStorage.getItem('zeus.diversity.mems')!)[0]).toBeNull();
  });
});
