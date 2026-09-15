// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Pins the field lesson: clicking the reload toast must ALWAYS converge on a
// reload. When SKIP_WAITING produces no controlling worker (purged SW, dead
// worker, radio mid-restart from an install triggered on another screen), the
// button used to read UPDATING... forever.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { UpdatePrompt } from './UpdatePrompt';

function makeRoot() {
  const container = document.createElement('div');
  document.body.appendChild(container);
  const root = createRoot(container);
  return { container, root };
}

const noSleep = () => Promise.resolve();

describe('UpdatePrompt', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    const m = makeRoot();
    container = m.container;
    root = m.root;
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    vi.restoreAllMocks();
  });

  it('hard-reloads once the server answers, even when the SW path does nothing', async () => {
    // onUpdate resolving without a reload = the stuck field case.
    const onUpdate = vi.fn().mockResolvedValue(undefined);
    const reloadFn = vi.fn();
    const fetchMock = vi.fn().mockResolvedValue({ ok: true });
    vi.stubGlobal('fetch', fetchMock);

    await act(async () => {
      root.render(
        <UpdatePrompt show onUpdate={onUpdate} reloadFn={reloadFn} sleepFn={noSleep} />,
      );
    });

    const button = container.querySelector('button');
    expect(button).not.toBeNull();
    await act(async () => {
      button!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });

    expect(onUpdate).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith('/api/version', { cache: 'no-store' });
    expect(reloadFn).toHaveBeenCalledTimes(1);
  });

  it('keeps probing while the radio restarts, shows the waiting line, then reloads', async () => {
    const onUpdate = vi.fn().mockResolvedValue(undefined);
    const reloadFn = vi.fn();
    const fetchMock = vi
      .fn()
      .mockRejectedValueOnce(new Error('down'))
      .mockRejectedValueOnce(new Error('down'))
      .mockResolvedValueOnce({ ok: true });
    vi.stubGlobal('fetch', fetchMock);

    await act(async () => {
      root.render(
        <UpdatePrompt show onUpdate={onUpdate} reloadFn={reloadFn} sleepFn={noSleep} />,
      );
    });

    await act(async () => {
      container.querySelector('button')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });

    expect(container.textContent).toContain('Waiting for the radio to come back');
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(reloadFn).toHaveBeenCalledTimes(1);
  });

  it('converges to a reload even when onUpdate itself throws', async () => {
    const onUpdate = vi.fn().mockRejectedValue(new Error('sw exploded'));
    const reloadFn = vi.fn();
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true }));
    vi.spyOn(console, 'error').mockImplementation(() => {});

    await act(async () => {
      root.render(
        <UpdatePrompt show onUpdate={onUpdate} reloadFn={reloadFn} sleepFn={noSleep} />,
      );
    });

    await act(async () => {
      container.querySelector('button')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });

    expect(reloadFn).toHaveBeenCalledTimes(1);
  });

  it('a second click while updating does not start a second convergence loop', async () => {
    const onUpdate = vi.fn().mockResolvedValue(undefined);
    const reloadFn = vi.fn();
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true }));

    await act(async () => {
      root.render(
        <UpdatePrompt show onUpdate={onUpdate} reloadFn={reloadFn} sleepFn={noSleep} />,
      );
    });

    const button = container.querySelector('button')!;
    await act(async () => {
      button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });

    expect(onUpdate).toHaveBeenCalledTimes(1);
    expect(reloadFn).toHaveBeenCalledTimes(1);
  });
});
