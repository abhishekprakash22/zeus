// SPDX-License-Identifier: GPL-2.0-or-later
//
// "Button & encoder mapping" grid for the Radio Settings Front Panel card.
// Collapsible; while open it polls the mapping snapshot every second so the
// press-to-identify flash tracks live panel presses. A press on a control the
// shipped inventory doesn't know (e.g. the panel's PS button, whose id lies
// outside the Thetis default table) surfaces a "detected" row with an assign
// picker — that is the intended binding flow. MOX and TUNE render locked:
// pinned server-side, no unlock. The VFO knob never appears — it is always
// the VFO.

import { useEffect, useMemo, useState } from 'react';
import type React from 'react';
import {
  useG2PanelMappingStore,
  type G2PanelControl,
  type G2PanelLastInput,
} from '../state/g2panel-mapping-store';

const DEFAULT_SENTINEL = '__default__';

// Responsive multi-column layout: the whole map fits the 1280×800 panel
// without scrolling (~4 columns there; phones degrade to 1-2), so a
// press-to-identify flash can never land off-screen.
const GRID_STYLE: React.CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))',
  gap: '0.3rem',
};
const FLASH_MS = 2500;

function actionLabel(name: string): string {
  // "TogglePureSignal" → "Toggle Pure Signal"
  return name.replace(/([a-z0-9])([A-Z])/g, '$1 $2');
}

function Row({
  kind,
  control,
  override,
  actions,
  flash,
  disabled,
  onAssign,
}: {
  kind: 'button' | 'encoder';
  control: G2PanelControl;
  override: string | undefined;
  actions: string[];
  flash: boolean;
  disabled: boolean;
  onAssign: (id: number, action: string | null) => void;
}) {
  return (
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        gap: '0.15rem',
        padding: '0.25rem 0.35rem',
        borderRadius: 4,
        border: '1px solid var(--bg-3, rgba(128,128,128,0.25))',
        background: flash
          ? 'color-mix(in srgb, var(--accent) 32%, transparent)'
          : override
            ? 'color-mix(in srgb, var(--accent) 8%, transparent)'
            : 'transparent',
        outline: flash ? '2px solid var(--accent)' : 'none',
        transition: 'background 300ms ease-out',
        minWidth: 0,
      }}
      data-testid={`g2map-row-${kind}-${control.id}`}
    >
      <div style={{ display: 'flex', alignItems: 'baseline', gap: '0.35rem', minWidth: 0 }}>
        <span style={{ color: 'var(--fg-3)', fontSize: '0.75em' }}>{control.id}</span>
        <span
          style={{
            fontWeight: override ? 600 : 400,
            fontSize: '0.9em',
            whiteSpace: 'nowrap',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
          }}
        >
          {control.label}
        </span>
      </div>
      {control.pinned ? (
        <span style={{ color: 'var(--fg-3)', fontSize: '0.8em' }}>
          {control.defaultAction ? actionLabel(control.defaultAction) : ''} · pinned
        </span>
      ) : (
        <select
          className="ps-select-mini"
          style={{ width: '100%', minWidth: 0 }}
          value={override ?? DEFAULT_SENTINEL}
          disabled={disabled}
          onChange={(e) =>
            onAssign(control.id, e.target.value === DEFAULT_SENTINEL ? null : e.target.value)}
        >
          <option value={DEFAULT_SENTINEL}>
            {control.defaultAction
              ? `Default (${actionLabel(control.defaultAction)})`
              : 'Unassigned (default)'}
          </option>
          {actions.map((a) => (
            <option key={a} value={a}>{actionLabel(a)}</option>
          ))}
        </select>
      )}
    </div>
  );
}

