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
import { SplitButton, RitButton } from '../RitSplitButtons';
import { DiversityToggleButton } from '../DiversityWindow';
import { CwDecodeToggleButton } from '../CwDecodeWindow';
import { TxPanel } from '../../layout/panels/TxPanel';
import { TxFidelityPanel } from '../../layout/panels/TxFidelityPanel';
import { TxMetersPanel } from '../../layout/panels/TxMetersPanel';
import { BandButtons } from '../BandButtons';
import { ModeBandwidth } from '../ModeBandwidth';
import { FilterRibbon } from '../filter/FilterRibbon';
import { DspPanel } from '../DspPanel';
import { RadioSettingsPanel } from '../RadioSettingsPanel';
import { MeterRenderer } from '../meter-group/MeterRenderer';
import { MeterReadingId } from '../meters/meterCatalog';

type SheetId = 'band' | 'mode' | 'filter' | 'dsp' | 'ant' | 'setup' | 'tx' | null;

const DRAWER_H = 108;

export function G2Drawer() {
  const [sheet, setSheet] = useState<SheetId>(null);
  // Sheets dismiss themselves after a selection (touch economy) unless pinned.
  const [pinned, setPinned] = useState(false);
  const [night, setNight] = useState(false);
  const onSheetSelection = (e: { target: EventTarget }) => {
    if (pinned) return;
    // Only the pick-one sheets self-dismiss; DSP/RADIO/SETUP are panels the
    // operator works inside, not menus.
    if (sheet !== 'band' && sheet !== 'mode' && sheet !== 'filter') return;
    const el = e.target as HTMLElement;
    // A button tap inside the sheet content = a selection made; give the click
    // a beat to land, then close. Sliders and inputs don't dismiss.
    if (el.closest('button')) {
      window.setTimeout(() => setSheet(null), 180);
    }
  };
  const [controlsOpen, setControlsOpen] = useState(false);
  const [audioProcOpen, setAudioProcOpen] = useState(false);
  const toggleControls = () => {
    setControlsOpen((v) => {
      document.body.classList.toggle('g2-controls-open', !v);
      return !v;
    });
  };
  const toggleNight = () => {
    setNight((n) => {
      document.body.classList.toggle('g2-night', !n);
      return !n;
    });
  };

  const toggleSheet = (id: Exclude<SheetId, null>) =>
    setSheet((cur) => (cur === id ? null : id));

  return (
    <>
      {/* Scoped chrome: hide the desktop transport while the drawer owns the
          bottom edge, and scale the hosted transport buttons to touch size.
          Kept as a stylesheet (not conditional render in App) so toggling the
          layout never remounts the transport's children. */}
      {controlsOpen ? (
        <div className="g2-controls-extras">
          <span className="g2-extras-label">SPLIT · RIT · DIV · CW · NIGHT</span>
          <SplitButton />
          <RitButton />
          <DiversityToggleButton />
          <CwDecodeToggleButton />
          <button
            type="button"
            style={{ ...sheetKeyBtnFlat, ...(night ? { color: 'var(--accent, #4aa3df)', borderColor: 'var(--accent, #4aa3df)' } : null) }}
            onClick={toggleNight}
          >
            NIGHT
          </button>
        </div>
      ) : null}
      <button
        type="button"
        className={controlsOpen ? 'g2-controls-btn on' : 'g2-controls-btn'}
        onClick={toggleControls}
        title="radio controls (step, front-end, AGC, SQL, AF...)"
      >
        CONTROLS
      </button>
      <button
        type="button"
        className={audioProcOpen ? 'g2-controls-btn g2-audioproc-btn on' : 'g2-controls-btn g2-audioproc-btn'}
        onClick={() => setAudioProcOpen((v) => !v)}
        title="TX audio processing (CFC, EQ, leveler) and stage meters"
      >
        AUDIO PROC
      </button>
      {audioProcOpen ? (
        <div className="g2-audioproc-panel">
          <TxMetersPanel />
          <TxFidelityPanel />
        </div>
      ) : null}
      <style>{`
        .g2-layout .transport { display: none !important; }
        /* G2 top-bar diet: the dense desktop control cluster (STEP / FRONT-END /
           AGC / SQL / AF ...) is mouse chrome — its daily-use controls live on
           the flags (AF, AGC-T, STEP) and in the drawer sheets. The brand,
           status, and Disconnect stay. */
        .g2-layout .topbar-controls-shell { display: none !important; }
        /* CONTROLS side panel (field request): the REAL desktop control
           cluster — STEP, FRONT-END/S-ATT, AGC, SQL, DYN, AF, ROGER, VIEW —
           re-homed as a left panel the side button toggles. Same DOM, same
           handlers; only the dress changes, so nothing can drift out of
           sync with the desktop. */
        .g2-controls-extras {
          position: fixed;
          left: 58px;
          bottom: 128px;
          z-index: 461;
          display: flex;
          align-items: center;
          gap: 8px;
          padding: 8px 10px;
          border-radius: 10px;
          border: 1px solid var(--line, #32373f);
          background: rgba(13, 17, 24, 0.97);
          box-shadow: 0 12px 40px rgba(0,0,0,0.6);
        }
        .g2-controls-extras button { min-height: 40px; min-width: 52px; }
        /* The transport buttons' engaged dress is scoped to the transport bar
           on some themes — restate it here so SPLIT/RIT/DIV/CW visibly light
           up in the extras row (field: 'pressing split does nothing' — it
           worked, it just had no visible answer). */
        .g2-controls-extras button.engaged {
          color: var(--accent, #4aa3df) !important;
          border-color: var(--accent, #4aa3df) !important;
        }
        .g2-extras-label {
          position: absolute;
          top: -14px;
          left: 8px;
          font-size: 8px;
          font-weight: 800;
          letter-spacing: 0.1em;
          color: var(--fg-3, #6a727d);
        }
        body.g2-controls-open .app.g2-layout .topbar-controls-shell {
          display: flex !important;
          position: fixed;
          left: 58px;
          top: 64px;
          z-index: 460;
          width: 420px;
          max-width: calc(100vw - 120px);
          max-height: calc(100vh - 280px);
          overflow: auto;
          padding: 10px;
          border-radius: 10px;
          border: 1px solid var(--line, #32373f);
          background: rgba(13, 17, 24, 0.97);
          box-shadow: 0 12px 40px rgba(0,0,0,0.6);
        }
        body.g2-controls-open .app.g2-layout .topbar-controls {
          flex-wrap: wrap;
          row-gap: 12px;
          overflow: visible;
        }
        /* Full-height glass (field request): the header row is retired from
           the flow entirely — the panes take its space. Its surviving
           tenants (brand, Disconnect) move INTO the CONTROLS panel; the
           re-dressed controls shell needs the header alive in the DOM, so
           the header collapses (fixed, zero-height, overflow visible)
           rather than display:none. !important beats the inline
           position/zIndex the header carries. */
        .app.g2-layout .topbar {
          position: fixed !important;
          top: 0; left: 0;
          height: 0 !important;
          min-height: 0 !important;
          padding: 0 !important;
          border: 0 !important;
          background: transparent !important;
          overflow: visible !important;
          z-index: 455 !important;
        }
        .app.g2-layout .topbar > .brand,
        .app.g2-layout .topbar > .topbar-spacer,
        .app.g2-layout .topbar > .topbar-divider,
        .app.g2-layout .topbar > .topbar-connect { display: none !important; }
        body.g2-controls-open .app.g2-layout .topbar > .brand {
          display: flex !important;
          position: fixed;
          left: 58px;
          top: 14px;
          z-index: 461;
          padding: 6px 10px;
          border-radius: 8px;
          border: 1px solid var(--line, #32373f);
          background: rgba(13, 17, 24, 0.97);
        }
        body.g2-controls-open .app.g2-layout .topbar > .topbar-connect {
          display: flex !important;
          position: fixed;
          left: 300px;
          top: 14px;
          z-index: 461;
          padding: 4px 8px;
          border-radius: 8px;
          border: 1px solid var(--line, #32373f);
          background: rgba(13, 17, 24, 0.97);
        }
        .g2-controls-btn {
          position: fixed;
          left: 10px;
          top: 128px;
          z-index: 461;
          width: 40px;
          height: 64px;
          border-radius: 8px;
          border: 1px solid var(--line, #32373f);
          background: var(--bg-2, #1c2129);
          color: var(--fg-1, #c3cad3);
          font-size: 9px;
          font-weight: 800;
          letter-spacing: 0.08em;
          writing-mode: vertical-rl;
          text-orientation: mixed;
          cursor: pointer;
        }
        .g2-controls-btn.on { color: var(--accent, #4aa3df); border-color: var(--accent, #4aa3df); }
        .g2-audioproc-btn { top: 200px; height: 84px; }
        .g2-audioproc-panel {
          position: fixed;
          left: 58px;
          top: 40px;
          z-index: 460;
          width: 460px;
          max-width: calc(100vw - 120px);
          max-height: calc(100vh - 170px);
          overflow: auto;
          display: flex;
          flex-direction: column;
          gap: 10px;
          padding: 10px;
          border-radius: 10px;
          border: 1px solid var(--line, #32373f);
          background: rgba(13, 17, 24, 0.97);
          box-shadow: 0 12px 40px rgba(0,0,0,0.6);
        }
        /* Night mode: one key dims the whole glass for the dark shack. */
        body.g2-night .app.g2-layout { filter: brightness(0.55); }
        /* The drawer is position:fixed — reserve its height so the layout's
           bottom edge (settings/SETUP chip, status corner) stays reachable
           above it instead of being buried under it (field report). */
        .app.g2-layout { padding-bottom: ${DRAWER_H}px; box-sizing: border-box; }
        .g2-drawer .g2-key { min-width: 64px; overflow: hidden; }
        .g2-drawer .g2-key > * { width: 100%; height: 100%; display: flex; min-width: 0; }
        /* One keyboard, one face: hosted transport buttons arrive with their
           own widths/padding/fonts — normalize the box so nothing overlaps
           its neighbor; engaged colors still come from their own classes. */
        .g2-drawer .g2-key button {
          width: 100% !important;
          min-width: 0 !important;
          max-width: none !important;
          margin: 0 !important;
          padding: 2px 4px !important;
          box-sizing: border-box !important;
          white-space: nowrap;
          overflow: hidden;
          text-overflow: ellipsis;
        }
        .g2-drawer .g2-key button {
          height: 100%;
          min-height: 84px;
          font-size: 12px;
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
                          : sheet === 'setup'
                            ? 'DISPLAY'
                            : 'TX · DRIVE / MIC'}
              </span>
              <button
                type="button"
                style={{ ...sheetClose, ...(pinned ? { color: 'var(--accent, #4aa3df)' } : null) }}
                onClick={() => setPinned((v) => !v)}
              >
                {pinned ? 'PINNED' : 'PIN'}
              </button>
              <button type="button" style={sheetClose} onClick={() => setSheet(null)}>
                CLOSE
              </button>
            </div>
            <div style={sheetContent} onClickCapture={onSheetSelection}>
              {sheet === 'band' && <BandButtons />}
              {sheet === 'mode' && <ModeBandwidth />}
              {sheet === 'filter' && <FilterRibbon embedded />}
              {sheet === 'dsp' && <DspPanel />}
              {sheet === 'ant' && <RadioSettingsPanel />}
              {sheet === 'setup' && <DisplayPanel />}
              {sheet === 'tx' && <TxPanel />}
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
        <SheetKey label="DISPLAY" active={sheet === 'setup'} onTap={() => toggleSheet('setup')} />
        <SheetKey label="TX" active={sheet === 'tx'} onTap={() => toggleSheet('tx')} />
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

const sheetKeyBtnFlat: CSSProperties = {
  padding: '0 10px',
  borderRadius: 7,
  border: '1px solid var(--line, #32373f)',
  background: 'var(--bg-2, #1c2129)',
  color: 'var(--fg-1, #c3cad3)',
  fontSize: 11,
  fontWeight: 800,
  letterSpacing: '0.06em',
  cursor: 'pointer',
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
