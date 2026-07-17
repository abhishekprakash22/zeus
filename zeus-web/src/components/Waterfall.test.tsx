// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { StrictMode, createElement } from 'react';

import { act, render } from './meters/__tests__/harness';
import { Waterfall } from './Waterfall';
import { _resetDrawBusForTest } from '../realtime/draw-bus';
import { useSignalEnhanceStore } from '../dsp/signal-estimator';
import { useTxStore } from '../state/tx-store';
import { createEmptyDisplaySlice, useDisplayStore } from '../state/display-store';

const releaseFrameConsumerMock = vi.hoisted(() => vi.fn());
const createWfRendererMock = vi.hoisted(() => vi.fn());

vi.mock('../gl/waterfall', () => ({
  createWfRenderer: createWfRendererMock,
}));

vi.mock('../state/display-store', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../state/display-store')>();
  return {
    ...actual,
    registerFrameConsumer: vi.fn(() => releaseFrameConsumerMock),
  };
});

vi.mock('./FilterCursorOverlay', () => ({
  FilterCursorOverlay: () => null,
}));

vi.mock('./NotchOverlay', () => ({
  NotchOverlay: () => null,
}));

vi.mock('./PassbandOverlay', () => ({
  PassbandOverlay: () => null,
}));

vi.mock('./WfDbScale', () => ({
  WfDbScale: () => null,
}));

vi.mock('../util/use-pan-tune-gesture', () => ({
  usePanTuneGesture: () => undefined,
}));

