// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Regression coverage for #1269: when Settings replaces the workspace, an
// OPEN DigitalWindow must stay MOUNTED (hidden via display:none) so an
// active FT8 QSO's once-per-QSO guards (QRZ lookup, HamClock DX push) and
// the operator's tab/draft survive a Settings open/close. Only a CLOSED
// window unmounts.

/** @vitest-environment jsdom */

// harness first — installs the localStorage polyfill before any store import.
import { render, act } from '../meters/__tests__/harness';
import { describe, expect, it, beforeEach, vi } from 'vitest';
import { createElement } from 'react';

// Count body mounts so we can prove the subtree is NOT remounted across a
// Settings open/close (a remount is exactly what re-fires the QRZ/HamClock
// egress in #1269).
const mocks = vi.hoisted(() => ({ ft8Mounts: 0, wsprMounts: 0 }));

vi.mock('../../layout/ft8/Ft8PopBody', async () => {
  const React = await import('react');
  return {
    Ft8PopBody: () => {
      React.useEffect(() => {
        mocks.ft8Mounts += 1;
      }, []);
      return React.createElement('div', { 'data-testid': 'ft8-body' });
    },
  };
});

vi.mock('../../layout/ft8/WsprPopBody', async () => {
  const React = await import('react');
  return {
    WsprPopBody: () => {
      React.useEffect(() => {
        mocks.wsprMounts += 1;
      }, []);
      return React.createElement('div', { 'data-testid': 'wspr-body' });
    },
  };
});

import { DigitalWindow } from '../DigitalWindow';
import { useFt8Store } from '../../state/ft8-store';
import { useWsprStore } from '../../state/wspr-store';
import { useLayoutStore } from '../../state/layout-store';

function windowEl(container: HTMLElement): HTMLElement | null {
  return container.querySelector('.digital-window');
}

beforeEach(() => {
  mocks.ft8Mounts = 0;
  mocks.wsprMounts = 0;
  act(() => {
    useFt8Store.setState({ open: false });
    useWsprStore.setState({ open: false });
    useLayoutStore.setState({ settingsViewOpen: false });
  });
});

describe('DigitalWindow — mounted-but-hidden while Settings is open (#1269)', () => {
  it('unmounts entirely when closed (nothing live to preserve)', () => {
    const { container, unmount } = render(createElement(DigitalWindow));
    expect(windowEl(container)).toBeNull();
    expect(mocks.ft8Mounts).toBe(0);
    unmount();
  });

  it('is visible (display flex) when open and Settings is closed', () => {
    act(() => {
      useFt8Store.setState({ open: true });
    });
    const { container, unmount } = render(createElement(DigitalWindow));
    const el = windowEl(container);
    expect(el).not.toBeNull();
    expect(el!.style.display).toBe('flex');
    expect(container.querySelector('[data-testid="ft8-body"]')).not.toBeNull();
    expect(mocks.ft8Mounts).toBe(1);
    unmount();
  });

  it('stays mounted but hidden (display:none) when Settings opens over an active session', () => {
    act(() => {
      useFt8Store.setState({ open: true });
    });
    const { container, unmount } = render(createElement(DigitalWindow));

    const before = windowEl(container);
    expect(before).not.toBeNull();
    expect(mocks.ft8Mounts).toBe(1);

    // Settings opens — the window must NOT unmount.
    act(() => {
      useLayoutStore.setState({ settingsViewOpen: true });
    });

    const during = windowEl(container);
    expect(during).not.toBeNull();
    // Same DOM node — React reused it, only the style toggled (not a remount).
    expect(during).toBe(before);
    expect(during!.style.display).toBe('none');
    // Inner content is still mounted and was NOT remounted (guard refs intact).
    expect(container.querySelector('[data-testid="ft8-body"]')).not.toBeNull();
    expect(mocks.ft8Mounts).toBe(1);

    // Settings closes — window re-shows in place with no remount.
    act(() => {
      useLayoutStore.setState({ settingsViewOpen: false });
    });
    const after = windowEl(container);
    expect(after).toBe(before);
    expect(after!.style.display).toBe('flex');
    expect(mocks.ft8Mounts).toBe(1);

    unmount();
  });

  it('applies the same mounted-but-hidden rule to the WSPR body', () => {
    act(() => {
      useWsprStore.setState({ open: true });
    });
    const { container, unmount } = render(createElement(DigitalWindow));
    expect(container.querySelector('[data-testid="wspr-body"]')).not.toBeNull();
    expect(mocks.wsprMounts).toBe(1);

    act(() => {
      useLayoutStore.setState({ settingsViewOpen: true });
    });
    const el = windowEl(container);
    expect(el).not.toBeNull();
    expect(el!.style.display).toBe('none');
    expect(container.querySelector('[data-testid="wspr-body"]')).not.toBeNull();
    expect(mocks.wsprMounts).toBe(1);

    unmount();
  });
});
