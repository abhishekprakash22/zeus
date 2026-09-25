// SPDX-License-Identifier: GPL-2.0-or-later
// SSTV store — SSE ingest paints rows into the live picture, `end` swaps in
// the re-rendered picture from /sstv/image/{id}, `discard` drops false starts.

import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { SstvImageMeta } from '../api/client';

vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  getSstvImage: vi.fn(async (id: number) => ({
    meta: meta(id, 2),
    rgb: btoa(String.fromCharCode(...new Array(2 * 2 * 3).fill(200))),
  })),
  getSstvStatus: vi.fn(),
  postSstvEnabled: vi.fn(async (on: boolean) => ({ enabled: on })),
  postSstvStop: vi.fn(),
  setMode: vi.fn(async () => ({})),
}));

import { sstvSidebandFor, useSstvStore } from './sstv-store';

function meta(id: number, rowsDone = 0): SstvImageMeta {
  return {
    id, mode: 'Martin 1', width: 2, height: 2, rowsDone, offsetHz: 0, clockErrorPpm: 0,
    dialHz: 14_230_000, sideBand: 'USB', startedUnixMs: 0, endedUnixMs: null, endReason: null,
  };
}

const b64 = (bytes: number[]) => btoa(String.fromCharCode(...bytes));

describe('sstv store', () => {
  beforeEach(() => {
    useSstvStore.setState({
      panelOpen: false, priorRadio: null, forcedMode: null, enabled: false, current: null, images: [],
      selectedId: null, pixels: {}, pixelsRev: 0,
    });
  });

  it('paints streamed rows into the live picture', () => {
    const s = useSstvStore.getState();
    s.ingest({ kind: 'start', image: meta(1) });
    s.ingest({ kind: 'rows', id: 1, row: 1, count: 1, width: 2, rgb: b64([10, 20, 30, 40, 50, 60]) });

    const st = useSstvStore.getState();
    const px = st.pixels[1]!;
    // Row 0 untouched (opaque black), row 1 painted RGBA.
    expect(Array.from(px.rgba)).toEqual([0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30, 255, 40, 50, 60, 255]);
    expect(st.current?.rowsDone).toBe(2);
  });

  it('end files the picture and loads the re-rendered pixels', async () => {
    const s = useSstvStore.getState();
    s.ingest({ kind: 'start', image: meta(3) });
    s.ingest({ kind: 'end', image: { ...meta(3, 2), endReason: 'Complete' } });

    expect(useSstvStore.getState().current).toBeNull();
    expect(useSstvStore.getState().images.map((i) => i.id)).toEqual([3]);
    await vi.waitFor(() => expect(useSstvStore.getState().pixels[3]?.rgba[0]).toBe(200));
  });

  it('discard drops a false start', () => {
    const s = useSstvStore.getState();
    s.ingest({ kind: 'start', image: meta(5) });
    s.ingest({ kind: 'discard', id: 5 });
    expect(useSstvStore.getState().current).toBeNull();
    expect(useSstvStore.getState().pixels[5]).toBeUndefined();
  });

  it('entering SSTV switches the decoder on; exiting switches it off', async () => {
    useSstvStore.getState().openWorkspace();
    await vi.waitFor(() => expect(useSstvStore.getState().enabled).toBe(true));
    useSstvStore.getState().closeWorkspace();
    await vi.waitFor(() => expect(useSstvStore.getState().enabled).toBe(false));
  });

  it('picks the analog SSTV sideband by band', () => {
    expect(sstvSidebandFor(7_171_000)).toBe('LSB');
    expect(sstvSidebandFor(3_845_000)).toBe('LSB');
    expect(sstvSidebandFor(14_230_000)).toBe('USB');
    expect(sstvSidebandFor(28_680_000)).toBe('USB');
  });
});
