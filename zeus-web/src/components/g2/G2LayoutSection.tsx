// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Settings → Display section for the G2 display layout: the touch-first
// chrome built for the radio's own 8-inch 1280x800 front glass (graphite
// theme + the bottom key drawer). Server-persisted beside the wideband
// flag so the radio remembers the choice across browsers and restarts.
// Display chrome only — turning it on/off never touches DSP or the wire.

import type { CSSProperties } from 'react';
import { useDisplaySettingsStore } from '../../state/display-settings-store';

export function G2LayoutSection() {
  const g2LayoutEnabled = useDisplaySettingsStore((s) => s.g2LayoutEnabled);
  const setG2LayoutEnabled = useDisplaySettingsStore((s) => s.setG2LayoutEnabled);

  return (
    <section>
      <div style={sectionHead}>
        <h3 style={sectionH3}>G2 Display Layout</h3>
        <p style={sectionP}>Touch chrome for the radio&apos;s 8-inch front panel.</p>
      </div>

      <div style={card}>
        <div style={row}>
          <label style={switchLabel}>
            <input
              type="checkbox"
              checked={g2LayoutEnabled}
              onChange={(event) => void setG2LayoutEnabled(event.currentTarget.checked)}
              style={{ accentColor: 'var(--accent)' }}
            />
            G2 touch drawer
          </label>
          <span style={hint}>
            Replaces the bottom transport bar with large touch keys (MOX, TUN,
            BAND, MODE, FILTER) and a compact FWD/SWR/ALC strip while connected.
            The desktop workspace is unchanged when this is off.
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
