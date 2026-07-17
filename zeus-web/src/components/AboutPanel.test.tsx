// SPDX-License-Identifier: GPL-2.0-or-later

import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// The desktop Photino webview swallows target="_blank" navigations, so the
// About panel must route external links through openExternalUrl (which posts
// zeus.openExternal to the C# host). Mock it so we can assert the wiring.
const openExternalUrl = vi.fn();
vi.mock('./report-problem/openExternalUrl', () => ({
  openExternalUrl: (url: string) => openExternalUrl(url),
}));

import { AboutPanel } from './AboutPanel';

describe('AboutPanel external links', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(async () => {
    openExternalUrl.mockClear();
    // AboutPanel fetches /api/version on mount; stub it so the effect resolves.
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve({ json: () => Promise.resolve({ version: 'test' }) })),
    );
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
    await act(async () => {
      root.render(<AboutPanel />);
    });
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    vi.unstubAllGlobals();
  });

  function clickLinkByText(text: string): void {
    const link = Array.from(container.querySelectorAll('a')).find((a) =>
      a.textContent?.includes(text),
    );
    expect(link, `link "${text}" should render`).toBeTruthy();
    act(() => {
      link!.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    });
  }

  it('opens the operator manual via the host bridge with an absolute URL', () => {
    clickLinkByText('Open the User Manual (PDF)');
    expect(openExternalUrl).toHaveBeenCalledTimes(1);
    const url = openExternalUrl.mock.calls[0]?.[0] as string;
    // Must be absolute (the host bridge rejects relative URLs) and point at /manual.
    expect(url).toMatch(/^https?:\/\//);
    expect(new URL(url).pathname).toBe('/manual');
  });

  it('opens the GitHub link via the host bridge', () => {
    clickLinkByText('github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus');
    expect(openExternalUrl).toHaveBeenCalledTimes(1);
    expect(openExternalUrl).toHaveBeenCalledWith(
      'https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus',
    );
  });

  it('prevents the default anchor navigation so the webview never swallows it', () => {
    const link = Array.from(container.querySelectorAll('a')).find((a) =>
      a.textContent?.includes('Open the User Manual (PDF)'),
    )!;
    const event = new MouseEvent('click', { bubbles: true, cancelable: true });
    act(() => {
      link.dispatchEvent(event);
    });
    expect(event.defaultPrevented).toBe(true);
  });
});
