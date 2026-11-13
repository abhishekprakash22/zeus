// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { act, render } from '../meters/__tests__/harness';

const setVfoBMock = vi.hoisted(() => vi.fn());
const setTxVfoMock = vi.hoisted(() => vi.fn());
const setRx2Mock = vi.hoisted(() => vi.fn());

vi.mock('../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/client')>();
  return { ...actual, setVfoB: setVfoBMock, setTxVfo: setTxVfoMock, setRx2: setRx2Mock };
});

import { SpectrumRadialMenu } from './SpectrumRadialMenu';
import { useRadialMenuStore, type RadialContext } from '../../state/radial-menu-store';
import { useConnectionStore } from '../../state/connection-store';
import { useDxClusterStore } from '../../state/dxcluster-store';
import { useMemoryStore } from '../../state/memory-store';
import { useNotchStore } from '../../state/notch-store';
import { useVfoLockStore } from '../../state/vfo-lock-store';
import type { DxClusterStatus } from '../../api/dxcluster';

const tune = vi.fn();

function ctx(over: Partial<RadialContext> = {}): RadialContext {
  return {
    clientX: 400,
    clientY: 300,
    rawHz: 14_074_420,
    tuneHz: 14_074_000,
    snapped: true,
    carrierHz: 14_074_600,
    hzPerPx: 50,
    rxIndex: 0,
    panRxIndex: 0,
    mode: 'USB',
    tune,
    ...over,
  };
}

function btn(label: string): HTMLButtonElement {
  const b = Array.from(document.querySelectorAll<HTMLButtonElement>('.srm button, .srm-spot button')).find((x) =>
    x.textContent?.includes(label),
  );
  if (!b) throw new Error(`no button ${label}`);
  return b;
}

let fetchMock: ReturnType<typeof vi.fn>;

function okJson(body: unknown) {
  return Promise.resolve({ ok: true, status: 200, statusText: 'OK', json: () => Promise.resolve(body) } as Response);
}

beforeEach(() => {
  vi.clearAllMocks();
  fetchMock = vi.fn((url: string) => okJson(url === '/api/dxcluster/spot' ? { sent: true } : []));
  vi.stubGlobal('fetch', fetchMock);
  useConnectionStore.setState({ maxReceivers: 2, filterLowHz: 100, filterHighHz: 2800 });
  useDxClusterStore.setState({ status: { state: 'Connected' } as DxClusterStatus });
  useMemoryStore.setState({ channels: [], hydrated: true });
  useNotchStore.setState({ notches: [] });
  useVfoLockStore.setState({ locked: false });
  setVfoBMock.mockImplementation(async () => ({ ...useConnectionStore.getState() }));
  setTxVfoMock.mockImplementation(async () => ({ ...useConnectionStore.getState() }));
  setRx2Mock.mockImplementation(async () => ({ ...useConnectionStore.getState() }));
});

afterEach(() => {
  useRadialMenuStore.setState({ open: null });
  vi.unstubAllGlobals();
});

async function openMenu(c: RadialContext) {
  const r = render(createElement(SpectrumRadialMenu));
  await act(async () => {
    useRadialMenuStore.setState({ open: c });
  });
  return r;
}

describe('SpectrumRadialMenu', () => {
  it('shows the snapped target in the centre and tunes through the surface', async () => {
    const { unmount } = await openMenu(ctx());
    const center = document.querySelector('.srm-center') as HTMLButtonElement;
    expect(center.textContent).toContain('SNAP');
    expect(center.textContent).toContain('14.074 000');
    await act(async () => center.click());
    expect(tune).toHaveBeenCalledTimes(1);
    expect(useRadialMenuStore.getState().open).toBeNull();
    unmount();
  });

  it('drops a notch on the carrier, not the raw press point', async () => {
    const { unmount } = await openMenu(ctx());
    await act(async () => btn('Notch').click());
    const n = useNotchStore.getState().notches;
    expect(n).toHaveLength(1);
    expect(n[0]!.centerHz).toBe(14_074_600);
    unmount();
  });

  it('Split puts VFO B on the target and transmits on B', async () => {
    const { unmount } = await openMenu(ctx());
    await act(async () => {
      btn('Split').click();
      await Promise.resolve();
      await Promise.resolve();
    });
    expect(setVfoBMock).toHaveBeenCalledWith(14_074_000);
    expect(setTxVfoMock).toHaveBeenCalledWith('B');
    unmount();
  });

  it('RX2 enables the second receiver on the target', async () => {
    const { unmount } = await openMenu(ctx());
    await act(async () => btn('RX2').click());
    expect(setRx2Mock).toHaveBeenCalledWith({ enabled: true, vfoBHz: 14_074_000 });
    unmount();
  });

  it('stores a memory with the receiver mode and filter', async () => {
    const { unmount } = await openMenu(ctx());
    await act(async () => {
      btn('Store').click();
      await Promise.resolve();
    });
    const call = fetchMock.mock.calls.find((c) => c[0] === '/api/memories' && (c[1] as RequestInit | undefined)?.method === 'POST');
    expect(call).toBeDefined();
    const body = JSON.parse((call![1] as RequestInit).body as string);
    expect(body).toMatchObject({ hz: 14_074_000, mode: 'USB', filterLowHz: 100, filterHighHz: 2800 });
    unmount();
  });

  it('closes on Escape', async () => {
    const { unmount } = await openMenu(ctx());
    await act(async () => {
      window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    });
    expect(useRadialMenuStore.getState().open).toBeNull();
    unmount();
  });

  it('spot form posts a CW spot on the carrier', async () => {
    const { unmount } = await openMenu(ctx({ mode: 'CWU' }));
    await act(async () => btn('Spot').click());
    const call = document.querySelector('input[aria-label="Callsign"]') as HTMLInputElement;
    expect(call).not.toBeNull();
    expect(document.querySelector('.srm-spot-hz')?.textContent).toBe('14074.6 kHz');
    const send = btn('Send');
    expect(send.disabled).toBe(true);

    await act(async () => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!;
      setter.call(call, 'w1aw');
      call.dispatchEvent(new Event('input', { bubbles: true }));
    });
    expect(btn('Send').disabled).toBe(false);
    await act(async () => {
      btn('Send').click();
      await Promise.resolve();
      await Promise.resolve();
    });
    const post = fetchMock.mock.calls.find((c) => c[0] === '/api/dxcluster/spot');
    expect(post).toBeDefined();
    expect(JSON.parse((post![1] as RequestInit).body as string)).toEqual({ freqHz: 14_074_600, call: 'W1AW', comment: '' });
    unmount();
  });

  it('greys out Spot without a cluster and dial actions under VFO lock', async () => {
    useDxClusterStore.setState({ status: { state: 'Disconnected' } as DxClusterStatus });
    useVfoLockStore.setState({ locked: true });
    const { unmount } = await openMenu(ctx());
    expect(btn('Spot').disabled).toBe(true);
    expect(btn('Split').disabled).toBe(true);
    expect((document.querySelector('.srm-center') as HTMLButtonElement).disabled).toBe(true);
    expect(btn('Notch').disabled).toBe(false);
    unmount();
  });
});
