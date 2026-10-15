// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, type RenderResult } from './meters/__tests__/harness';

vi.mock('../api/themeSettings', () => ({
  fetchThemeSettings: vi.fn(async () => ({ theme: 'light', overrides: { '--paper-surface': '#C2C2C2' } })),
  updateThemeSettings: vi.fn(async () => ({})),
}));

import { updateThemeSettings } from '../api/themeSettings';
import { DEFAULT_PAPER_SURFACE, useThemeStore } from '../state/theme-store';
import { PaperSurfaceControl } from './PaperSurfaceControl';
import { ThemeApplier } from './ThemeApplier';
import { paperSurface } from '../theme/paper';

let mounted: RenderResult | null = null;
beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  localStorage.clear();
  useThemeStore.setState({
    theme: 'light',
    overrides: {},
    paperSurface: DEFAULT_PAPER_SURFACE,
    lightDisplays: 'blue',
    displayDepthLight: 22,
    displayDepthDark: 0,
    hydrated: true,
  });
});
afterEach(() => {
  mounted?.unmount();
  mounted = null;
  vi.useRealTimers();
  vi.mocked(updateThemeSettings).mockClear();
});

describe('paper surface control', () => {
  it('the slider sets the surface, which persists locally and to the radio', () => {
    act(() => {
      mounted = render(createElement(PaperSurfaceControl));
    });
    const slider = mounted!.container.querySelector('input[type="range"]') as HTMLInputElement;
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!;
      setter.call(slider, '74');
      slider.dispatchEvent(new Event('input', { bubbles: true }));
    });
    const want = paperSurface(74, 'neutral');
    expect(useThemeStore.getState().paperSurface).toBe(want);
    expect(localStorage.getItem('zeus.theme.paper')).toBe(want);
    act(() => {
      vi.advanceTimersByTime(400);
    });
    const payload = vi.mocked(updateThemeSettings).mock.calls.at(-1)![0];
    expect(payload.overrides['--paper-surface']).toBe(want);
  });

  it('tint buttons keep the lightness and change the cast', () => {
    useThemeStore.setState({ paperSurface: paperSurface(80, 'neutral') });
    act(() => {
      mounted = render(createElement(PaperSurfaceControl));
    });
    const warm = Array.from(mounted!.container.querySelectorAll('button')).find((b) => b.textContent === 'Warm')!;
    act(() => warm.click());
    expect(useThemeStore.getState().paperSurface).toBe(paperSurface(80, 'warm'));
  });

  it('shows the achieved contrast', () => {
    act(() => {
      mounted = render(createElement(PaperSurfaceControl));
    });
    expect(mounted!.container.querySelector('[data-testid="paper-stats"]')!.textContent).toMatch(/digits \d+\.\d:1/);
  });
});

describe('theme applier', () => {
  it('writes the paper tokens only in the light theme', () => {
    useThemeStore.setState({ lightDisplays: 'paper' });
    act(() => {
      mounted = render(createElement(ThemeApplier));
    });
    const css = () => document.getElementById('zeus-paper-tokens')?.textContent ?? '';
    expect(css()).toContain(`--spec-bg: ${DEFAULT_PAPER_SURFACE};`);
    act(() => useThemeStore.setState({ theme: 'dark' }));
    expect(css()).toBe('');
  });

  it("hydrate adopts the radio's stored surface", async () => {
    useThemeStore.setState({ hydrated: false });
    await useThemeStore.getState().hydrate();
    expect(useThemeStore.getState().paperSurface).toBe('#C2C2C2');
  });
});
