// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { describe, expect, it, vi, beforeEach } from 'vitest';

import { isApplyActive, rideApply } from './update-apply-ride';
import { fetchUpdateStatus, getUpdateApplyStatus, type UpdateApplyStatusDto } from '../api/client';

vi.mock('../api/client', () => ({
  fetchUpdateStatus: vi.fn(),
  getUpdateApplyStatus: vi.fn(),
}));

const mockedApply = vi.mocked(getUpdateApplyStatus);
const mockedStatus = vi.mocked(fetchUpdateStatus);

const noSleep = () => Promise.resolve();

function st(phase: UpdateApplyStatusDto['phase'], percent = 0, error: string | null = null): UpdateApplyStatusDto {
  return { phase, percent, targetVersion: null, error };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('isApplyActive', () => {
  it('marks the in-flight phases active and the terminal ones not', () => {
    expect(isApplyActive('downloading')).toBe(true);
    expect(isApplyActive('verifying')).toBe(true);
    expect(isApplyActive('swapping')).toBe(true);
    expect(isApplyActive('restarting')).toBe(true);
    expect(isApplyActive('idle')).toBe(false);
    expect(isApplyActive('failed')).toBe(false);
    expect(isApplyActive('unsupported')).toBe(false);
  });
});

describe('rideApply', () => {
  it('reports progress, reads two missed polls as the restart, and reloads when the server returns', async () => {
    mockedApply
      .mockResolvedValueOnce(st('downloading', 40))
      .mockResolvedValueOnce(st('swapping', 100))
      .mockRejectedValueOnce(new Error('down'))
      .mockRejectedValueOnce(new Error('down'));
    mockedStatus
      .mockRejectedValueOnce(new Error('still down'))
      .mockResolvedValueOnce({} as never);

    const onStatus = vi.fn();
    const onFailed = vi.fn();
    const onRestarting = vi.fn();
    const reload = vi.fn();

    await rideApply({ onStatus, onFailed, onRestarting, reload, sleep: noSleep });

    expect(onStatus).toHaveBeenCalledTimes(2);
    expect(onStatus).toHaveBeenNthCalledWith(1, st('downloading', 40));
    expect(onStatus).toHaveBeenNthCalledWith(2, st('swapping', 100));
    expect(onRestarting).toHaveBeenCalledTimes(1);
    expect(reload).toHaveBeenCalledTimes(1);
    expect(onFailed).not.toHaveBeenCalled();
  });

  it('one missed poll is tolerated — only consecutive misses mean the restart', async () => {
    mockedApply
      .mockResolvedValueOnce(st('downloading', 10))
      .mockRejectedValueOnce(new Error('blip'))
      .mockResolvedValueOnce(st('downloading', 60))
      .mockResolvedValueOnce(st('failed', 0, 'sha mismatch'));

    const onStatus = vi.fn();
    const onFailed = vi.fn();
    const onRestarting = vi.fn();
    const reload = vi.fn();

    await rideApply({ onStatus, onFailed, onRestarting, reload, sleep: noSleep });

    expect(onRestarting).not.toHaveBeenCalled();
    expect(reload).not.toHaveBeenCalled();
    expect(onFailed).toHaveBeenCalledWith(st('failed', 0, 'sha mismatch'));
  });

  it('a failed apply ends the ride without a reload', async () => {
    mockedApply.mockResolvedValueOnce(st('failed', 0, 'boom'));
    const onFailed = vi.fn();
    const reload = vi.fn();

    await rideApply({ onStatus: vi.fn(), onFailed, reload, sleep: noSleep });

    expect(onFailed).toHaveBeenCalledWith(st('failed', 0, 'boom'));
    expect(reload).not.toHaveBeenCalled();
  });

  it('an idle phase (nothing running under us) simply returns', async () => {
    mockedApply.mockResolvedValueOnce(st('idle'));
    const onStatus = vi.fn();
    const onFailed = vi.fn();
    const reload = vi.fn();

    await rideApply({ onStatus, onFailed, reload, sleep: noSleep });

    expect(onStatus).not.toHaveBeenCalled();
    expect(onFailed).not.toHaveBeenCalled();
    expect(reload).not.toHaveBeenCalled();
  });
});
