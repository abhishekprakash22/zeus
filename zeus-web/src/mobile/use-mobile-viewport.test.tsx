// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { act, renderHook } from '../components/meters/__tests__/harness';
import { useDesktopViewportLock, useIsMobileViewport } from './use-mobile-viewport';

type MatchListener = (event: MediaQueryListEvent) => void;

let mediaMatches = false;
let mediaListeners: Set<MatchListener>;

function setUrl(search = ''): void {
  window.history.replaceState(null, '', `/${search}`);
}

function installMatchMedia(matches: boolean): void {
  mediaMatches = matches;
  mediaListeners = new Set();
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    value: vi.fn((media: string): MediaQueryList => ({
      media,
      matches: mediaMatches,
      onchange: null,
      addEventListener: (_type: string, listener: EventListenerOrEventListenerObject) => {
        mediaListeners.add(listener as MatchListener);
      },
      removeEventListener: (_type: string, listener: EventListenerOrEventListenerObject) => {
        mediaListeners.delete(listener as MatchListener);
      },
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });
}

function pushMediaMatch(matches: boolean): void {
  mediaMatches = matches;
  act(() => {
    for (const listener of mediaListeners) {
      listener({ matches } as MediaQueryListEvent);
    }
  });
}

describe('useIsMobileViewport', () => {
  beforeEach(() => {
    setUrl();
    installMatchMedia(false);
    document.documentElement.removeAttribute('data-force-desktop');
  });

  afterEach(() => {
    document.documentElement.removeAttribute('data-force-desktop');
    vi.restoreAllMocks();
  });

  it('tracks the mobile breakpoint when desktop host is not forced', () => {
    installMatchMedia(true);
    const hook = renderHook(() => useIsMobileViewport());

    expect(hook.result.current).toBe(true);

    pushMediaMatch(false);
    expect(hook.result.current).toBe(false);

    hook.unmount();
  });

  it('keeps a desktop-host loopback view in the desktop shell', () => {
    installMatchMedia(true);
    const hook = renderHook(() => useIsMobileViewport({ forceDesktop: true }));

    expect(hook.result.current).toBe(false);

    pushMediaMatch(true);
    expect(hook.result.current).toBe(false);

    hook.unmount();
  });

  it('lets ?mobile=1 explicitly preview mobile even on a desktop host', () => {
    setUrl('?mobile=1');
    installMatchMedia(false);
    const hook = renderHook(() => useIsMobileViewport({ forceDesktop: true }));

    expect(hook.result.current).toBe(true);

    hook.unmount();
  });

  it('sets the CSS force-desktop flag for desktop-host views', () => {
    let forceDesktop = true;
    const hook = renderHook(() => {
      useDesktopViewportLock(forceDesktop);
      return null;
    });

    expect(document.documentElement.getAttribute('data-force-desktop')).toBe('1');

    forceDesktop = false;
    hook.rerender();
    expect(document.documentElement.getAttribute('data-force-desktop')).toBeNull();

    hook.unmount();
  });

  it('does not set the CSS force-desktop flag for ?mobile=1 previews', () => {
    setUrl('?mobile=1');
    const hook = renderHook(() => {
      useDesktopViewportLock(true);
      return null;
    });

    expect(document.documentElement.getAttribute('data-force-desktop')).toBeNull();

    hook.unmount();
  });
});
