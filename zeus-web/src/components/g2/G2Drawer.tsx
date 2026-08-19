// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// G2 display layout — the bottom key drawer for the radio's 8-inch
// 1280x800 front glass (Settings → Display → "G2 touch drawer").
//
// Design contract (operator-approved preview, 2026-08-19):
//   108 px drawer of large touch keys + a compact TX strip (FWD/SWR/ALC
//   thin bars with peak hold and live numerics) replacing the desktop
//   transport bar. Graphite chrome, amber accent.
//
// Honesty about reuse: MOX and TUN are the REAL transport buttons
// (<MoxButton/>, <TunButton/>) rendered at touch size via scoped CSS, so
// every gate, confirm, and state path they carry applies unchanged. BAND,
// MODE, and FILTER open bottom sheets hosting the real BandButtons /
// ModeBandwidth / FilterRibbon components — the same controls the desktop
// panels mount, following the focused receiver. The TX strip is three
// MeterRenderer hbar widgets fed by the live meter pipeline. NB·NR opens
// the real DspPanel; ANT opens the radio settings panel (antenna section
// included) — same stores, same semantics as the desktop.
//
// The classic .transport strip is hidden by the scoped stylesheet below
// while the drawer is mounted — App leaves it in the tree so switching the
// layout off restores the desktop chrome with zero remount churn.

import { useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { MoxButton } from '../MoxButton';
import { TunButton } from '../TunButton';
import { TxMonitorButton } from '../TxMonitorButton';
import { PsToggleButton } from '../PsToggleButton';
import { CtunButton } from '../CtunButton';
import { RecorderButton } from '../RecorderButton';
import { DisplayPanel } from '../DisplayPanel';
import { BandButtons } from '../BandButtons';
import { ModeBandwidth } from '../ModeBandwidth';
import { FilterRibbon } from '../filter/FilterRibbon';
import { DspPanel } from '../DspPanel';
import { RadioSettingsPanel } from '../RadioSettingsPanel';
import { MeterRenderer } from '../meter-group/MeterRenderer';
import { MeterReadingId } from '../meters/meterCatalog';

type SheetId = 'band' | 'mode' | 'filter' | 'dsp' | 'ant' | 'setup' | null;

const DRAWER_H = 108;

export function G2Drawer() {
  const [sheet, setSheet] = useState<SheetId>(null);

  const toggleSheet = (id: Exclude<SheetId, null>) =>
    setSheet((cur) => (cur === id ? null : id));

  return (
    <>
      {/* Scoped chrome: hide the desktop transport while the drawer owns the
          bottom edge, and scale the hosted transport buttons to touch size.
          Kept as a stylesheet (not conditional render in App) so toggling the
          layout never remounts the transport's children. */}
      <style>{`
        .g2-layout .transport { display: none !important; }
        /* The drawer is position:fixed — reserve its height so the layout's
           bottom edge (settings/SETUP chip, status corner) stays reachable
           above it instead of being buried under it (field report). */
        .app.g2-layout { padding-bottom: ${DRAWER_H}px; box-sizing: border-box; }
        .g2-drawer .g2-key > * { width: 100%; height: 100%; display: flex; }
        .g2-drawer .g2-key button {
          width: 100%;
          height: 100%;
          min-height: 84px;
          font-size: 13px;
          font-weight: 700;
          letter-spacing: 0.05em;
          border-radius: 9px;
          /* Hosted transport buttons arrive with transparent chrome in this
             context — give them the same face as the sheet keys so the
             drawer reads as one keyboard (field report). */
          border: 1px solid var(--line, #32373f);
          background: var(--bg-2, #24282e);
        }
      `}</style>

      {sheet !== null && (
        <div style={sheetScrim} onClick={() => setSheet(null)}>
          <div style={sheetBody} onClick={(e) => e.stopPropagation()}>
            <div style={sheetHead}>
              <span style={sheetTitle}>
                {sheet === 'band'
                  ? 'BAND'
                  : sheet === 'mode'
                    ? 'MODE'
                    : sheet === 'filter'
                      ? 'FILTER'
                      : sheet === 'dsp'
                        ? 'NB · NR · DSP'
                        : sheet === 'ant'
                          ? 'RADIO'
                          : 'SETUP · DISPLAY'}
              </span>
              <button type="button" style={sheetClose} onClick={() => setSheet(null)}>
                CLOSE
              </button>
            </div>
            <div style={sheetContent}>
              {sheet === 'band' && <BandButtons />}
              {sheet === 'mode' && <ModeBandwidth />}
              {sheet === 'filter' && <FilterRibbon embedded />}
              {sheet === 'dsp' && <DspPanel />}
              {sheet === 'ant' && <RadioSettingsPanel />}
              {sheet === 'setup' && <DisplayPanel />}
            </div>
          </div>
        </div>
      )}

      <div className="g2-drawer" style={drawer}>
        <div className="g2-key" style={key}>
          <MoxButton />
        </div>
        <div className="g2-key" style={key}>
          <TunButton />
        </div>
        <div className="g2-key" style={key}>
          <TxMonitorButton />
        </div>
        <div className="g2-key" style={key}>
          <PsToggleButton />
        </div>
        <div className="g2-key" style={key}>
          <CtunButton />
        </div>
        <SheetKey label="BAND" active={sheet === 'band'} onTap={() => toggleSheet('band')} />
        <SheetKey label="MODE" active={sheet === 'mode'} onTap={() => toggleSheet('mode')} />
        <SheetKey label="FILTER" active={sheet === 'filter'} onTap={() => toggleSheet('filter')} />
        <SheetKey label="NB·NR" active={sheet === 'dsp'} onTap={() => toggleSheet('dsp')} />
        <SheetKey label="RADIO" active={sheet === 'ant'} onTap={() => toggleSheet('ant')} />
        <SheetKey label="SETUP" active={sheet === 'setup'} onTap={() => toggleSheet('setup')} />
        <div className="g2-key" style={key}>
          <RecorderButton />
        </div>
        <div style={txStrip}>
          <TxBar uid="g2-fwd" reading={MeterReadingId.TxFwdWatts} />
          <TxBar uid="g2-swr" reading={MeterReadingId.TxSwr} />
          <TxBar uid="g2-alc" reading={MeterReadingId.TxAlcPk} />
        </div>
      </div>
    </>
  );
}

function SheetKey({
  label,
  active,
  onTap,
}: {
  label: string;
  active: boolean;
  onTap: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onTap}
      style={{
        ...key,
        ...sheetKeyBtn,
        ...(active ? sheetKeyActive : null),
      }}
    >
      {label}
    </button>
  );
}

