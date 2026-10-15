// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Settings → Theme: the spectrum displays' background (panadapter, waterfall,
// filter mini-pan). Light: Blue (default, Navy) or Paper; Dark: black (the
// classic look) through Ocean. See theme/display-surface.ts.

import type { CSSProperties } from 'react';
import { useThemeStore } from '../state/theme-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import {
  DARK_MAX_DEPTH,
  DISPLAY_DEPTH_MIN,
  DISPLAY_PRESETS,
  LIGHT_MAX_DEPTH,
  LIGHT_MIN_DEPTH,
  blueDisplayTokens,
  depthHex,
  depthRgb,
} from '../theme/display-surface';
import { contrast, rgbOf } from '../theme/paper';

export function DisplaySurfaceControl() {
  const theme = useThemeStore((s) => s.theme);
  const lightDisplays = useThemeStore((s) => s.lightDisplays);
  const depthLight = useThemeStore((s) => s.displayDepthLight);
  const depthDark = useThemeStore((s) => s.displayDepthDark);
  const setLightDisplays = useThemeStore((s) => s.setLightDisplays);
  const setDepth = useThemeStore((s) => s.setDisplayDepth);
  const trace = useDisplaySettingsStore((s) => s.rxTraceColor);

  const light = theme === 'light';
  const blue = !light || lightDisplays === 'blue';
  const depth = light ? depthLight : depthDark;
  const min = light ? LIGHT_MIN_DEPTH : DISPLAY_DEPTH_MIN;
  const max = light ? LIGHT_MAX_DEPTH : DARK_MAX_DEPTH;
  const presets = DISPLAY_PRESETS.filter((p) => p.depth >= min && p.depth <= max);
  const bg = depthRgb(depth);
  const traceRgb = rgbOf(trace || '#FFA028') ?? [255, 160, 40];
  const labelRgb = rgbOf(blueDisplayTokens(depth)['--scale-ink']!)!;

  return (
    <div style={wrap} data-testid="display-surface">
      <div style={head}>Spectrum displays</div>
      {light && (
        <div style={row} role="radiogroup" aria-label="Display surface">
          {(['blue', 'paper'] as const).map((m) => (
            <button
              key={m}
              type="button"
              role="radio"
              aria-checked={lightDisplays === m}
              onClick={() => setLightDisplays(m)}
              style={chip(lightDisplays === m)}
            >
              {m === 'blue' ? 'Blue' : 'Paper'}
            </button>
          ))}
        </div>
      )}
      {blue ? (
        <>
          <div style={row}>
            {presets.map((p) => (
              <button
                key={p.label}
                type="button"
                aria-pressed={p.depth === depth}
                onClick={() => setDepth(theme, p.depth)}
                style={chip(p.depth === depth)}
              >
                <span style={{ ...dot, background: depthHex(p.depth) }} />
                {p.label}
              </button>
            ))}
          </div>
          <label style={{ ...row, alignItems: 'center' }}>
            <span style={lbl}>Depth</span>
            <input
              type="range"
              aria-label="Display background depth"
              min={min}
              max={max}
              step={1}
              value={depth}
              onChange={(e) => setDepth(theme, Number(e.currentTarget.value))}
              style={{ flex: '1 1 220px', minHeight: 32 }}
            />
            <span style={mono}>{depthHex(depth)}</span>
          </label>
          <div style={stats} data-testid="display-stats">
            Trace <b>{contrast(traceRgb, bg).toFixed(1)}:1</b> · scale labels{' '}
            <b>{contrast(labelRgb, bg).toFixed(1)}:1</b>
          </div>
          <p style={hint}>
            The panadapter, waterfall and filter displays sit on this colour; the waterfall keeps your palette with its
            floor lifted to match.{' '}
            {light
              ? 'Stops at Steel — beyond it weak signals start to sink into the background.'
              : 'Black is the classic look.'}
          </p>
        </>
      ) : (
        <p style={hint}>Paper: the displays follow the paper grey above, with the paper waterfall palette.</p>
      )}
    </div>
  );
}

const wrap: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 10,
  marginTop: 12,
  padding: 12,
  border: '1px solid var(--line-soft)',
  borderRadius: 8,
  background: 'var(--bg-1)',
};
const head: CSSProperties = { fontSize: 11, letterSpacing: '0.08em', color: 'var(--fg-2)', fontWeight: 600 };
const row: CSSProperties = { display: 'flex', gap: 8, flexWrap: 'wrap' };
const lbl: CSSProperties = { fontSize: 12, color: 'var(--fg-2)', minWidth: 70, alignSelf: 'center' };
const mono: CSSProperties = { fontFamily: 'var(--font-mono, monospace)', fontSize: 12, color: 'var(--fg-1)', minWidth: 70 };
const dot: CSSProperties = { width: 14, height: 14, borderRadius: '50%', border: '1px solid var(--line)', display: 'inline-block' };
const chip = (on: boolean): CSSProperties => ({
  display: 'inline-flex',
  alignItems: 'center',
  gap: 6,
  minHeight: 32,
  padding: '4px 10px',
  borderRadius: 999,
  border: `1px solid ${on ? 'var(--accent)' : 'var(--line-soft)'}`,
  background: on ? 'color-mix(in srgb, var(--accent) 12%, transparent)' : 'transparent',
  color: 'var(--fg-1)',
  fontSize: 12,
  cursor: 'pointer',
});
const stats: CSSProperties = { fontSize: 12, color: 'var(--fg-2)' };
const hint: CSSProperties = { fontSize: 12, color: 'var(--fg-3)', margin: 0, lineHeight: 1.45 };
