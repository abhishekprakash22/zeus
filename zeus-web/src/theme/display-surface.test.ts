// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, type RenderResult } from '../components/meters/__tests__/harness';

vi.mock('../api/themeSettings', () => ({
  fetchThemeSettings: vi.fn(async () => ({
    theme: 'light',
    overrides: { '--light-displays': '#000000', '--display-bg-light': '#11243F', '--display-bg-dark': '#0C182C' },
  })),
  updateThemeSettings: vi.fn(async () => ({})),
}));

import {
  DARK_MAX_DEPTH,
  LIGHT_DEFAULT_DEPTH,
  LIGHT_MAX_DEPTH,
  LIGHT_MIN_DEPTH,
  blueDisplayTokens,
  clampDepth,
  depthHex,
  depthOf,
  depthRgb,
} from './display-surface';
import { contrast, rgbOf } from './paper';
import { displayFloorLutVersion, liftFloor, lutFor, setDisplayFloor } from '../gl/colormap';
import { displayOverrideEntries, displaysFromOverrides, useThemeStore } from '../state/theme-store';
import { updateThemeSettings } from '../api/themeSettings';
import { ThemeApplier } from '../components/ThemeApplier';

let mounted: RenderResult | null = null;
beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  localStorage.clear();
  useThemeStore.setState({ theme: 'light', lightDisplays: 'blue', displayDepthLight: 22, displayDepthDark: 0, hydrated: true });
});
afterEach(() => {
  mounted?.unmount();
  mounted = null;
  vi.useRealTimers();
  setDisplayFloor(null);
});

describe('blue depth path', () => {
  it('starts at black and runs to Steel', () => {
    expect(depthRgb(0)).toEqual([0, 0, 0]);
    expect(depthHex(22)).toBe('#0C182C');
    expect(depthHex(58)).toBe('#1A3456');
  });

  it('a stored colour maps back to its depth', () => {
    for (let d = 0; d <= LIGHT_MAX_DEPTH; d++) expect(depthOf(depthHex(d))).toBe(d);
    expect(depthOf('junk')).toBe(0);
  });

  it('clamps per theme: light Midnight..Steel, dark black..Ocean', () => {
    expect(clampDepth(0, 'light')).toBe(LIGHT_MIN_DEPTH);
    expect(clampDepth(99, 'light')).toBe(LIGHT_MAX_DEPTH);
    expect(clampDepth(99, 'dark')).toBe(DARK_MAX_DEPTH);
    expect(clampDepth(-5, 'dark')).toBe(0);
  });

  it('display ink and the default amber trace stay readable at every depth', () => {
    for (let d = 0; d <= LIGHT_MAX_DEPTH; d++) {
      const bg = depthRgb(d);
      const t = blueDisplayTokens(d);
      expect(contrast(rgbOf(t['--scale-ink']!)!, bg)).toBeGreaterThanOrEqual(4.5);
      expect(contrast(rgbOf(t['--disp-ink-0']!)!, bg)).toBeGreaterThanOrEqual(7);
      expect(contrast([255, 160, 40], bg)).toBeGreaterThanOrEqual(4.5);
    }
  });

  it('black keeps the classic display colours exactly', () => {
    const t = blueDisplayTokens(0);
    expect(t['--spec-bg']).toBe('#151A20');
    expect(t['--wf-0']).toBe('#000814');
    expect(t['--minipan-bg']).toBe('rgba(2, 4, 7, 0.48)');
  });

  it('the filter mini-pan sits on the same blue as the panadapter', () => {
    const t = blueDisplayTokens(40);
    const [r, g, b] = depthRgb(40);
    expect(t['--spec-bg']).toBe(depthHex(40));
    expect(t['--minipan-bg']).toBe(`rgb(${r}, ${g}, ${b})`);
  });
});

