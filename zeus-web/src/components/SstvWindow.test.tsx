// SPDX-License-Identifier: GPL-2.0-or-later
// SSTV window: logging a received picture sends an SSTV QSO with the FSK-ID
// callsign, the picture's dial/band and its start time.

import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { CreateLogEntryRequest } from '../api/log';
import type { SstvImageMeta } from '../api/client';
import { useLoggerStore } from '../state/logger-store';
import { useSstvStore } from '../state/sstv-store';
import { SstvWindow } from './SstvWindow';

const META: SstvImageMeta = {
  id: 4, mode: 'Martin 1', width: 320, height: 256, rowsDone: 256, offsetHz: 12,
  clockErrorPpm: 150, dialHz: 14_230_000, sideBand: 'USB',
  startedUnixMs: Date.UTC(2026, 8, 25, 18, 30, 0), endedUnixMs: null, endReason: 'Complete',
  key: 'k', adjustable: false, slantPpm: 0, shiftPx: 0, callsign: 'EA4ABC',
};

describe('SstvWindow log row', () => {
  let container: HTMLDivElement;
  let root: Root;
  let sent: CreateLogEntryRequest[];

  beforeEach(() => {
    sent = [];
    useLoggerStore.setState({
      addLogEntry: vi.fn(async (req: CreateLogEntryRequest) => {
        sent.push(req);
        return { id: 'x' } as never;
      }),
    });
    useSstvStore.setState({
      panelOpen: true, enabled: true, current: null, images: [META], selectedId: 4,
      pixels: {}, modes: ['Martin 1'], ensurePixels: () => undefined,
    });
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    useSstvStore.setState({ panelOpen: false, images: [], selectedId: null });
  });

  it('logs an SSTV QSO pre-filled from the FSK ID', async () => {
    act(() => root.render(<SstvWindow />));
    const call = container.querySelector<HTMLInputElement>('.sstv-call');
    expect(call?.value).toBe('EA4ABC');

    const btn = Array.from(container.querySelectorAll('button')).find((b) => b.textContent === 'LOG');
    await act(async () => {
      btn?.click();
    });

    expect(sent).toEqual([
      expect.objectContaining({
        callsign: 'EA4ABC',
        mode: 'SSTV',
        band: '20m',
        frequencyMhz: 14.23,
        rstSent: '595',
        rstRcvd: '595',
        qsoDateTimeUtc: '2026-09-25T18:30:00.000Z',
      }),
    ]);
    expect(btn?.textContent).toBe('LOGGED');
  });
});
