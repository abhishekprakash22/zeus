// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, type RenderResult } from './meters/__tests__/harness';

const remote = vi.hoisted(() => ({ on: false }));
vi.mock('../remote/remote-client', () => ({ isRemoteMode: () => remote.on }));
vi.mock('./update-apply-ride', () => ({ rideApply: vi.fn(async () => {}), isApplyActive: () => false }));

import { EarlierVersionsSection } from './EarlierVersionsSection';

const LIST = {
  installedVersion: '1.148',
  canInstall: true,
  previousVersion: '1.147',
  canRollBack: true,
  releases: [
    { tag: 'v1.148', version: '1.148', name: null, publishedAt: '2026-10-23T02:30:00Z', manifestUrl: 'm148', pageUrl: 'p148', isInstalled: true, isOlder: false },
    { tag: 'v1.146', version: '1.146', name: null, publishedAt: '2026-10-21T10:00:00Z', manifestUrl: 'm146', pageUrl: 'p146', isInstalled: false, isOlder: true },
  ],
  error: null,
};

let mounted: RenderResult | null = null;
const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
  if (url === '/api/system/update/releases') return new Response(JSON.stringify(LIST));
  if (init?.method === 'POST')
    return new Response(JSON.stringify({ ok: true, status: { phase: 'downloading', percent: 0, targetVersion: '1.146', error: null } }));
  return new Response('{}');
});
const posts = (url: string) => fetchMock.mock.calls.filter(([u, i]) => u === url && (i as RequestInit | undefined)?.method === 'POST');
const btn = (text: string) =>
  Array.from(mounted!.container.querySelectorAll('button')).find((b) => b.textContent?.includes(text)) as HTMLButtonElement | undefined;
const flush = async () => {
  for (let i = 0; i < 6; i++) await Promise.resolve();
};

beforeEach(() => {
  remote.on = false;
  fetchMock.mockClear();
  vi.stubGlobal('fetch', fetchMock);
});
afterEach(() => {
  mounted?.unmount();
  mounted = null;
  vi.unstubAllGlobals();
});

async function openList() {
  await act(async () => {
    mounted = render(createElement(EarlierVersionsSection));
  });
  await act(async () => {
    btn('SHOW EARLIER VERSIONS')!.click();
    await flush();
  });
}

describe('earlier versions', () => {
  it('lists releases with install on the others and none on the installed one', async () => {
    await openList();
    const row148 = mounted!.container.querySelector('[data-testid="release-v1.148"]')!;
    const row146 = mounted!.container.querySelector('[data-testid="release-v1.146"]')!;
    expect(row148.textContent).toContain('installed');
    expect(row148.querySelector('button')).toBeNull();
    expect(row146.textContent).toContain('older');
    expect(row146.querySelector('button')?.textContent).toBe('INSTALL');
  });

  it('installs a chosen release only after confirming', async () => {
    await openList();
    await act(async () => mounted!.container.querySelector<HTMLButtonElement>('[data-testid="release-v1.146"] button')!.click());
    expect(posts('/api/system/update/install')).toHaveLength(0);
    expect(mounted!.container.textContent).toContain('Install 1.146?');
    await act(async () => {
      btn('CONFIRM')!.click();
      await flush();
    });
    const sent = posts('/api/system/update/install');
    expect(sent).toHaveLength(1);
    expect(JSON.parse(sent[0]![1]!.body as string)).toEqual({ tag: 'v1.146' });
  });

  it('rolls back to the kept previous version', async () => {
    await openList();
    await act(async () => btn('ROLL BACK TO 1.147')!.click());
    await act(async () => {
      btn('CONFIRM')!.click();
      await flush();
    });
    expect(posts('/api/system/update/rollback')).toHaveLength(1);
  });

  it('over remote the list is visible but nothing installs', async () => {
    remote.on = true;
    await openList();
    expect(mounted!.container.textContent).toContain('installed at the radio');
    expect(btn('ROLL BACK')).toBeUndefined();
    expect(btn('INSTALL')).toBeUndefined();
    expect(btn('RELEASE PAGE')).toBeDefined();
  });
});
