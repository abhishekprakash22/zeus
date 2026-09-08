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

import { useTxStore } from '../../state/tx-store';
import { useState, useRef, useEffect } from 'react';
import type { CSSProperties } from 'react';
import { useDisplaySettingsStore } from '../../state/display-settings-store';


function G2MultiBadge() {
  // Laurence review item 2: the MULTI encoder's current assignment, visible.
  // Flashes briefly whenever the panel cycles it.
  const name = useTxStore((s) => s.multiEncoderFunction);
  const [flash, setFlash] = useState(false);
  const prev = useRef<string | null>(null);
  useEffect(() => {
    if (name && prev.current !== null && prev.current !== name) {
      setFlash(true);
      const t = setTimeout(() => setFlash(false), 1200);
      prev.current = name;
      return () => clearTimeout(t);
    }
    prev.current = name ?? null;
    return undefined;
  }, [name]);
  if (!name) return null;
  return (
    <div
      style={{
        position: 'fixed',
        top: 6,
        right: 10,
        zIndex: 55,
        padding: '3px 10px',
        borderRadius: 6,
        fontSize: 11,
        letterSpacing: 0.6,
        border: '1px solid ' + (flash ? 'var(--accent, #4aa3df)' : '#2a3a52'),
        background: flash ? 'var(--accent, #4aa3df)' : 'rgba(13,21,38,0.85)',
        color: flash ? '#08111f' : '#9fc3e8',
        transition: 'background 200ms, color 200ms, border-color 200ms',
        pointerEvents: 'none',
      }}
    >
      MULTI · {name.toUpperCase()}
    </div>
  );
}

export function G2LayoutSection() {
  const g2LayoutEnabled = useDisplaySettingsStore((s) => s.g2LayoutEnabled);
  const setG2LayoutEnabled = useDisplaySettingsStore((s) => s.setG2LayoutEnabled);

  return (
    <section>
      <G2MultiBadge />
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