describe('Waterfall', () => {
  beforeEach(() => {
    createWfRendererMock.mockImplementation(() => {
      throw new Error('shader compile failed (vertex): no compiler log');
    });
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    Object.defineProperty(HTMLCanvasElement.prototype, 'getContext', {
      configurable: true,
      value: vi.fn(() => ({})),
    });
    useSignalEnhanceStore.setState({ popEnabled: false, popRenderIntensity: 72 });
    useTxStore.setState({ moxOn: false, tunOn: false });
    releaseFrameConsumerMock.mockClear();
  });

  afterEach(() => {
    _resetDrawBusForTest();
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    useDisplayStore.setState({
      connected: false,
      width: 0,
      centerHz: 0n,
      hzPerPixel: 0,
      panDb: null,
      wfDb: null,
      panValid: false,
      wfValid: false,
      panFloorDb: null,
      wfFloorDb: null,
      lastSeq: 0,
      rx2: createEmptyDisplaySlice(),
    });
  });

  it('degrades instead of crashing when the WebGL renderer fails to build', () => {
    const { container, unmount } = render(createElement(Waterfall));

    expect(container.textContent).toContain('Waterfall renderer unavailable');

    unmount();

    expect(releaseFrameConsumerMock).toHaveBeenCalledTimes(1);
  });

  it('does not lose the WebGL context during StrictMode effect remount', () => {
    vi.useFakeTimers();
    const loseContext = vi.fn();
    const renderer = {
      caps: { floatLinear: true, colorBufferFloat: true, gpu: 'test-gpu' },
      resize: vi.fn(),
      pushFrame: vi.fn(),
      draw: vi.fn(),
      setColormap: vi.fn(),
      setPopMode: vi.fn(),
      setScrollSpeed: vi.fn(),
      setTransparent: vi.fn(),
      debugState: vi.fn(() => ({
        texWidth: 0,
        writeRow: 0,
        validRows: 0,
        scrollSpeed: 1,
        lastViewOffsetUv: 0,
        contextLost: false,
      })),
      clearHistory: vi.fn(),
      dispose: vi.fn(),
    };
    createWfRendererMock.mockReturnValue(renderer);

    Object.defineProperty(HTMLCanvasElement.prototype, 'getContext', {
      configurable: true,
      value: vi.fn(() => ({
        getExtension: vi.fn((name: string) =>
          name === 'WEBGL_lose_context' ? { loseContext } : null,
        ),
      })),
    });
    vi.stubGlobal(
      'ResizeObserver',
      class ResizeObserver {
        observe = vi.fn();
        disconnect = vi.fn();
      },
    );
    vi.stubGlobal(
      'IntersectionObserver',
      class IntersectionObserver {
        observe = vi.fn();
        disconnect = vi.fn();
      },
    );
    vi.stubGlobal('requestAnimationFrame', vi.fn(() => 1));
    vi.stubGlobal('cancelAnimationFrame', vi.fn());

    const { unmount } = render(
      createElement(StrictMode, null, createElement(Waterfall)),
    );

    act(() => {
      vi.advanceTimersByTime(250);
    });

    expect(loseContext).not.toHaveBeenCalled();

    act(() => {
      unmount();
    });

    act(() => {
      vi.advanceTimersByTime(251);
    });

    expect(loseContext).toHaveBeenCalledTimes(1);
  });

  it('clears normalized Pop waterfall history when TX changes the value domain', () => {
    const renderer = {
      caps: { floatLinear: true, colorBufferFloat: true, gpu: 'test-gpu' },
      resize: vi.fn(),
      pushFrame: vi.fn(),
      draw: vi.fn(),
      setColormap: vi.fn(),
      setPopMode: vi.fn(),
      setScrollSpeed: vi.fn(),
      setTransparent: vi.fn(),
      debugState: vi.fn(() => ({
        texWidth: 1024,
        writeRow: 12,
        validRows: 12,
        scrollSpeed: 1,
        lastViewOffsetUv: 0,
        contextLost: false,
      })),
      clearHistory: vi.fn(),
      dispose: vi.fn(),
    };
    createWfRendererMock.mockReturnValue(renderer);

    Object.defineProperty(HTMLCanvasElement.prototype, 'getContext', {
      configurable: true,
      value: vi.fn(() => ({
        getExtension: vi.fn(() => null),
      })),
    });
    vi.stubGlobal(
      'ResizeObserver',
      class ResizeObserver {
        observe = vi.fn();
        disconnect = vi.fn();
      },
    );
    vi.stubGlobal(
      'IntersectionObserver',
      class IntersectionObserver {
        observe = vi.fn();
        disconnect = vi.fn();
      },
    );
    vi.stubGlobal('requestAnimationFrame', vi.fn(() => 1));
    vi.stubGlobal('cancelAnimationFrame', vi.fn());

    useSignalEnhanceStore.setState({ popEnabled: true });
    const { unmount } = render(createElement(Waterfall));

    expect(renderer.clearHistory).not.toHaveBeenCalled();

    act(() => {
      useTxStore.setState({ moxOn: true, tunOn: false });
    });

    expect(renderer.clearHistory).toHaveBeenCalledTimes(1);
    expect(renderer.setPopMode).toHaveBeenLastCalledWith(false, 0, 0, 0);

    unmount();
  });

  it('keeps normal RX terrain relief depth during redraw', () => {
    let raf: FrameRequestCallback | null = null;
    const renderer = {
      caps: { floatLinear: true, colorBufferFloat: true, gpu: 'test-gpu' },
      resize: vi.fn(),
      pushFrame: vi.fn(),
      draw: vi.fn(),
      setColormap: vi.fn(),
      setPopMode: vi.fn(),
      setScrollSpeed: vi.fn(),
      setTransparent: vi.fn(),
      debugState: vi.fn(() => ({
        texWidth: 1024,
        writeRow: 12,
        validRows: 12,
        scrollSpeed: 1,
        lastViewOffsetUv: 0,
        contextLost: false,
      })),
      clearHistory: vi.fn(),
      dispose: vi.fn(),
    };
    createWfRendererMock.mockReturnValue(renderer);

    Object.defineProperty(HTMLCanvasElement.prototype, 'getContext', {
      configurable: true,
      value: vi.fn(() => ({
        getExtension: vi.fn(() => null),
      })),
    });
    vi.stubGlobal(
      'ResizeObserver',
      class ResizeObserver {
        observe = vi.fn();
        disconnect = vi.fn();
      },
    );
    vi.stubGlobal(
      'IntersectionObserver',
      class IntersectionObserver {
        observe = vi.fn();
        disconnect = vi.fn();
      },
    );
    vi.stubGlobal('requestAnimationFrame', vi.fn((cb: FrameRequestCallback) => {
      raf = cb;
      return 1;
    }));
    vi.stubGlobal('cancelAnimationFrame', vi.fn());
    useSignalEnhanceStore.setState({
      popEnabled: false,
      waterfallReliefDepth: 91,
      waterfallSmoothness: 63,
    });

    const { unmount } = render(createElement(Waterfall));
    renderer.setPopMode.mockClear();

    const panDb = new Float32Array(16).fill(-110);
    const wfDb = new Float32Array(16).fill(-110);
    wfDb[8] = -70;
    act(() => {
      useDisplayStore.getState().pushFrame({
        msgType: 1,
        headerFlags: 0,
        seq: 1,
        tsUnixMs: 0,
        rxId: 0,
        bodyFlags: 3,
        panValid: true,
        wfValid: true,
        width: 16,
        centerHz: 14_200_000n,
        hzPerPixel: 100,
        panDb,
        wfDb,
      });
    });
    act(() => {
      raf?.(0);
    });

    expect(renderer.setPopMode).toHaveBeenLastCalledWith(false, 0, 0.91, 0.63);

    unmount();
  });
});
