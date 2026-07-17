// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';

import { act, render } from './meters/__tests__/harness';

const setZoomMock = vi.hoisted(() => vi.fn());
const setRadioLoMock = vi.hoisted(() => vi.fn());

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return {
    ...actual,
    setZoom: setZoomMock,
    setRadioLo: setRadioLoMock,
  };
});

import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';
import * as viewCenter from '../state/view-center';
import { ZoomControl } from './ZoomControl';

let rafNowMs = 0;
let nextRafHandle = 1;
let rafCallbacks = new Map<number, FrameRequestCallback>();

function setRange(input: HTMLInputElement, value: number): void {
  const setter = Object.getOwnPropertyDescriptor(
    HTMLInputElement.prototype,
    'value',
  )!.set!;
  setter.call(input, String(value));
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

async function flush(): Promise<void> {
  drainRafs();
  await Promise.resolve();
  drainRafs();
  await Promise.resolve();
}

function drainRafs(maxFrames = 100): void {
  let frames = 0;
  while (rafCallbacks.size > 0 && frames < maxFrames) {
    const callbacks = Array.from(rafCallbacks.values());
    rafCallbacks.clear();
    rafNowMs += 16.7;
    for (const cb of callbacks) cb(rafNowMs);
    frames++;
  }
}

describe('ZoomControl', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    viewCenter._resetForTest();
    rafNowMs = 0;
    nextRafHandle = 1;
    rafCallbacks = new Map<number, FrameRequestCallback>();
    vi.stubGlobal(
      'requestAnimationFrame',
      vi.fn((cb: FrameRequestCallback) => {
        const handle = nextRafHandle++;
        rafCallbacks.set(handle, cb);
        return handle;
      }),
    );
    vi.stubGlobal(
      'cancelAnimationFrame',
      vi.fn((handle: number) => {
        rafCallbacks.delete(handle);
      }),
    );

    useConnectionStore.setState({
      status: 'Connected',
      mode: 'USB',
      ctunEnabled: true,
      vfoHz: 14_205_000,
      radioLoHz: 14_200_000,
      cwPitchHz: 600,
      zoomLevel: 4,
    });
    useDisplayStore.setState({
      width: 200,
      centerHz: 14_200_000n,
      hzPerPixel: 100,
      panDb: new Float32Array(200),
      panValid: true,
    });
    setRadioLoMock.mockImplementation(async (hz: number) => ({
      ...useConnectionStore.getState(),
      radioLoHz: hz,
    }));
    setZoomMock.mockImplementation(async (level: number) => ({
      ...useConnectionStore.getState(),
      radioLoHz: 14_200_000,
      zoomLevel: level,
    }));
  });

  afterEach(() => {
    viewCenter._resetForTest();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('recenters CTUN before posting a slider zoom-in and keeps it after the zoom echo', async () => {
    const { container, unmount } = render(createElement(ZoomControl));
    const input = container.querySelector('input[type="range"]') as HTMLInputElement;

    await act(async () => {
      setRange(input, 5);
      await flush();
    });

    expect(setRadioLoMock).toHaveBeenCalledWith(14_205_000, undefined);
    expect(setZoomMock).toHaveBeenCalledWith(5, expect.any(AbortSignal));
    expect(useConnectionStore.getState().radioLoHz).toBe(14_205_000);
    expect(viewCenter.viewCenterFor('A').getTargetCenterHz()).toBe(14_205_000);

    unmount();
  });
});
