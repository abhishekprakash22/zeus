// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, type RenderResult } from '../components/meters/__tests__/harness';
import {
  AF_TX_SOURCE,
  acquireAfLease,
  binDb,
  parseAfFrame,
  peakFrequencyHz,
  type AfFrame,
} from './af-analysis';
import { effectiveView, stepView, useFilterViewStore, viewsFor } from './filter-view-store';
import { appliedAgcGainDb, audioAxis, audioEdges, audioX, displayRangeDb, scopeGain, scopeTrigger, smoothBins, trackFloorDb } from './AfView';
import { FilterDisplay } from './FilterDisplay';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';

function frameBytes(source: number, bins: number[], scope: number[], muted = false): ArrayBuffer {
  const buf = new ArrayBuffer(8 + bins.length + scope.length + 2);
  const dv = new DataView(buf);
  dv.setUint8(0, 0x3e);
  dv.setUint8(1, source);
  dv.setUint8(2, muted ? 1 : 0);
  dv.setUint16(3, 8000, true);
  dv.setUint8(5, bins.length);
  dv.setUint16(6, scope.length, true);
  bins.forEach((b, i) => dv.setUint8(8 + i, b));
  scope.forEach((v, i) => dv.setInt8(8 + bins.length + i, v));
  dv.setInt8(8 + bins.length + scope.length, -9);
  dv.setInt8(8 + bins.length + scope.length + 1, -6);
  return buf;
}

describe('AF analysis frames', () => {
  it('decode header, bins, scope and levels', () => {
    const f = parseAfFrame(frameBytes(1, [0, 128, 255], [-127, 0, 127], true), 42)!;
    expect(f.source).toBe(1);
    expect(f.muted).toBe(true);
    expect(f.rateHz).toBe(8000);
    expect(Array.from(f.bins)).toEqual([0, 128, 255]);
    expect(Array.from(f.scope)).toEqual([-127, 0, 127]);
    expect(f.rmsDbfs).toBe(-9);
    expect(f.peakDbfs).toBe(-6);
    expect(f.at).toBe(42);
  });

  it('reject truncated or foreign frames', () => {
    expect(parseAfFrame(new ArrayBuffer(4))).toBeNull();
    const b = frameBytes(0, [1, 2], [0]);
    new DataView(b).setUint8(0, 0x19);
    expect(parseAfFrame(b)).toBeNull();
  });

  it('maps bytes back to dBFS', () => {
    expect(binDb(0)).toBe(-120);
    expect(binDb(255)).toBe(0);
  });

  it('finds a CW tone between bins and ignores noise', () => {
    // 128 bins over 0..4 kHz = 31.25 Hz/bin; a tone at bin ~20.5 (≈640 Hz).
    const bins = new Array(128).fill(40);
    bins[20] = 220;
    bins[21] = 220;
    const f = { rateHz: 8000, bins: Uint8Array.from(bins) } as unknown as AfFrame;
    const hz = peakFrequencyHz(f, 300, 900)!;
    expect(hz).toBeGreaterThan(630);
    expect(hz).toBeLessThan(660);
    const flat = { rateHz: 8000, bins: Uint8Array.from(new Array(128).fill(40)) } as unknown as AfFrame;
    expect(peakFrequencyHz(flat, 300, 900)).toBeNull();
  });
});

describe('AF view helpers', () => {
  it('audio edges come from the signed filter, clamped to 0..4 kHz', () => {
    expect(audioEdges(-2900, -100)).toEqual([100, 2900]);
    expect(audioEdges(150, 2850)).toEqual([150, 2850]);
    expect(audioEdges(0, 6000)).toEqual([0, 4000]);
  });

  it('the scope triggers on a rising zero crossing', () => {
    expect(scopeTrigger(Int8Array.from([5, 3, -2, -4, 1, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]))).toBe(4);
    expect(scopeTrigger(Int8Array.from([1, 1, 1, 1]))).toBe(0);
  });
});

