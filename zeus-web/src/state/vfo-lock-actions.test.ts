// SPDX-License-Identifier: GPL-2.0-or-later
import { beforeEach, describe, expect, it, vi } from 'vitest';

const setVfoLock = vi.fn();
vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return { ...actual, setVfoLock: (locked: boolean) => setVfoLock(locked) };
});

import { normalizeState } from '../api/client';
import { useConnectionStore } from './connection-store';
import { useVfoLockStore } from './vfo-lock-store';
import { toggleRadioVfoLock } from './vfo-lock-actions';

const flush = () => new Promise((r) => setTimeout(r, 0));

describe('radio VFO lock', () => {
  beforeEach(() => {
    setVfoLock.mockReset();
    useVfoLockStore.getState().setLocked(false);
  });

  it('a state snapshot carrying vfoLocked drives the screen flag (front-panel LOCK)', () => {
    useConnectionStore.getState().applyState(normalizeState({ vfoLocked: true }));
    expect(useVfoLockStore.getState().locked).toBe(true);
    useConnectionStore.getState().applyState(normalizeState({ vfoLocked: false }));
    expect(useVfoLockStore.getState().locked).toBe(false);
  });

  it('a legacy snapshot without the field leaves the flag alone', () => {
    useVfoLockStore.getState().setLocked(true);
    useConnectionStore.getState().applyState(normalizeState({}));
    expect(useVfoLockStore.getState().locked).toBe(true);
  });

  it('the screen key posts to the radio and adopts its answer', async () => {
    setVfoLock.mockResolvedValue(normalizeState({ vfoLocked: true }));
    toggleRadioVfoLock();
    expect(setVfoLock).toHaveBeenCalledWith(true);
    expect(useVfoLockStore.getState().locked).toBe(true);
    await flush();
    expect(useVfoLockStore.getState().locked).toBe(true);
  });

  it('a failed post rolls the flag back', async () => {
    setVfoLock.mockRejectedValue(new Error('409'));
    toggleRadioVfoLock();
    expect(useVfoLockStore.getState().locked).toBe(true);
    await flush();
    expect(useVfoLockStore.getState().locked).toBe(false);
  });
});
