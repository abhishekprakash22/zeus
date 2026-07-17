// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';

import { act, render } from './meters/__tests__/harness';
import { usePanadapterRenderStore } from '../state/panadapter-render-store';
import { SpectrumControls } from './SpectrumControls';

function findButton(container: HTMLElement, label: string): HTMLButtonElement | null {
  return Array.from(container.querySelectorAll('button')).find(
    (button): button is HTMLButtonElement => button.textContent?.trim() === label,
  ) ?? null;
}

describe('SpectrumControls', () => {
  afterEach(() => {
    act(() => {
      usePanadapterRenderStore.setState({ panadapter3dEnabled: true });
    });
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it('toggles the panadapter between 3D and legacy 2D beside Pop', () => {
    const { container, unmount } = render(createElement(SpectrumControls));

    const enabled = findButton(container, '3D');
    expect(enabled).not.toBeNull();
    expect(enabled!.getAttribute('aria-pressed')).toBe('true');

    act(() => {
      enabled!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });

    const disabled = findButton(container, '2D');
    expect(disabled).not.toBeNull();
    expect(disabled!.getAttribute('aria-pressed')).toBe('false');
    expect(localStorage.getItem('zeus.panadapter.webgpu3d')).toBe('0');

    act(() => {
      disabled!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });

    const reenabled = findButton(container, '3D');
    expect(reenabled).not.toBeNull();
    expect(reenabled!.getAttribute('aria-pressed')).toBe('true');
    expect(localStorage.getItem('zeus.panadapter.webgpu3d')).toBe('1');
    unmount();
  });
});