describe('scope gain and spectrum smoothing', () => {
  it('quiet audio gets gain, loud audio none, silence is capped', () => {
    expect(20 * Math.log10(scopeGain(0.001))).toBeCloseTo(58.6, 0); // −60 dBFS peak → ~+59 dB
    expect(scopeGain(0.85)).toBeCloseTo(1, 5);
    expect(20 * Math.log10(scopeGain(0))).toBeCloseTo(60, 0); // capped at +60 dB
  });

  it('averages bins frame to frame and restarts on a size change', () => {
    let s = smoothBins(null, Uint8Array.from([0, 255]));
    expect(Array.from(s)).toEqual([-120, 0]);
    s = smoothBins(s, Uint8Array.from([255, 255]), 0.5);
    expect(s[0]).toBeCloseTo(-60, 5);
    expect(smoothBins(s, Uint8Array.from([255, 255, 255])).length).toBe(3);
  });

  it('refers bins to the receiver input by taking the AGC gain back out', () => {
    // Same band noise heard at two AGC gains: −40 dBFS at +40 dB, −70 at +10 dB.
    const quietBand = smoothBins(null, Uint8Array.from([Math.round(((-40 + 120) / 120) * 255)]), 0.35, 40);
    const underSignal = smoothBins(null, Uint8Array.from([Math.round(((-70 + 120) / 120) * 255)]), 0.35, 10);
    expect(quietBand[0]!).toBeCloseTo(-80, 0);
    expect(underSignal[0]!).toBeCloseTo(-80, 0); // the floor holds still
  });

  it('the floor falls fast and rises slowly', () => {
    expect(trackFloorDb(NaN, -80, 0.016)).toBe(-80);
    expect(trackFloorDb(-80, -100, 0.016)).toBeCloseTo(-86, 5); // 30 % of the way down at once
    const up = trackFloorDb(-80, -60, 0.016);
    expect(up).toBeGreaterThan(-80);
    expect(up).toBeLessThan(-79.9); // a voice cannot lift it in one frame
    let f = -80;
    for (let i = 0; i < 60 * 5; i++) f = trackFloorDb(f, -60, 1 / 60);
    expect(f).toBeGreaterThan(-74); // ~63 % after one 5 s time constant
    expect(f).toBeLessThan(-66);
  });

  it('a strong signal does not move the scale (fixed span above the floor)', () => {
    expect(displayRangeDb(-90, -85, -200, 0)).toEqual([-98, -28]);
    expect(displayRangeDb(-90, -10, -200, 0)).toEqual([-98, -28]); // same noise, same place
  });

  it('turns the WDSP AGC meter into the gain actually applied', () => {
    expect(appliedAgcGainDb(-80)).toBe(80); // quiet band: AGC boosting hard
    expect(appliedAgcGainDb(-20)).toBe(20); // under a signal: much less
    expect(appliedAgcGainDb(-400)).toBe(0); // meter not run yet
    expect(appliedAgcGainDb(NaN)).toBe(0);
  });

  it('with the right sign the floor holds still through the AGC swing', () => {
    // Band noise of −120 dBFS-equivalent at the input, heard at two AGC states.
    const heardQuiet = -120 + 80; // AGC +80 dB → −40 dBFS
    const heardUnderSignal = -120 + 20; // AGC +20 dB → −100 dBFS
    const toByte = (db: number) => Math.round(((db + 120) / 120) * 255);
    const a = smoothBins(null, Uint8Array.from([toByte(heardQuiet)]), 0.35, appliedAgcGainDb(-80));
    const b = smoothBins(null, Uint8Array.from([toByte(heardUnderSignal)]), 0.35, appliedAgcGainDb(-20));
    expect(a[0]!).toBeCloseTo(-120, 0);
    expect(b[0]!).toBeCloseTo(-120, 0);
  });

  it('reads the wide header and rebuilds the true scope scale', () => {
    const bins = 300;
    const buf = new ArrayBuffer(9 + bins + 2 + 2);
    const dv = new DataView(buf);
    dv.setUint8(0, 0x3e);
    dv.setUint8(2, 0x02 | 0x04);
    dv.setUint16(3, 8000, true);
    dv.setUint16(5, bins, true);
    dv.setUint16(7, 2, true);
    dv.setInt8(9 + bins + 2 + 1, -40);
    const f = parseAfFrame(buf)!;
    expect(f.bins.length).toBe(300);
    expect(f.scopeScale).toBeCloseTo(0.01, 5);
  });
});

describe('audio axis follows the passband', () => {
  it('lower-sideband modes are mirrored so traces line up with their stations', () => {
    expect(audioAxis('LSB', -2850, -150, false).mirror).toBe(true);
    expect(audioAxis('CWL', -850, -350, false).mirror).toBe(true);
    expect(audioAxis('DIGL', -3000, -100, false).mirror).toBe(true);
    expect(audioAxis('USB', 150, 2850, false).mirror).toBe(false);
    expect(audioAxis('LSB', -2850, -150, true).mirror).toBe(false); // TX audio is plain
  });

  it('the span fits the filter with a margin instead of a fixed 4 kHz', () => {
    expect(audioAxis('LSB', -2850, -150, false).spanHz).toBe(3100);
    expect(audioAxis('CWU', 350, 850, false).spanHz).toBe(1100);
    expect(audioAxis('USB', 0, 6000, false).spanHz).toBe(4000);
    expect(audioAxis('USB', 0, 6000, true).spanHz).toBe(4000);
  });

  it('places 0 Hz on the right when mirrored (next to the dial in LSB)', () => {
    expect(audioX(0, 300, 3000, true)).toBe(300);
    expect(audioX(3000, 300, 3000, true)).toBe(0);
    expect(audioX(1000, 300, 3000, false)).toBe(100);
  });
});

