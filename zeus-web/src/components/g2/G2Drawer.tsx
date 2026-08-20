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

import { useEffect, useRef, useState } from 'react';
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
import { useConnectionStore } from '../../state/connection-store';
import { useTxAudioProfileStore } from '../../state/tx-audio-profile-store';
import { disconnectAll } from '../../util/disconnect-all';
import { setReceiverMuted } from '../../api/client';

type SheetId = 'band' | 'mode' | 'filter' | 'dsp' | 'ant' | 'setup' | 'tx' | null;

// Two-deck drawer (option C, field-picked): a slim tab strip for the seven
// page keys — they OPEN panels, tabs is what they are — over a full-height
// transport row. Hierarchy by size: the row you tap in anger is the big one.
const TABS_H = 32;
const LOWER_H = 66;
const DRAWER_H = TABS_H + LOWER_H; // sheets + layout padding anchor on this


// DISC — the drawer's disconnect key (field request: a disconnect within
// reach, just above the settings keys, without opening the CONTROLS panel).
// Same verb as ConnectPanel's Disconnect (util/disconnect-all.ts). A first
// tap arms SURE? for 3 s so a stray tap on glass can't drop the session;
// when the TX audio profile has unsaved live edits the armed label says so
// (the panel's save-first prompt needs the panel — this key is the quick
// exit and makes the cost visible instead).
// FULL SCR — a rail mirror of the topbar FullscreenButton. The real button
// stays mounted (App keeps the topbar in the DOM under G2), so ALL the
// hard-won machinery — pref + kiosk marker writes, first-gesture restore,
// stale-geometry watchdog — keeps running there; this button is a dumb
// toggle whose label tracks fullscreenchange. No logic duplicated.
function FullscreenSideButton() {
  const [full, setFull] = useState<boolean>(!!document.fullscreenElement);
  useEffect(() => {
    const onChange = () => setFull(!!document.fullscreenElement);
    document.addEventListener('fullscreenchange', onChange);
    return () => document.removeEventListener('fullscreenchange', onChange);
  }, []);
  const toggle = () => {
    if (document.fullscreenElement) void document.exitFullscreen().catch(() => {});
    else void document.documentElement.requestFullscreen().catch(() => {});
  };
  return (
    <button
      type="button"
      className={full ? 'g2-controls-btn g2-fullscr-btn on' : 'g2-controls-btn g2-fullscr-btn'}
      onClick={toggle}
      title={full ? 'Exit full screen (Esc also works)' : 'Full screen — hide the browser chrome'}
    >
      {full ? 'EXIT FS' : 'FULL SCR'}
    </button>
  );
}

function DisconnectSideButton() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const dirty = useTxAudioProfileStore((s) => s.dirty);
  const [armed, setArmed] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);
  if (!connected) return null;
  const onTap = () => {
    if (!armed) {
      setArmed(true);
      timer.current = setTimeout(() => setArmed(false), 3000);
      return;
    }
    if (timer.current) clearTimeout(timer.current);
    setArmed(false);
    void disconnectAll().catch(() => {});
  };
  return (
    <button
      type="button"
      className={armed ? 'g2-controls-btn g2-disc-btn armed' : 'g2-controls-btn g2-disc-btn'}
      onClick={onTap}
      title="Disconnect from the radio"
    >
      {armed ? (dirty ? 'UNSAVED·SURE?' : 'SURE?') : 'DISC'}
    </button>
  );
}