export function G2PanelMappingGrid() {
  const [open, setOpen] = useState(false);
  const mapping = useG2PanelMappingStore((s) => s.mapping);
  const loaded = useG2PanelMappingStore((s) => s.loaded);
  const inflight = useG2PanelMappingStore((s) => s.inflight);
  const error = useG2PanelMappingStore((s) => s.error);
  const load = useG2PanelMappingStore((s) => s.load);
  const setOverride = useG2PanelMappingStore((s) => s.setOverride);
  const resetAll = useG2PanelMappingStore((s) => s.resetAll);

  // Poll while open so the identify flash tracks live presses.
  useEffect(() => {
    if (!open) return;
    void load();
    const id = window.setInterval(() => void load(), 1000);
    return () => window.clearInterval(id);
  }, [open, load]);

  const last: G2PanelLastInput | null =
    mapping.lastInput && mapping.lastInput.ageMs < FLASH_MS ? mapping.lastInput : null;

  // A live press on an id the inventory doesn't know → detected row.
  const detected = useMemo(() => {
    if (!last) return null;
    const known = last.kind === 'button' ? mapping.buttons : mapping.encoders;
    return known.some((c) => c.id === last.id) ? null : last;
  }, [last, mapping.buttons, mapping.encoders]);

  const overrideCount =
    Object.keys(mapping.buttonOverrides).length + Object.keys(mapping.encoderOverrides).length;

  return (
    <div className="ps-field">
      <div className="ps-name">
        Button &amp; encoder mapping
        <em>
          Reassign any panel button or encoder. Press a control on the physical
          panel and its row flashes here; a control the shipped map doesn't
          know (like the PS button) appears below with an assign picker. MOX
          and TUNE are pinned; the main VFO knob is always the VFO.
        </em>
      </div>

      <button
        type="button"
        className="ps-select-mini"
        onClick={() => setOpen((v) => !v)}
        style={{ cursor: 'pointer' }}
      >
        {open ? 'Hide mapping' : `Edit mapping${overrideCount > 0 ? ` (${overrideCount} custom)` : ''}`}
      </button>

      {open ? (
        <div style={{ marginTop: '0.5rem', display: 'flex', flexDirection: 'column', gap: '0.15rem' }}>
          {error ? (
            <span style={{ color: 'var(--tx, #d33)', fontSize: '0.85em' }}>{error}</span>
          ) : null}
          {!loaded ? (
            <span style={{ color: 'var(--fg-3)' }}>Loading…</span>
          ) : (
            <>
              {detected ? (
                <div
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    gap: '0.5rem',
                    padding: '0.3rem 0.35rem',
                    borderRadius: 4,
                    border: '1px dashed var(--accent)',
                    background: 'color-mix(in srgb, var(--accent) 14%, transparent)',
                  }}
                  data-testid="g2map-detected"
                >
                  <span style={{ fontWeight: 600 }}>
                    Detected: {detected.kind} {detected.id}
                  </span>
                  <span style={{ color: 'var(--fg-3)', fontSize: '0.85em' }}>
                    not in the shipped map — assign it:
                  </span>
                  <select
                    className="ps-select-mini"
                    value={DEFAULT_SENTINEL}
                    disabled={inflight}
                    onChange={(e) => {
                      if (e.target.value !== DEFAULT_SENTINEL)
                        void setOverride(detected.kind, detected.id, e.target.value);
                    }}
                  >
                    <option value={DEFAULT_SENTINEL}>choose action…</option>
                    {(detected.kind === 'button' ? mapping.buttonActions : mapping.encoderActions)
                      .map((a) => (
                        <option key={a} value={a}>{actionLabel(a)}</option>
                      ))}
                  </select>
                </div>
              ) : null}

              <div style={{ color: 'var(--fg-2)', fontSize: '0.85em', margin: '0.3rem 0 0.1rem' }}>
                Buttons
              </div>
              <div style={GRID_STYLE}>
              {mapping.buttons.map((c) => (
                <Row
                  key={`b${c.id}`}
                  kind="button"
                  control={c}
                  override={mapping.buttonOverrides[c.id]}
                  actions={mapping.buttonActions}
                  flash={!!last && last.kind === 'button' && last.id === c.id}
                  disabled={inflight}
                  onAssign={(id, action) => void setOverride('button', id, action)}
                />
              ))}

              {/* Overridden buttons outside the shipped inventory (already
                  bound via a detected row) still need visible, editable rows. */}
              {Object.entries(mapping.buttonOverrides)
                .filter(([id]) => !mapping.buttons.some((c) => c.id === Number(id)))
                .map(([id, action]) => (
                  <Row
                    key={`bx${id}`}
                    kind="button"
                    control={{ id: Number(id), label: `Button ${id}`, defaultAction: null, pinned: false }}
                    override={action}
                    actions={mapping.buttonActions}
                    flash={!!last && last.kind === 'button' && last.id === Number(id)}
                    disabled={inflight}
                    onAssign={(bid, a) => void setOverride('button', bid, a)}
                  />
                ))}
              </div>

              <div style={{ color: 'var(--fg-2)', fontSize: '0.85em', margin: '0.4rem 0 0.1rem' }}>
                Encoders
              </div>
              <div style={GRID_STYLE}>
              {mapping.encoders.map((c) => (
                <Row
                  key={`e${c.id}`}
                  kind="encoder"
                  control={c}
                  override={mapping.encoderOverrides[c.id]}
                  actions={mapping.encoderActions}
                  flash={!!last && last.kind === 'encoder' && last.id === c.id}
                  disabled={inflight}
                  onAssign={(id, action) => void setOverride('encoder', id, action)}
                />
              ))}
              {Object.entries(mapping.encoderOverrides)
                .filter(([id]) => !mapping.encoders.some((c) => c.id === Number(id)))
                .map(([id, action]) => (
                  <Row
                    key={`ex${id}`}
                    kind="encoder"
                    control={{ id: Number(id), label: `Encoder ${id}`, defaultAction: null, pinned: false }}
                    override={action}
                    actions={mapping.encoderActions}
                    flash={!!last && last.kind === 'encoder' && last.id === Number(id)}
                    disabled={inflight}
                    onAssign={(eid, a) => void setOverride('encoder', eid, a)}
                  />
                ))}
              </div>

              <div style={{ marginTop: '0.5rem' }}>
                <button
                  type="button"
                  className="ps-select-mini"
                  disabled={inflight || overrideCount === 0}
                  style={{ cursor: overrideCount > 0 ? 'pointer' : 'default' }}
                  onClick={() => {
                    if (window.confirm('Reset every button and encoder to the shipped defaults?'))
                      void resetAll();
                  }}
                >
                  Reset all to defaults
                </button>
              </div>
            </>
          )}
        </div>
      ) : null}
    </div>
  );
}
