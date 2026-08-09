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

/** Touch-first numeric control: the kiosk has no keyboard, so every value
 * steps with -/+ (the number input stays for desktop typing). */
function Stepper(props: {
  label: string; unit?: string; value: number; min: number; max: number;
  step: number; disabled?: boolean; title?: string;
  onChange: (v: number) => void;
}) {
  const clamp = (v: number) => Math.min(props.max, Math.max(props.min, v));
  return (
    <label style={{ display: 'inline-flex', gap: 4, alignItems: 'center', fontSize: 11, letterSpacing: '.05em' }} title={props.title}>
      {props.label}
      <button
        type="button" className="cwdec-btn cw-step"
        disabled={props.disabled}
        onClick={() => props.onChange(clamp(props.value - props.step))}
      >
        −
      </button>
      <input
        type="number" min={props.min} max={props.max} step={props.step}
        value={props.value} disabled={props.disabled}
        style={{ width: 54, textAlign: 'center' }}
        onChange={(e) => props.onChange(clamp(Number(e.currentTarget.value)))}
      />
      <button
        type="button" className="cwdec-btn cw-step"
        disabled={props.disabled}
        onClick={() => props.onChange(clamp(props.value + props.step))}
      >
        +
      </button>
      {props.unit}
    </label>
  );
}

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
        // A paddle-field save that comes back UNCHANGED means the server
        // ignored an unknown property — the backend predates the GPIO
        // keyer. Say so instead of silently snapping the checkbox back.
        if (
          (p.paddleGpioEnabled !== undefined && saved.paddleGpioEnabled !== p.paddleGpioEnabled) ||
          (p.paddleSwap !== undefined && saved.paddleSwap !== p.paddleSwap)
        ) {
          setErr('the server ignored this setting — the radio is running a build older than the GPIO keyer (update via Settings → Updates)');
          return;
        }
        setErr('');
      })
      .catch((e) => setErr((e as Error)?.message ?? 'save failed'));
  }, []);

  return (
    <div className="cw-settings">
      <div style={head}>KEYER</div>
      <div style={row}>
        <Stepper label="SPEED" unit="WPM" value={s.wpm} min={5} max={50} step={1}
          onChange={(v) => patch({ wpm: v })} />
        <Stepper label="FARNSWORTH" unit="WPM" value={s.farnsworthWpm ?? 0} min={0} max={50} step={1}
          title="Character speed stays at SPEED; spacing stretches to this. 0 disables."
          onChange={(v) => patch({ farnsworthWpm: v || null })} />
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
        <Stepper label="PITCH" unit="Hz" value={s.sidetoneHz} min={200} max={1200} step={10}
          onChange={(v) => patch({ sidetoneHz: v })} />
        <Stepper label="LEVEL" unit="dB" value={Math.round(s.sidetoneGainDb)} min={-60} max={0} step={1}
          onChange={(v) => patch({ sidetoneGainDb: v })} />
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
        <Stepper label="DOT GPIO" value={s.paddleDotPin} min={0} max={27} step={1}
          disabled={!s.paddleGpioEnabled}
          onChange={(v) => patch({ paddleDotPin: v })} />
        <Stepper label="DASH GPIO" value={s.paddleDashPin} min={0} max={27} step={1}
          disabled={!s.paddleGpioEnabled}
          onChange={(v) => patch({ paddleDashPin: v })} />
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