function TxBar({ uid, reading }: { uid: string; reading: MeterReadingId }): ReactNode {
  return (
    <div style={txRow}>
      <MeterRenderer widget={{ uid, reading, kind: 'hbar' }} />
    </div>
  );
}

const drawer: CSSProperties = {
  position: 'fixed',
  left: 0,
  right: 0,
  bottom: 0,
  height: DRAWER_H,
  display: 'flex',
  gap: 8,
  padding: 10,
  boxSizing: 'border-box',
  background: '#1b1e23',
  borderTop: '1px solid #32373f',
  zIndex: 60,
};

const key: CSSProperties = {
  flex: '1 1 0',
  minWidth: 58,
  display: 'flex',
  alignItems: 'stretch',
};

const sheetKeyBtn: CSSProperties = {
  alignItems: 'center',
  justifyContent: 'center',
  border: '1px solid #32373f',
  borderRadius: 9,
  background: '#24282e',
  color: '#e8ecf1',
  fontSize: 13,
  fontWeight: 700,
  letterSpacing: '0.05em',
  cursor: 'pointer',
};

const sheetKeyActive: CSSProperties = {
  borderColor: 'var(--accent, #4aa3df)',
  color: 'var(--accent, #4aa3df)',
};

const txStrip: CSSProperties = {
  width: 420,
  flex: '0 0 420px',
  display: 'flex',
  flexDirection: 'column',
  gap: 4,
  padding: '4px 8px',
  boxSizing: 'border-box',
  background: '#14171c',
  border: '1px solid #32373f',
  borderRadius: 8,
};

const txRow: CSSProperties = {
  flex: 1,
  minHeight: 0,
  display: 'flex',
};

const sheetScrim: CSSProperties = {
  position: 'fixed',
  inset: 0,
  bottom: DRAWER_H,
  background: 'rgba(4, 5, 7, 0.55)',
  zIndex: 55,
  display: 'flex',
  alignItems: 'flex-end',
};

const sheetBody: CSSProperties = {
  width: '100%',
  maxHeight: '62%',
  display: 'flex',
  flexDirection: 'column',
  background: '#1b1e23',
  borderTop: '1px solid #32373f',
  boxShadow: '0 -12px 40px rgba(0,0,0,0.6)',
};

const sheetHead: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  padding: '10px 14px',
  borderBottom: '1px solid #32373f',
};

const sheetTitle: CSSProperties = {
  fontSize: 12,
  fontWeight: 800,
  letterSpacing: '0.18em',
  color: 'var(--accent, #4aa3df)',
};

const sheetClose: CSSProperties = {
  minWidth: 84,
  minHeight: 44,
  border: '1px solid #32373f',
  borderRadius: 8,
  background: '#24282e',
  color: '#e8ecf1',
  fontSize: 12,
  fontWeight: 700,
  letterSpacing: '0.08em',
  cursor: 'pointer',
};

const sheetContent: CSSProperties = {
  overflowY: 'auto',
  padding: 12,
};