describe('palette floor lift', () => {
  it('the foot becomes the floor, the bright end is untouched', () => {
    const base = lutFor('blue');
    const lifted = liftFloor(base, [12, 24, 44]);
    expect(Array.from(lifted.slice(0, 3))).toEqual([12, 24, 44]);
    expect(Array.from(lifted.slice(100 * 4, 100 * 4 + 3))).toEqual([12, 24, 44]); // t≈0.39
    expect(Array.from(lifted.slice(255 * 4, 255 * 4 + 3))).toEqual(Array.from(base.slice(255 * 4, 255 * 4 + 3)));
  });

  it('every operator palette takes the floor; pop and paper keep their own', () => {
    setDisplayFloor([17, 36, 64]);
    for (const id of ['blue', 'inferno', 'viridis', 'amber', 'grey', 'rainbow'] as const) {
      expect(Array.from(lutFor(id).slice(0, 3))).toEqual([17, 36, 64]);
    }
    expect(Array.from(lutFor('pop').slice(0, 3))).not.toEqual([17, 36, 64]);
    const v = displayFloorLutVersion();
    setDisplayFloor(null);
    expect(displayFloorLutVersion()).toBe(v + 1);
    expect(Array.from(lutFor('blue').slice(0, 3))).toEqual([0, 0, 0]);
  });
});

describe('theme store: displays', () => {
  it('light defaults to blue Navy; the floor follows the store synchronously', () => {
    expect(useThemeStore.getState().displayDepthLight).toBe(LIGHT_DEFAULT_DEPTH);
    useThemeStore.getState().setDisplayDepth('light', 40);
    expect(Array.from(lutFor('blue').slice(0, 3))).toEqual(depthRgb(40));
    useThemeStore.getState().setLightDisplays('paper');
    expect(Array.from(lutFor('blue').slice(0, 3))).toEqual([0, 0, 0]);
  });

  it('dark at depth 0 is the classic black floor; a depth lifts it', () => {
    useThemeStore.getState().setTheme('dark');
    expect(Array.from(lutFor('blue').slice(0, 3))).toEqual([0, 0, 0]);
    useThemeStore.getState().setDisplayDepth('dark', 22);
    expect(Array.from(lutFor('blue').slice(0, 3))).toEqual(depthRgb(22));
  });

  it('persists to the radio and reads back', async () => {
    useThemeStore.getState().setDisplayDepth('light', 40);
    act(() => {
      vi.advanceTimersByTime(400);
    });
    const payload = vi.mocked(updateThemeSettings).mock.calls.at(-1)![0];
    expect(payload.overrides['--display-bg-light']).toBe(depthHex(40));
    expect(payload.overrides['--light-displays']).toBe('#000000');

    const back = displaysFromOverrides(
      displayOverrideEntries({ lightDisplays: 'paper', displayDepthLight: 58, displayDepthDark: 4 }),
      { lightDisplays: 'blue', displayDepthLight: 22, displayDepthDark: 0 },
    );
    expect(back).toEqual({ lightDisplays: 'paper', displayDepthLight: 58, displayDepthDark: 4 });

    useThemeStore.setState({ hydrated: false });
    await useThemeStore.getState().hydrate();
    expect(useThemeStore.getState().displayDepthLight).toBe(depthOf('#11243F'));
    expect(useThemeStore.getState().displayDepthDark).toBe(22);
  });
});

describe('theme applier: displays', () => {
  const css = () => document.getElementById('zeus-paper-tokens')?.textContent ?? '';

  it('light + blue: paper chrome with the blue display surface', () => {
    act(() => {
      mounted = render(createElement(ThemeApplier));
    });
    expect(css()).toContain(`--spec-bg: ${depthHex(22)};`);
    expect(css()).toContain('--minipan-bg: rgb(12, 24, 44);');
    expect(css()).toContain('--bg-1:'); // paper chrome still there
  });

  it('dark at black writes nothing; a dark depth writes only the displays', () => {
    useThemeStore.setState({ theme: 'dark' });
    act(() => {
      mounted = render(createElement(ThemeApplier));
    });
    expect(css()).toBe('');
    act(() => useThemeStore.setState({ displayDepthDark: 40 }));
    expect(css()).toContain(':root:not([data-theme="light"])');
    expect(css()).toContain(`--spec-bg: ${depthHex(40)};`);
    expect(css()).not.toContain('--bg-1:');
  });
});
