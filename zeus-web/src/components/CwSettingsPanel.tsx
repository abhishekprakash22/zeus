// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Settings -> CW: the keyer's CONFIGURATION home. Everything here is a
// persisted setting behind PUT /api/cw/settings (PATCH semantics): speed,
// Farnsworth, keyer mode, sidetone, and the GPIO paddle cluster. The CW
// workspace panel keeps the OPERATING controls (send box, macros, live WPM
// slider) — both surfaces read and write the same server-side store, so
// there is one source of truth and no divergence.

import { useCallback, useEffect, useState } from 'react';
import {
  DEFAULT_CW_SETTINGS,
  fetchCwSettings,
  saveCwSettings,
  type CwKeyerMode,
  type CwSettings,
} from '../api/cw';

const MODES: ReadonlyArray<{ id: CwKeyerMode; label: string; hint: string }> = [
  { id: 'Straight', label: 'STRAIGHT', hint: 'Straight key, bug, or external keyer — either contact keys directly' },
  { id: 'IambicA', label: 'IAMBIC A', hint: 'Releasing both paddles finishes the current element' },
  { id: 'IambicB', label: 'IAMBIC B', hint: 'Releasing both paddles adds one opposite element' },
];

const row: React.CSSProperties = {
  display: 'flex', gap: 14, alignItems: 'center', flexWrap: 'wrap', marginBottom: 12,
};
const label: React.CSSProperties = {
  display: 'inline-flex', gap: 6, alignItems: 'center', fontSize: 11, letterSpacing: '.05em',
};
const head: React.CSSProperties = {
  fontSize: 11, fontWeight: 700, letterSpacing: '.08em', opacity: .75,
  margin: '16px 0 8px',
};

export function CwSettingsPanel() {
  const [s, setS] = useState<CwSettings>(DEFAULT_CW_SETTINGS);
  const [err, setErr] = useState('');

  useEffect(() => {
    const ac = new AbortController();
    void fetchCwSettings(ac.signal)
      .then(setS)
      .catch(() => undefined);
    return () => ac.abort();
  }, []);

  const patch = useCallback((p: Partial<CwSettings>) => {
    setS((prev) => ({ ...prev, ...p }));       // optimistic
    void saveCwSettings(p)
      .then((saved) => {
        setS(saved);                            // server-clamped truth
        setErr('');
      })
      .catch((e) => setErr((e as Error)?.message ?? 'save failed'));
  }, []);

  return (
    <div className="cw-settings">
      <div style={head}>KEYER</div>
      <div style={row}>
        <label style={label}>
          SPEED
          <input
            type="number" min={5} max={50} value={s.wpm}
            onChange={(e) => patch({ wpm: Number(e.currentTarget.value) })}
          />
          WPM
        </label>
        <label style={label} title="Character speed stays at SPEED; spacing stretches to this. 0 disables.">
          FARNSWORTH
          <input
            type="number" min={0} max={50} value={s.farnsworthWpm ?? 0}
            onChange={(e) => patch({ farnsworthWpm: Number(e.currentTarget.value) || null })}
          />
          WPM
        </label>
      </div>
      <div style={row}>
        {MODES.map((m) => (
          <label key={m.id} style={label} title={m.hint}>
            <input
              type="radio" name="cw-keyer-mode"
              checked={s.keyerMode === m.id}
              onChange={() => patch({ keyerMode: m.id })}
            />
            {m.label}
          </label>
        ))}
      </div>
      <div style={{ fontSize: 10, opacity: .6, marginBottom: 4 }}>
        Speed and mode drive every keyer: the radio's own key jack, the GPIO
        paddle below, and keyboard/macro sends.
      </div>

      <div style={head}>SIDETONE</div>
      <div style={row}>
        <label style={label}>
          PITCH
          <input
            type="number" min={200} max={1200} step={10} value={s.sidetoneHz}
            onChange={(e) => patch({ sidetoneHz: Number(e.currentTarget.value) })}
          />
          Hz
        </label>
        <label style={label}>
          LEVEL
          <input
            type="number" min={-60} max={0} value={Math.round(s.sidetoneGainDb)}
            onChange={(e) => patch({ sidetoneGainDb: Number(e.currentTarget.value) })}
          />
          dB
        </label>
      </div>

      <div style={head}>PADDLE ON GPIO</div>
      <div style={{ fontSize: 10, opacity: .6, marginBottom: 8 }}>
        A paddle plugged into this computer's GPIO header (Raspberry Pi).
        Wiring: DOT and DASH contacts to the pins below, common to GND —
        internal pull-ups, no resistors. The radio's own key jack does not
        need any of this. Keys real RF when enabled.
      </div>
      <div style={row}>
        <label style={label}>
          <input
            type="checkbox"
            checked={s.paddleGpioEnabled}
            onChange={(e) => patch({ paddleGpioEnabled: e.currentTarget.checked })}
          />
          ENABLED
        </label>
        <label style={label}>
          DOT GPIO
          <input
            type="number" min={0} max={27} value={s.paddleDotPin}
            disabled={!s.paddleGpioEnabled}
            onChange={(e) => patch({ paddleDotPin: Number(e.currentTarget.value) })}
          />
        </label>
        <label style={label}>
          DASH GPIO
          <input
            type="number" min={0} max={27} value={s.paddleDashPin}
            disabled={!s.paddleGpioEnabled}
            onChange={(e) => patch({ paddleDashPin: Number(e.currentTarget.value) })}
          />
        </label>
        <label style={label} title="Swap dot and dash contacts">
          <input
            type="checkbox"
            checked={s.paddleSwap}
            disabled={!s.paddleGpioEnabled}
            onChange={(e) => patch({ paddleSwap: e.currentTarget.checked })}
          />
          SWAP
        </label>
      </div>
      {err && <div style={{ color: 'var(--tx)', fontSize: 11 }}>· {err}</div>}
    </div>
  );
}
