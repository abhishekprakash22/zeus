// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { act, render } from './meters/__tests__/harness';
import CrashConsentModal from './CrashConsentModal';

function response(body: unknown, ok = true, status = 200): Response {
  return {
    ok,
    status,
    statusText: ok ? 'OK' : 'Error',
    json: async () => body,
  } as Response;
}

async function waitFor(cond: () => boolean): Promise<void> {
  for (let i = 0; i < 50; i++) {
    if (cond()) return;
    // eslint-disable-next-line no-await-in-loop
    await act(async () => {
      await new Promise((r) => setTimeout(r, 0));
    });
  }
  expect(cond()).toBe(true);
}

function dialog(container: HTMLElement): HTMLElement | null {
  return container.querySelector('[role="dialog"]');
}

function button(container: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(container.querySelectorAll('button')).find(
    (b) => b.textContent === text,
  );
  expect(match, `button "${text}" should render`).toBeTruthy();
  return match as HTMLButtonElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CrashConsentModal', () => {
  it('stays hidden when the answered version is current', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () =>
      response({
        available: false,
        autoShareCrashes: false,
        agreementVersion: 1,
        currentAgreementVersion: 1,
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    const { container, unmount } = render(createElement(CrashConsentModal));

    await waitFor(() => fetchMock.mock.calls.length === 1);
    expect(dialog(container)).toBeNull();
    unmount();
  });

  it('shows when the answered version is behind', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(async () =>
        response({
          available: false,
          autoShareCrashes: false,
          agreementVersion: 0,
          currentAgreementVersion: 1,
        }),
      ),
    );

    const { container, unmount } = render(createElement(CrashConsentModal));

    await waitFor(() => dialog(container) !== null);
    expect(container.textContent).toContain('Help improve Zeus');
    expect(container.textContent).toContain('Automatically send crash logs to the developers?');
    unmount();
  });

  it('posts optIn=true and dismisses on opt-in', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      if (String(input) === '/api/support/availability') {
        return response({
          available: false,
          autoShareCrashes: false,
          agreementVersion: 0,
          currentAgreementVersion: 1,
        });
      }
      return response({
        available: true,
        autoShareCrashes: true,
        agreementVersion: 1,
        currentAgreementVersion: 1,
      });
    });
    vi.stubGlobal('fetch', fetchMock);
    const { container, unmount } = render(createElement(CrashConsentModal));
    await waitFor(() => dialog(container) !== null);

    await act(async () => {
      button(container, 'Yes, send crash reports').dispatchEvent(
        new MouseEvent('click', { bubbles: true }),
      );
    });

    await waitFor(() => dialog(container) === null);
    const post = fetchMock.mock.calls.find((call) => String(call[0]) === '/api/support/agreement');
    expect(post?.[1]?.method).toBe('POST');
    expect(post?.[1]?.body).toBe(JSON.stringify({ optIn: true }));
    unmount();
  });

  it('posts optIn=false and dismisses on opt-out', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      if (String(input) === '/api/support/availability') {
        return response({
          available: true,
          autoShareCrashes: true,
          agreementVersion: 0,
          currentAgreementVersion: 1,
        });
      }
      return response({
        available: true,
        autoShareCrashes: true,
        agreementVersion: 1,
        currentAgreementVersion: 1,
      });
    });
    vi.stubGlobal('fetch', fetchMock);
    const { container, unmount } = render(createElement(CrashConsentModal));
    await waitFor(() => dialog(container) !== null);

    await act(async () => {
      button(container, 'No thanks').dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });

    await waitFor(() => dialog(container) === null);
    const post = fetchMock.mock.calls.find((call) => String(call[0]) === '/api/support/agreement');
    expect(post?.[1]?.method).toBe('POST');
    expect(post?.[1]?.body).toBe(JSON.stringify({ optIn: false }));
    unmount();
  });

  it('renders nothing when the availability fetch fails', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => {
      throw new Error('offline');
    });
    vi.stubGlobal('fetch', fetchMock);

    const { container, unmount } = render(createElement(CrashConsentModal));

    await waitFor(() => fetchMock.mock.calls.length === 1);
    expect(dialog(container)).toBeNull();
    unmount();
  });
});
