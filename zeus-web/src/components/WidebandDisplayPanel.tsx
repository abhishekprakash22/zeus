// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import type { CSSProperties } from 'react';
import { useDisplaySettingsStore } from '../state/display-settings-store';

export function WidebandDisplayPanel() {
  const widebandDisplayEnabled = useDisplaySettingsStore((s) => s.widebandDisplayEnabled);
  const setWidebandDisplayEnabled = useDisplaySettingsStore((s) => s.setWidebandDisplayEnabled);

  return (
    <section>
      <div style={sectionHead}>
        <h3 style={sectionH3}>Wideband Display</h3>
        <p style={sectionP}>Full-band panadapter and waterfall view.</p>
      </div>

      <div style={card}>
        <div style={row}>
          <label style={switchLabel}>
            <input
              type="checkbox"
              checked={widebandDisplayEnabled}
              onChange={(event) => void setWidebandDisplayEnabled(event.currentTarget.checked)}
              style={{ accentColor: 'var(--accent)' }}
            />
            ADC0 0-60 MHz
          </label>
          <span style={hint}>
            Uses bounded P2 ADC snapshots or P3 sidecar-projected full-band bins while a spectrum display is open.
          </span>
        </div>
      </div>
    </section>
  );
}

const sectionHead: CSSProperties = {
  display: 'flex',
  alignItems: 'baseline',
  flexWrap: 'wrap',
  gap: 10,
  marginBottom: 10,
};

const sectionH3: CSSProperties = {
  margin: 0,
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.18em',
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};

const sectionP: CSSProperties = {
  margin: 0,
  flex: '1 1 260px',
  fontSize: 12,
  lineHeight: 1.5,
  color: 'var(--fg-2)',
};

const card: CSSProperties = {
  display: 'grid',
  gap: 8,
  padding: 10,
  border: '1px solid var(--line)',
  borderRadius: 'var(--r-md)',
  background: 'linear-gradient(180deg, var(--bg-1), var(--bg-0))',
};

const row: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 10,
  flexWrap: 'wrap',
};

const switchLabel: CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 8,
  fontSize: 11,
  fontWeight: 800,
  letterSpacing: '0.08em',
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};

const hint: CSSProperties = {
  flex: '1 1 260px',
  fontSize: 11,
  lineHeight: 1.35,
  color: 'var(--fg-3)',
};