describe('view list per mode', () => {
  it('CW pitch exists only in CW, and the choice falls back without being lost', () => {
    expect(viewsFor('LSB')).not.toContain('cw');
    expect(viewsFor('CWU')).toContain('cw');
    expect(effectiveView('cw', 'LSB')).toBe('aspec');
    expect(effectiveView('cw', 'CWL')).toBe('cw');
    expect(stepView('lvl', 'USB', 1)).toBe('pass');
    expect(stepView('pass', 'USB', -1)).toBe('lvl');
    expect(stepView('awf', 'CWU', 1)).toBe('cw');
  });
});

describe('lease and filter display', () => {
  let mounted: RenderResult | null = null;
  const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    void init;
    if (url === '/api/ui/filter-view' && !init) return new Response('{}');
    return new Response('{"leaseSeconds":6}');
  });
  const posts = (url: string) =>
    fetchMock.mock.calls.filter(([u, i]) => u === url && (i as RequestInit | undefined)?.method === 'POST');

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    vi.stubGlobal('fetch', fetchMock);
    fetchMock.mockClear();
    localStorage.clear();
    useFilterViewStore.setState({ views: {}, hydrated: true });
    useConnectionStore.setState({ mode: 'LSB', filterLowHz: -2900, filterHighHz: -100, cwPitchHz: 600 } as never);
    useTxStore.setState({ moxOn: false } as never);
  });
  afterEach(() => {
    mounted?.unmount();
    mounted = null;
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('one renewal covers every source; the last release stops it', () => {
    const a = acquireAfLease(0);
    const b = acquireAfLease(0);
    const c = acquireAfLease(1);
    const last = JSON.parse(posts('/api/audio-analysis/subscribe').at(-1)![1]!.body as string);
    expect(last.sources.sort()).toEqual([0, 1]);
    a();
    b();
    c();
    fetchMock.mockClear();
    vi.advanceTimersByTime(5000);
    expect(posts('/api/audio-analysis/subscribe')).toHaveLength(0);
  });

  it('shows the passband by default, and the chip switches to an audio view', async () => {
    await act(async () => {
      mounted = render(
        createElement(FilterDisplay, { receiver: 'A', children: createElement('div', { 'data-testid': 'pan' }) }),
      );
    });
    const q = (s: string) => mounted!.container.querySelector(s);
    expect(q('[data-testid="pan"]')).not.toBeNull();
    expect(posts('/api/audio-analysis/subscribe')).toHaveLength(0); // passband needs no feed

    await act(async () => (q('.fv-chip') as HTMLButtonElement).click());
    // The strip is portalled to <body> so the card cannot clip it — all six present.
    const strip = document.querySelector('[data-testid="filter-view-strip"]')!;
    const items = Array.from(strip.querySelectorAll('.fv-item')) as HTMLButtonElement[];
    expect(items).toHaveLength(6);
    expect(items.find((b) => b.getAttribute('aria-label')?.startsWith('CW pitch'))!.disabled).toBe(true); // LSB
    await act(async () => items.find((b) => b.getAttribute('aria-label') === 'Audio spectrum')!.click());
    expect(document.querySelector('[data-testid="filter-view-strip"]')).toBeNull(); // closes on pick

    expect(q('[data-testid="af-view-aspec"]')).not.toBeNull();
    expect(useFilterViewStore.getState().views[0]).toBe('aspec');
    const body = JSON.parse(posts('/api/audio-analysis/subscribe').at(-1)![1]!.body as string);
    expect(body.sources).toEqual([0]);
    expect(posts('/api/ui/filter-view')).toHaveLength(1); // remembered on the radio
  });

  it('keyed, the audio view follows the TX mic and says so', async () => {
    useFilterViewStore.setState({ views: { 0: 'scope' } });
    useTxStore.setState({ moxOn: true } as never);
    await act(async () => {
      mounted = render(createElement(FilterDisplay, { receiver: 'A', children: null }));
    });
    expect(mounted!.container.querySelector('.fv-tx')?.textContent).toBe('TX');
    const body = JSON.parse(posts('/api/audio-analysis/subscribe').at(-1)![1]!.body as string);
    expect(body.sources).toEqual([AF_TX_SOURCE]);
  });

  it('the wheel over the page dots steps the view', async () => {
    await act(async () => {
      mounted = render(createElement(FilterDisplay, { receiver: 'A', children: null }));
    });
    const dots = mounted!.container.querySelector('.fv-dots')!;
    await act(async () => {
      dots.dispatchEvent(new WheelEvent('wheel', { deltaY: 100, bubbles: true, cancelable: true }));
    });
    expect(useFilterViewStore.getState().views[0]).toBe('aspec');
  });
});