export function G2Drawer() {
  // Audio follows the ACTIVE receiver — hoisted here from G2RxStack (field
  // bug: opening Settings unmounts the panes, whose unmount cleanup unmuted
  // BOTH receivers mid-session). The drawer mounts with the G2 LAYOUT
  // SETTING and stays mounted while Settings is open, so mute state now
  // survives a settings visit; when the layout itself is turned off the
  // drawer unmounts and the cleanup returns both receivers to the desktop's
  // expectations. Best-effort — a failed call leaves the mixer as-is.
  const rx2Enabled = useConnectionStore((s) => s.rx2Enabled);
  const focusedRxIndex = useConnectionStore((s) => s.focusedRxIndex);
  useEffect(() => {
    if (!rx2Enabled) {
      void setReceiverMuted(0, false).catch(() => {});
      return;
    }
    void setReceiverMuted(0, focusedRxIndex !== 0).catch(() => {});
    void setReceiverMuted(1, focusedRxIndex !== 1).catch(() => {});
  }, [focusedRxIndex, rx2Enabled]);
  useEffect(
    () => () => {
      void setReceiverMuted(0, false).catch(() => {});
      void setReceiverMuted(1, false).catch(() => {});
    },
    [],
  );
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
      <DisconnectSideButton />
      <FullscreenSideButton />
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
          top: 224px;
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
        /* Rail order, top to bottom: DISC, FULL SCR, CONTROLS, AUDIO PROC.
           The rail starts at 60px — clear of the workspace docking button
           that owns the top-left corner (field report: DISC overlapped it).
           Armed DISC grows to 84px and stays clear of FULL SCR at 152. */
        .g2-disc-btn { top: 60px; height: 64px; }
        .g2-disc-btn.armed {
          color: var(--tx, #e05656);
          border-color: var(--tx, #e05656);
          height: 84px;
        }
        .g2-fullscr-btn { top: 152px; height: 64px; }
        .g2-audioproc-btn { top: 296px; height: 84px; }
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
          min-height: 48px;
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
        <div style={tabsRow}>
          <SheetTab label="BAND" active={sheet === 'band'} onTap={() => toggleSheet('band')} />
          <SheetTab label="MODE" active={sheet === 'mode'} onTap={() => toggleSheet('mode')} />
          <SheetTab label="FILTER" active={sheet === 'filter'} onTap={() => toggleSheet('filter')} />
          <SheetTab label="NB·NR" active={sheet === 'dsp'} onTap={() => toggleSheet('dsp')} />
          <SheetTab label="RADIO" active={sheet === 'ant'} onTap={() => toggleSheet('ant')} />
          <SheetTab label="DISPLAY" active={sheet === 'setup'} onTap={() => toggleSheet('setup')} />
          <SheetTab label="TX" active={sheet === 'tx'} onTap={() => toggleSheet('tx')} />
        </div>
        <div style={lowerRow}>
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
        <span style={rowDivider} aria-hidden />
        <div className="g2-key" style={key}>
          <RecorderButton />
        </div>
        <div style={txStrip}>
          <TxBar uid="g2-fwd" reading={MeterReadingId.TxFwdWatts} />
          <TxBar uid="g2-swr" reading={MeterReadingId.TxSwr} />
          <TxBar uid="g2-alc" reading={MeterReadingId.TxAlcPk} />
        </div>
        </div>
      </div>
    </>
  );
}

function SheetTab({
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
      style={{ ...tabBtn, ...(active ? tabActive : null) }}
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
  flexDirection: 'column',
  boxSizing: 'border-box',
  background: 'linear-gradient(180deg, #161c26, #10151d)',
  borderTop: '1px solid #3d4552',
  zIndex: 60,
};

const tabsRow: CSSProperties = {
  height: TABS_H,
  flex: `0 0 ${TABS_H}px`,
  display: 'flex',
  alignItems: 'stretch',
  padding: '0 10px',
  borderBottom: '1px solid #202834',
};

const tabBtn: CSSProperties = {
  // Distribute the seven tabs across the strip so they sit over the
  // transport keys instead of huddling left (field report).
  flex: '1 1 0',
  minWidth: 0,
  textAlign: 'center',
  padding: '0 8px',
  border: 'none',
  background: 'transparent',
  color: 'var(--fg-2, #8b95a3)',
  fontSize: 11,
  fontWeight: 800,
  letterSpacing: '0.08em',
  cursor: 'pointer',
};

const tabActive: CSSProperties = {
  color: 'var(--accent, #4aa3df)',
  boxShadow: 'inset 0 -2px 0 var(--accent, #4aa3df)',
  background: 'linear-gradient(180deg, transparent 55%, rgba(74,163,223,0.10))',
};

const lowerRow: CSSProperties = {
  flex: 1,
  display: 'flex',
  alignItems: 'stretch',
  gap: 8,
  padding: 8,
  boxSizing: 'border-box',
  minHeight: 0,
};

const rowDivider: CSSProperties = {
  width: 1,
  margin: '6px 2px',
  background: '#2a3341',
  flex: '0 0 1px',
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

const txStrip: CSSProperties = {
  // Three meters SIDE BY SIDE: the two-deck row leaves ~50px of height,
  // which three stacked hbars cannot share (field photo: scales collapsed
  // into each other). Each meter now gets the full row height instead.
  width: 460,
  flex: '0 0 460px',
  display: 'flex',
  flexDirection: 'row',
  gap: 8,
  padding: '2px 8px',
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
