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
// Display → EFFECTS: Off / Subtle / Full (see state/effects-store.ts).

import type { CSSProperties } from 'react';
import { EFFECTS_LEVELS, useEffectsStore, type EffectsLevel } from '../state/effects-store';

const LABEL: Record<EffectsLevel, string> = { off: 'Off', subtle: 'Subtle', full: 'Full' };
const DESCRIBE: Record<EffectsLevel, string> = {
  off: 'Classic look: single-colour trace, plain passband, static VFO digits.',
  subtle:
    'Trace coloured by the waterfall palette, waterfall dimmed outside the receive filter, rolling VFO digits.',
  full: 'Subtle plus phosphor persistence on the panadapter: steady carriers burn in, bursts glow and fade.',
};

export function EffectsSettingsPanel() {
  const level = useEffectsStore((s) => s.level);
  const reduced = useEffectsStore((s) => s.reducedMotion);
  const setLevel = useEffectsStore((s) => s.setLevel);
  return (
    <div style={wrap}>
      <span style={label}>EFFECTS</span>
      <div role="radiogroup" aria-label="Display effects" style={row}>
        {EFFECTS_LEVELS.map((l) => (
          <button
            key={l}
            type="button"
            role="radio"
            aria-checked={level === l}
            className={`btn sm${level === l ? ' active' : ''}`}
            style={seg}
            onClick={() => setLevel(l)}
          >
            {LABEL[l]}
          </button>
        ))}
      </div>
      <span style={hint}>
        {DESCRIBE[level]}
        {reduced && level !== 'off' ? ' Rolling digits are off because this device asks for reduced motion.' : ''}
      </span>
    </div>
  );
}

const wrap: CSSProperties = { display: 'flex', flexDirection: 'column', gap: 8, padding: '6px 0' };
const label: CSSProperties = { fontSize: 10, letterSpacing: '0.08em', color: 'var(--fg-3, #8a94a3)' };
const row: CSSProperties = { display: 'flex', gap: 6, flexWrap: 'wrap' };
const seg: CSSProperties = { minWidth: 72, minHeight: 36 };
const hint: CSSProperties = { fontSize: 11, lineHeight: 1.4, color: 'var(--fg-3, #8a94a3)', maxWidth: 520 };
