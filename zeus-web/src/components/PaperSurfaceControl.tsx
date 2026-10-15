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
// Settings → Theme → Light: the paper surface. One slider (lightness) and a
// tint pick the grey everything is derived from; the readout shows the glare
// and the contrast the derived ink actually achieves (it darkens itself to
// stay readable, so every position is safe).

import type { CSSProperties } from 'react';
import { useThemeStore } from '../state/theme-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import {
  PAPER_MAX_LIGHTNESS,
  PAPER_MIN_LIGHTNESS,
  contrast,
  luminance,
  paperParams,
  paperSurface,
  paperTokens,
  paperTraceRgb,
  rgbOf,
  type PaperTint,
} from '../theme/paper';

const TINTS: { id: PaperTint; label: string }[] = [
  { id: 'cool', label: 'Cool' },
  { id: 'neutral', label: 'Neutral' },
  { id: 'warm', label: 'Warm' },
];

const PRESETS: { label: string; lightness: number }[] = [
  { label: 'Silver', lightness: 94 },
  { label: 'Pearl', lightness: 90 },
  { label: 'Ash', lightness: 86 },
  { label: 'Pewter', lightness: 80 },
  { label: 'Slate', lightness: 74 },
  { label: 'Graphite', lightness: 68 },
];

export function PaperSurfaceControl() {
  const surface = useThemeStore((s) => s.paperSurface);
  const setSurface = useThemeStore((s) => s.setPaperSurface);
  const trace = useDisplaySettingsStore((s) => s.rxTraceColor);
  const { lightness, tint } = paperParams(surface);
  const tokens = paperTokens(surface);
  const S = rgbOf(surface)!;
  const card = rgbOf(tokens['--bg-1']!)!;
  const ink = rgbOf(tokens['--fg-0']!)!;
  const label = rgbOf(tokens['--fg-2']!)!;
  const traceInk = paperTraceRgb(trace || '#FFA028', surface);
  const glare = Math.round(luminance(S) * 100);

  return (
    <div style={wrap} data-testid="paper-surface">
      <div style={head}>Paper surface</div>
      <div style={row}>
        {PRESETS.map((p) => (
          <button
            key={p.label}
            type="button"
            aria-pressed={p.lightness === lightness}
            onClick={() => setSurface(paperSurface(p.lightness, tint))}
            style={chip(p.lightness === lightness)}
          >
            <span style={{ ...dot, background: paperSurface(p.lightness, tint) }} />
            {p.label}
          </button>
        ))}
      </div>
      <label style={{ ...row, alignItems: 'center' }}>
        <span style={lbl}>Lightness</span>
        <input
          type="range"
          aria-label="Paper lightness"
          min={PAPER_MIN_LIGHTNESS}
          max={PAPER_MAX_LIGHTNESS}
          step={1}
          value={lightness}
          onChange={(e) => setSurface(paperSurface(Number(e.currentTarget.value), tint))}
          style={{ flex: '1 1 220px', minHeight: 32 }}
        />
        <span style={mono}>{lightness}%</span>
      </label>
      <div style={row} role="radiogroup" aria-label="Paper tint">
        <span style={lbl}>Tint</span>
        {TINTS.map((t) => (
          <button
            key={t.id}
            type="button"
            role="radio"
            aria-checked={t.id === tint}
            onClick={() => setSurface(paperSurface(lightness, t.id))}
            style={chip(t.id === tint)}
          >
            {t.label}
          </button>
        ))}
      </div>
      <div style={stats} data-testid="paper-stats">
        Surface <b>{surface}</b> · brightness <b>{glare}%</b> of white · digits{' '}
        <b>{contrast(ink, card).toFixed(1)}:1</b> · labels <b>{contrast(label, S).toFixed(1)}:1</b> · trace{' '}
        <b>{contrast(traceInk, S).toFixed(1)}:1</b>
      </div>
      <p style={hint}>
        The chassis and cards follow this grey (and the displays too, with Displays: Paper). Text darkens
        automatically to stay readable at any setting.
      </p>
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
const mono: CSSProperties = { fontFamily: 'var(--font-mono, monospace)', fontSize: 12, color: 'var(--fg-1)', minWidth: 40 };
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
