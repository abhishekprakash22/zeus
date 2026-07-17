// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Regression coverage for #1269: an OPEN FreeDvWindow must stay MOUNTED
// (hidden via display:none) while Settings replaces the workspace, so the
// FreeDV modem session / TX-text draft isn't torn down on a Settings
// open/close. Only a CLOSED window unmounts.

/** @vitest-environment jsdom */

// harness first — installs the localStorage polyfill before any store import.
import { render, act } from '../meters/__tests__/harness';
import { describe, expect, it, beforeEach, vi } from 'vitest';
import { createElement } from 'react';

const mocks = vi.hoisted(() => ({ panelMounts: 0 }));

vi.mock('../../layout/panels/FreeDvPanel', async () => {
  const React = await import('react');
  return {
    FreeDvPanel: () => {
      React.useEffect(() => {
        mocks.panelMounts += 1;
      }, []);
      return React.createElement('div', { 'data-testid': 'freedv-body' });
    },
  };
});

import { FreeDvWindow } from '../FreeDvWindow';
import { useFreeDvWindowStore } from '../../state/freedv-window-store';
import { useLayoutStore } from '../../state/layout-store';

function windowEl(container: HTMLElement): HTMLElement | null {
  return container.querySelector('[role="dialog"][aria-label="FreeDV"]');
}

beforeEach(() => {
  mocks.panelMounts = 0;
  act(() => {
    useFreeDvWindowStore.setState({ isOpen: false });
    useLayoutStore.setState({ settingsViewOpen: false });
  });
});

describe('FreeDvWindow — mounted-but-hidden while Settings is open (#1269)', () => {
  it('unmounts entirely when closed', () => {
    const { container, unmount } = render(createElement(FreeDvWindow));
    expect(windowEl(container)).toBeNull();
    expect(mocks.panelMounts).toBe(0);
    unmount();
  });

  it('is visible (display flex) when open and Settings is closed', () => {
    act(() => {
      useFreeDvWindowStore.setState({ isOpen: true });
    });
    const { container, unmount } = render(createElement(FreeDvWindow));
    const el = windowEl(container);
    expect(el).not.toBeNull();
    expect(el!.style.display).toBe('flex');
    expect(container.querySelector('[data-testid="freedv-body"]')).not.toBeNull();
    expect(mocks.panelMounts).toBe(1);
    unmount();
  });

  it('stays mounted but hidden (display:none) when Settings opens', () => {
    act(() => {
      useFreeDvWindowStore.setState({ isOpen: true });
    });
    const { container, unmount } = render(createElement(FreeDvWindow));

    const before = windowEl(container);
    expect(before).not.toBeNull();
    expect(mocks.panelMounts).toBe(1);

    act(() => {
      useLayoutStore.setState({ settingsViewOpen: true });
    });

    const during = windowEl(container);
    expect(during).toBe(before); // reused node — not a remount
    expect(during!.style.display).toBe('none');
    expect(container.querySelector('[data-testid="freedv-body"]')).not.toBeNull();
    expect(mocks.panelMounts).toBe(1); // draft/session preserved

    act(() => {
      useLayoutStore.setState({ settingsViewOpen: false });
    });
    const after = windowEl(container);
    expect(after).toBe(before);
    expect(after!.style.display).toBe('flex');
    expect(mocks.panelMounts).toBe(1);

    unmount();
  });
});
