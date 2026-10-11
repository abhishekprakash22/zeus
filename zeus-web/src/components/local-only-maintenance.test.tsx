// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

/** @vitest-environment jsdom */

import { act, render, type RenderResult } from './meters/__tests__/harness';
import { createElement, type ReactElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

let remote = false;
vi.mock('../remote/remote-client', () => ({ isRemoteMode: () => remote }));

vi.mock('../api/client', () => ({
  getP2AppStatus: vi.fn(async () => ({
    mode: 'Supervised', pid: 1234, binaryPath: '/home/pi/github/Saturn/p2app', restarts: 0, lastExitCode: null,
  })),
  getP2AppUpdateStatus: vi.fn(async () => ({
    phase: 'idle', error: null, repoDir: null, head: 'abc1234', running: false, rolledBack: false, log: [],
  })),
  postP2AppUpdate: vi.fn(),
  fetchFpgaStatus: vi.fn(async () => ({
    phase: 'idle', progress: 0, detail: '', error: null, primaryAddrHex: '0x980000', saturnPresent: true,
  })),
  fetchFpgaImages: vi.fn(),
  compareFpgaImage: vi.fn(),
  startFpgaFlash: vi.fn(),
}));

import { P2AppUpdateSection } from './P2AppUpdateSection';
import { FpgaFlashSection } from './FpgaFlashSection';
import { LOCAL_ONLY_NOTE } from '../remote/local-only';

let mounted: RenderResult | null = null;
async function mount(c: () => ReactElement | null): Promise<string> {
  await act(async () => {
    mounted = render(createElement(c));
  });
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
  return mounted!.container.textContent ?? '';
}

beforeEach(() => {
  remote = false;
});
afterEach(() => {
  mounted?.unmount();
  mounted = null;
});

describe('maintenance is local-only', () => {
  it('p2app: the radio screen offers UPDATE and STOP', async () => {
    const text = await mount(P2AppUpdateSection);
    expect(text).toContain('UPDATE P2APP');
    expect(text).toContain('STOP');
    expect(text).not.toContain(LOCAL_ONLY_NOTE);
  });

  it('p2app: a remote session sees status but no UPDATE / STOP / START', async () => {
    remote = true;
    const text = await mount(P2AppUpdateSection);
    expect(text).toContain('Running — started and kept alive');
    expect(text).not.toContain('UPDATE P2APP');
    expect(mounted!.container.querySelectorAll('button').length).toBe(0);
    expect(text).toContain(LOCAL_ONLY_NOTE);
  });

  it('FPGA: the radio screen offers the image shelf', async () => {
    const text = await mount(FpgaFlashSection);
    expect(text).toContain('LOAD AVAILABLE IMAGES');
  });

  it('FPGA: a remote session gets the note, never the flash controls', async () => {
    remote = true;
    const text = await mount(FpgaFlashSection);
    expect(text).not.toContain('LOAD AVAILABLE IMAGES');
    expect(mounted!.container.querySelectorAll('button').length).toBe(0);
    expect(text).toContain(LOCAL_ONLY_NOTE);
  });
});
