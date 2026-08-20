// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// G2 display layout — the stacked receiver panes (commit 3 of the
// operator-approved series). RX1 over RX2, piHPSDR-style: each pane is
// the REAL Panadapter over the REAL Waterfall, bound to its receiver
// ('A' / 'B') — both components were per-receiver by design, and the
// display pipeline keeps a slice per receiver (selectDisplaySlice), so
// no rendering fork is needed. Each pane carries an RX flag (frequency,
// mode) on its top edge; NO "slice" language anywhere — RX1/RX2 only.
//
// Activation contract (approved frame): tap anywhere on an INACTIVE
// pane to make its receiver the focused one — the capture-phase handler
// swallows that first tap so it never tunes; taps on the active pane
// pass through to the panadapter's own tune gestures. First tap
// selects, second tap acts.
//
// The split is a persisted-in-session drag divider (default 55/45).

import { useCallback, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import type { CSSProperties, PointerEvent as ReactPointerEvent } from 'react';
import { Panadapter } from '../Panadapter';
import { Waterfall } from '../Waterfall';
import { VfoDisplay } from '../VfoDisplay';
import { ZoomControl } from '../ZoomControl';
import { AnalogMeterPanel } from '../analog-meter/AnalogMeterPanel';
import { FilterMiniPan } from '../filter/FilterMiniPan';
import { useConnectionStore } from '../../state/connection-store';
import { useRxMetersStore } from '../../state/rx-meters-store';
import { useDisplayStore, selectDisplaySlice } from '../../state/display-store';
import {
  getReceiverVfoHz,
  getReceiverMode,
  getReceiverFilterLowHz,
  getReceiverFilterHighHz,
  rxIndexOf,
  type ReceiverKey,
} from '../../state/receiver-state';
import { setReceiverMuted, setReceiver, setAgcTop } from '../../api/client';
import { useToolbarFavoritesStore } from '../../state/toolbar-favorites-store';

const RATIO_H = 10;

/** dBm estimate for the FOCUSED receiver from its display slice — the
 *  analog meter's G2 signal source, so the needle follows the active pane.
 *  Called from the meter's rAF loop; getState() keeps it subscription-free. */
function sampleFocusedRxDbm(): number | null {
  const conn = useConnectionStore.getState();
  // RX1 focused: return null so the meter uses its real calibrated stream.
  if (conn.focusedRxIndex !== 1) return null;
  const vfo = getReceiverVfoHz(conn, 'B');
  return sliceTunePeakDbm(useDisplayStore.getState(), 'B', vfo);
}

/** Peak calibrated dBm within ±3 kHz of the tune line in a receiver's
 *  display slice — the display-derived stand-in for a real S reading on
 *  receivers the meter stream doesn't cover. */
function sliceTunePeakDbm(
  st: ReturnType<typeof useDisplayStore.getState>,
  receiver: ReceiverKey,
  vfoHz: number,
): number | null {
  const slice = selectDisplaySlice(st, receiver);
  const pan = slice.panDb;
  if (!pan || pan.length < 8 || !(slice.hzPerPixel > 0)) return null;
  const center = Number(slice.centerHz);
  const mid = pan.length / 2 + (vfoHz - center) / slice.hzPerPixel;
  const half = Math.max(2, Math.round(3000 / slice.hzPerPixel));
  const a = Math.max(0, Math.floor(mid - half));
  const b = Math.min(pan.length, Math.ceil(mid + half));
  if (b - a < 1) return null;
  let peak = -Infinity;
  for (let i = a; i < b; i++) peak = Math.max(peak, pan[i] ?? -Infinity);
  return Number.isFinite(peak) ? peak : null;
}

/** S-bar tick positions on the -127..-33 dBm span. */
const S_TICKS = [
  { label: 'S1', dbm: -121 },
  { label: '3', dbm: -109 },
  { label: '5', dbm: -97 },
  { label: '7', dbm: -85 },
  { label: '9', dbm: -73 },
  { label: '+20', dbm: -53 },
  { label: '+40', dbm: -33 },
].map((t) => ({ ...t, pct: ((t.dbm + 127) / 94) * 100 }));

/** "S7", "S9+10" — the readout beside the mini bar. */
function dbmToSText(dbm: number | null): string {
  if (dbm == null || !Number.isFinite(dbm)) return '—';
  if (dbm >= -73) {
    const over = Math.round((dbm + 73) / 5) * 5;
    return over > 0 ? `S9+${over}` : 'S9';
  }
  return `S${Math.max(0, Math.min(9, Math.round((dbm + 127) / 6)))}`;
}

/** "2.7 kHz" filter-width chip. */
function formatWidth(hz: number): string {
  if (!Number.isFinite(hz) || hz <= 0) return '';
  return hz >= 1000 ? `${(hz / 1000).toFixed(1)} kHz` : `${Math.round(hz)} Hz`;
}

/** Map a display-estimated dBm onto the mini S-bar (S0 -127 dBm .. S9+40 -33 dBm). */
function dbmToPct(dbm: number | null): number {
  if (dbm == null || !Number.isFinite(dbm)) return 0;
  return Math.min(100, Math.max(0, ((dbm + 127) / 94) * 100));
}

export function G2RxStack() {
  // RX2 present only when enabled — a dead receiver earns no glass (field
  // request). With both up the split is a fixed 50/50; the adjustable drag
  // lives INSIDE each pane (spectrum/waterfall ratio), not between panes.
  const rx2Enabled = useConnectionStore((s) => s.rx2Enabled);
  // Removable instrument cards (field request): ✕ hides a card; a restore
  // pill row appears top-right while anything is hidden. Session-local.
  const [hiddenCards, setHiddenCards] = useState<string[]>(() => {
    try {
      const raw = localStorage.getItem('zeus.g2.hiddenCards');
      const v = raw ? JSON.parse(raw) : [];
      return Array.isArray(v) ? v.filter((x) => typeof x === 'string') : [];
    } catch {
      return [];
    }
  });
  const persistHidden = (h: string[]) => {
    try {
      localStorage.setItem('zeus.g2.hiddenCards', JSON.stringify(h));
    } catch {
      /* best-effort */
    }
    return h;
  };
  const hideCard = (id: string) =>
    setHiddenCards((h) => (h.includes(id) ? h : persistHidden([...h, id])));
  const showCard = (id: string) => setHiddenCards((h) => persistHidden(h.filter((x) => x !== id)));

  // Audio-follow moved to G2Drawer (field bug: Settings unmounts this
  // stack, and an unmount cleanup here unmuted both receivers mid-session;
  // the drawer tracks the LAYOUT SETTING, not the view).

  return (
    <div style={stack}>
      <RxPane receiver="A" heightPct={rx2Enabled ? 50 : 100} />
      {rx2Enabled ? <RxPane receiver="B" heightPct={50} /> : null}
      {/* Instrument cards (commit 4 of the approved frame): the analog
          S-meter — face untouched, the Zeus signature — and the bandwidth
          filter display (FilterMiniPan; it splits per receiver internally
          when RX2 is enabled). Cards float over the stack's top-right so
          they cost no pane height; the panadapters keep full width. */}
      {!hiddenCards.includes('smeter') ? (
        <G2Card
          title="S-METER"
          storageId="smeter"
          initial={{ x: -312, y: 8, w: 300, h: 170 }}
          onClose={() => hideCard('smeter')}
        >
          <AnalogMeterPanel sampleDbmOverride={sampleFocusedRxDbm} />
        </G2Card>
      ) : null}
      {!hiddenCards.includes('filter-a') ? (
        <G2Card
          title="FILTER · RX1"
          storageId="filter-a"
          initial={{ x: -312, y: 186, w: 300, h: 150 }}
          onClose={() => hideCard('filter-a')}
        >
          <FilterMiniPan receiver="A" />
        </G2Card>
      ) : null}
      {rx2Enabled && !hiddenCards.includes('filter-b') ? (
        <G2Card
          title="FILTER · RX2"
          storageId="filter-b"
          initial={{ x: -312, y: 372, w: 300, h: 150 }}
          onClose={() => hideCard('filter-b')}
        >
          <FilterMiniPan receiver="B" />
        </G2Card>
      ) : null}
      {hiddenCards.length > 0 ? (
        <div style={restoreRow}>
          {hiddenCards.map((id) => (
            <button key={id} type="button" style={restorePill} onClick={() => showCard(id)}>
              + {id === 'smeter' ? 'S-METER' : id === 'filter-a' ? 'FILTER RX1' : 'FILTER RX2'}
            </button>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function RxPane({ receiver, heightPct }: { receiver: ReceiverKey; heightPct: number }) {
  const rxIndex = rxIndexOf(receiver);
  const active = useConnectionStore((s) => s.focusedRxIndex === rxIndex);
  const setFocusedRxIndex = useConnectionStore((s) => s.setFocusedRxIndex);
  const vfoHz = useConnectionStore((s) => getReceiverVfoHz(s, receiver));
  const mode = useConnectionStore((s) => getReceiverMode(s, receiver));
  const widthHz = useConnectionStore(
    (s) => getReceiverFilterHighHz(s, receiver) - getReceiverFilterLowHz(s, receiver),
  );
  const applyState = useConnectionStore((s) => s.applyState);
  const muted = useConnectionStore((s) => s.receivers[rxIndex]?.muted ?? false);
  const afGainDb = useConnectionStore((s) => s.receivers[rxIndex]?.afGainDb ?? 0);
  const agcTopDb = useConnectionStore((s) => s.agcTopDb);
  const splitEnabled = useConnectionStore((s) => s.splitEnabled);
  const stepHz = useToolbarFavoritesStore((s) => s.stepHz);
  const setStepHz = useToolbarFavoritesStore((s) => s.setStepHz);
  const [popoverOpen, setPopoverOpen] = useState(false);
  const cycleStep = () => {
    const steps = [10, 100, 500, 1000, 5000, 10000];
    const i = steps.indexOf(stepHz);
    setStepHz(steps[(i + 1) % steps.length] ?? 100);
  };
  // Flag S-bar signal. RX1 gets the REAL calibrated meter-stream peak (the
  // same source the desktop S-meter trusts). RX2 has no meter stream, so it
  // gets the display slice's PEAK around its own tune line — a mean over the
  // span just reads the noise floor (field-falsified), the passband peak is
  // what an S-meter means.
  const realPk = useRxMetersStore((s) => s.signalPk);
  const estDbm = useDisplayStore((st) =>
    rxIndex === 0 ? null : sliceTunePeakDbm(st, receiver, vfoHz),
  );
  const barDbm = rxIndex === 0 ? (Number.isFinite(realPk) ? realPk : null) : estDbm;
  // Peak hold for the flag S-bar: hold the max 1.5 s, then let it fall.
  const peakRef = useRef<{ dbm: number; at: number }>({ dbm: -Infinity, at: 0 });
  if (barDbm != null && Number.isFinite(barDbm)) {
    const now = performance.now();
    const pk = peakRef.current;
    if (barDbm >= pk.dbm || now - pk.at > 1500) peakRef.current = { dbm: barDbm, at: now };
  }
  const peakDbm = Number.isFinite(peakRef.current.dbm) ? peakRef.current.dbm : null;
  // Spectrum share of the pane (the requested per-receiver drag): 0.2-0.7.
  const [specFrac, setSpecFrac] = useState(0.44);
  // Per-pane waterfall speed multiplier (field request): ×½ ×1 ×2 ×4 cycle.
  const [speedFactor, setSpeedFactor] = useState(1);
  const cycleSpeed = () =>
    setSpeedFactor((f) => (f === 0.5 ? 1 : f === 1 ? 2 : f === 2 ? 4 : 0.5));
  const paneRef = useRef<HTMLDivElement | null>(null);
  const ratioDrag = useRef(false);
  const onRatioDown = useCallback((e: ReactPointerEvent) => {
    ratioDrag.current = true;
    (e.target as HTMLElement).setPointerCapture(e.pointerId);
    e.stopPropagation();
  }, []);
  const onRatioMove = useCallback((e: ReactPointerEvent) => {
    if (!ratioDrag.current || !paneRef.current) return;
    const r = paneRef.current.getBoundingClientRect();
    setSpecFrac(Math.min(0.7, Math.max(0.2, (e.clientY - r.top) / Math.max(1, r.height))));
  }, []);
  const onRatioUp = useCallback(() => {
    ratioDrag.current = false;
  }, []);

  // First tap on an inactive pane activates its receiver and is swallowed
  // (capture phase) so the panadapter never sees it as a tune. The active
  // pane's taps pass through untouched.
  const onCapturePointerDown = (e: ReactPointerEvent) => {
    // Taps born on the flag are the flag's business — it activates the
    // receiver itself, so its controls work in ONE tap even on the
    // inactive pane (field report: popover unreachable on RX2).
    if ((e.target as HTMLElement).closest?.('[data-g2-flag]')) return;
    if (active) return;
    e.preventDefault();
    e.stopPropagation();
    setFocusedRxIndex(rxIndex);
  };

  return (
    <div
      ref={paneRef}
      style={{ ...pane, height: `${heightPct}%` }}
      onPointerDownCapture={onCapturePointerDown}
    >
      <div style={{ ...paneSpectrum, flex: `0 0 calc(${(specFrac * 100).toFixed(1)}% - ${RATIO_H / 2}px)` }}>
        <Panadapter receiver={receiver} multiRx={false} />
      </div>
      {/* ZOOM lives on each pane, and each pane zooms its OWN receiver —
          the backend applies zoom per DSP channel. */}
      <div style={zoomDock}>
        <ZoomControl receiver={rxIndex === 1 ? 'B' : 'A'} />
        <button
          type="button"
          style={speedPill}
          onPointerDown={(e) => e.stopPropagation()}
          onClick={cycleSpeed}
          title="waterfall speed for this receiver (tap to cycle)"
        >
          SPD ×{speedFactor === 0.5 ? '½' : speedFactor}
        </button>
      </div>
      <div
        style={ratioBar}
        onPointerDown={onRatioDown}
        onPointerMove={onRatioMove}
        onPointerUp={onRatioUp}
        onPointerCancel={onRatioUp}
      >
        <span style={dividerGrip} />
      </div>
      <div style={paneWaterfall}>
        <Waterfall receiver={receiver} speedFactor={speedFactor} />
      </div>
      <div
        data-g2-flag
        style={{
          ...flag,
          ...(active ? flagActive : null),
          pointerEvents: 'auto',
          // While the popover is open the flag must sit above every card and
          // dock. The frequency itself is now the real VfoDisplay (inline
          // edit + per-digit wheel) — nothing left to bury under cards, which
          // retired the keypad and its whole z-order fight (field reports ×2).
          zIndex: popoverOpen ? 60 : 6,
        }}
        onPointerDownCapture={(e) => {
          // Flag taps are flag business — they must not tune the pane
          // underneath; they DO activate the receiver so one tap works.
          e.stopPropagation();
          if (!active) setFocusedRxIndex(rxIndex);
        }}
      >
        <div style={flagRow}>
          <span style={{ ...flagBadge, ...(active ? flagBadgeActive : null) }}>
            RX{rxIndex + 1}
          </span>
          {active ? <span style={flagActiveTag}>ACTIVE</span> : null}
          {rxIndex === 0 && splitEnabled ? (
            <span style={{ ...flagActiveTag, color: 'var(--tx, #e05656)' }}>SPLIT▸B</span>
          ) : null}
        </div>
        {/* The original VFO readout, compact: hover a digit and scroll to
            step that decade; click the digits to type a frequency inline.
            Receiver-aware — posts this pane's VFO. */}
        <div style={flagVfo}>
          <VfoDisplay compact rxIndex={rxIndex} />
        </div>
        <div style={flagRow}>
          <span style={flagMode}>{mode ?? ''}</span>
          <span style={flagChip}>{formatWidth(widthHz)}</span>
          <span
            style={{ ...flagChip, cursor: 'pointer', borderColor: 'var(--accent, #4aa3df)', color: 'var(--accent, #4aa3df)' }}
            onClick={() => setPopoverOpen((o) => !o)}
            title="AF · AGC-T · mute for this receiver"
          >
            CTRL
          </span>
          <span
            style={{ ...flagChip, cursor: 'pointer' }}
            onClick={cycleStep}
            title="tune step — tap to cycle"
          >
            STEP {stepHz >= 1000 ? `${stepHz / 1000}k` : stepHz}
          </span>
        </div>
        <div style={sBarRow}>
          <div style={sBarShell}>
            <div style={{ ...sBarFill, width: `${dbmToPct(barDbm)}%` }} />
            {S_TICKS.map((t) => (
              <span key={t.label} style={{ ...sTick, left: `${t.pct}%` }} />
            ))}
            {peakDbm != null ? (
              <span style={{ ...sPeak, left: `${dbmToPct(peakDbm)}%` }} />
            ) : null}
          </div>
          <span style={sReadout}>{dbmToSText(barDbm)}</span>
        </div>
        <div style={sTickLabels}>
          {S_TICKS.map((t) => (
            <span key={t.label} style={{ ...sTickLabel, left: `${t.pct}%` }}>
              {t.label}
            </span>
          ))}
        </div>
        {popoverOpen ? (
          <div style={popover}>
            <label style={popRow}>
              <span style={popLabel}>AF</span>
              <input
                type="range"
                min={-30}
                max={12}
                step={1}
                value={afGainDb}
                style={{ flex: 1, minWidth: 0, width: '100%' }}
                onChange={(e) => {
                  const db = Number(e.target.value);
                  void setReceiver(rxIndex, { afGainDb: db }).then(applyState).catch(() => {});
                }}
              />
              <span style={popVal}>{afGainDb} dB</span>
            </label>
            <label style={popRow}>
              <span style={popLabel}>AGC-T</span>
              <input
                type="range"
                min={20}
                max={120}
                step={1}
                value={agcTopDb}
                style={{ flex: 1, minWidth: 0, width: '100%' }}
                onChange={(e) => {
                  const db = Number(e.target.value);
                  void setAgcTop(db).then(applyState).catch(() => {});
                }}
              />
              <span style={popVal}>{agcTopDb}</span>
            </label>
            <button
              type="button"
              style={{ ...popBtn, background: muted ? 'var(--accent, #4aa3df)' : undefined }}
              onClick={() =>
                void setReceiverMuted(rxIndex, !muted).then(applyState).catch(() => {})
              }
            >
              {muted ? 'UNMUTE' : 'MUTE'}
            </button>
          </div>
        ) : null}
      </div>
    </div>
  );
}

/** Draggable + resizable floating card. x negative = anchored from the
 *  right edge (so defaults hug top-right like the approved frame). Drag by
 *  the title strip, resize by the corner handle; session-only positions. */
const CARD_STORE = 'zeus.g2.cards';

function readCardBox(id: string): { x: number; y: number; w: number; h: number } | null {
  try {
    const raw = localStorage.getItem(CARD_STORE);
    if (!raw) return null;
    const all = JSON.parse(raw);
    const b = all?.[id];
    return b && [b.x, b.y, b.w, b.h].every((n: unknown) => Number.isFinite(n)) ? b : null;
  } catch {
    return null;
  }
}

function writeCardBox(id: string, box: { x: number; y: number; w: number; h: number }): void {
  try {
    const raw = localStorage.getItem(CARD_STORE);
    const all = raw ? JSON.parse(raw) : {};
    all[id] = box;
    localStorage.setItem(CARD_STORE, JSON.stringify(all));
  } catch {
    // best-effort
  }
}

function G2Card({
  title,
  storageId,
  initial,
  onClose,
  children,
}: {
  title: string;
  storageId: string;
  initial: { x: number; y: number; w: number; h: number };
  onClose?: () => void;
  children: ReactNode;
}) {
  const [box, setBox] = useState(() => readCardBox(storageId) ?? initial);
  const mode = useRef<null | 'move' | 'size'>(null);
  const start = useRef({ px: 0, py: 0, x: 0, y: 0, w: 0, h: 0 });

  const begin = (m: 'move' | 'size') => (e: ReactPointerEvent) => {
    mode.current = m;
    start.current = { px: e.clientX, py: e.clientY, ...box };
    (e.target as HTMLElement).setPointerCapture(e.pointerId);
    e.preventDefault();
    e.stopPropagation();
  };
  const move = (e: ReactPointerEvent) => {
    if (!mode.current) return;
    const dx = e.clientX - start.current.px;
    const dy = e.clientY - start.current.py;
    if (mode.current === 'move') {
      setBox((b) => ({ ...b, x: start.current.x + dx, y: Math.max(0, start.current.y + dy) }));
    } else {
      setBox((b) => ({
        ...b,
        w: Math.max(200, start.current.w + dx),
        h: Math.max(120, start.current.h + dy),
      }));
    }
  };
  const end = () => {
    if (mode.current) writeCardBox(storageId, box);
    mode.current = null;
  };

  const pos: CSSProperties =
    box.x < 0 ? { right: -box.x, top: box.y } : { left: box.x, top: box.y };

  return (
    <div style={{ ...cardShell, ...pos, width: box.w, height: box.h }}>
      <div
        style={cardGrip}
        onPointerDown={begin('move')}
        onPointerMove={move}
        onPointerUp={end}
        onPointerCancel={end}
      >
        <span style={{ flex: 1 }}>{title}</span>
        {onClose ? (
          <button
            type="button"
            style={cardClose}
            onPointerDown={(e) => e.stopPropagation()}
            onClick={onClose}
            aria-label={`hide ${title}`}
          >
            ✕
          </button>
        ) : null}
      </div>
      <div style={cardBody}>{children}</div>
      <div
        style={cardResize}
        onPointerDown={begin('size')}
        onPointerMove={move}
        onPointerUp={end}
        onPointerCancel={end}
      />
    </div>
  );
}

const cardShell: CSSProperties = {
  position: 'absolute',
  zIndex: 7,
  display: 'flex',
  flexDirection: 'column',
  overflow: 'hidden',
  borderRadius: 8,
  border: '1px solid var(--line, #32373f)',
  background: 'rgba(20, 23, 28, 0.92)',
  boxShadow: '0 8px 28px rgba(0,0,0,0.55)',
};

const cardGrip: CSSProperties = {
  flex: '0 0 22px',
  display: 'flex',
  alignItems: 'center',
  padding: '0 8px',
  fontSize: 9,
  fontWeight: 800,
  letterSpacing: '0.16em',
  color: 'var(--fg-2, #9aa3ae)',
  background: 'var(--bg-1, #1b1e23)',
  borderBottom: '1px solid var(--line, #32373f)',
  cursor: 'move',
  touchAction: 'none',
  userSelect: 'none',
};

const cardBody: CSSProperties = {
  flex: 1,
  minHeight: 0,
  overflow: 'hidden',
  display: 'flex',
};

const cardResize: CSSProperties = {
  position: 'absolute',
  right: 0,
  bottom: 0,
  width: 26,
  height: 26,
  cursor: 'nwse-resize',
  touchAction: 'none',
  background:
    'linear-gradient(135deg, transparent 55%, var(--fg-3, #6a727d) 55%, var(--fg-3, #6a727d) 62%, transparent 62%, transparent 75%, var(--fg-3, #6a727d) 75%, var(--fg-3, #6a727d) 82%, transparent 82%)',
};

const flagRow: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 8,
};

const flagMode: CSSProperties = {
  fontSize: 10,
  fontWeight: 700,
  letterSpacing: '0.1em',
  color: 'var(--fg-2, #9aa3ae)',
};

const stack: CSSProperties = {
  position: 'relative',
  display: 'flex',
  flexDirection: 'column',
  width: '100%',
  height: '100%',
  minHeight: 0,
  background: 'var(--bg-workspace, #101215)',
};

const pane: CSSProperties = {
  position: 'relative',
  display: 'flex',
  flexDirection: 'column',
  minHeight: 0,
  overflow: 'hidden',
};

const paneSpectrum: CSSProperties = {
  position: 'relative',
  flex: '0 0 44%', // overridden inline by the per-pane ratio drag
  minHeight: 0,
  display: 'flex',
};

const paneWaterfall: CSSProperties = {
  position: 'relative',
  flex: '1 1 auto',
  minHeight: 0,
  display: 'flex',
};







const popover: CSSProperties = {
  marginTop: 6,
  padding: 8,
  width: '100%',
  boxSizing: 'border-box',
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
  borderRadius: 8,
  border: '1px solid var(--line, #263041)',
  background: 'rgba(10, 14, 20, 0.96)',
};

const popRow: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 8,
};

const popLabel: CSSProperties = {
  width: 38,
  flex: '0 0 38px',
  fontSize: 10,
  fontWeight: 800,
  letterSpacing: '0.08em',
  color: 'var(--fg-2, #9aa3ae)',
};

const popVal: CSSProperties = {
  flex: '0 0 40px',
  minWidth: 0,
  textAlign: 'right',
  fontFamily: '"JetBrains Mono", Consolas, monospace',
  fontSize: 11,
  color: 'var(--fg-1, #c3cad3)',
};

const sPeak: CSSProperties = {
  position: 'absolute',
  top: -1,
  bottom: -1,
  width: 2,
  background: '#ffffffcc',
};

const cardClose: CSSProperties = {
  width: 20,
  height: 16,
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  border: 'none',
  borderRadius: 3,
  background: 'transparent',
  color: 'var(--fg-2, #9aa3ae)',
  fontSize: 10,
  cursor: 'pointer',
};

const restoreRow: CSSProperties = {
  position: 'absolute',
  top: 6,
  right: 10,
  zIndex: 8,
  display: 'flex',
  gap: 6,
};

const restorePill: CSSProperties = {
  padding: '4px 9px',
  borderRadius: 10,
  border: '1px solid var(--line, #263041)',
  background: 'rgba(13, 18, 26, 0.9)',
  color: 'var(--fg-2, #9aa3ae)',
  fontSize: 10,
  fontWeight: 700,
  letterSpacing: '0.06em',
  cursor: 'pointer',
};

const speedPill: CSSProperties = {
  marginLeft: 8,
  padding: '4px 8px',
  borderRadius: 6,
  border: '1px solid var(--line, #263041)',
  background: 'transparent',
  color: 'var(--fg-1, #c3cad3)',
  fontFamily: '"JetBrains Mono", Consolas, monospace',
  fontSize: 11,
  fontWeight: 700,
  cursor: 'pointer',
};

const zoomDock: CSSProperties = {
  position: 'absolute',
  right: 10,
  bottom: 10,
  zIndex: 6,
  display: 'flex',
  alignItems: 'center',
  padding: '4px 8px',
  borderRadius: 7,
  border: '1px solid var(--line, #263041)',
  background: 'rgba(13, 18, 26, 0.88)',
};

const ratioBar: CSSProperties = {
  flex: `0 0 ${RATIO_H}px`,
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  background: 'var(--bg-1, #14181f)',
  borderTop: '1px solid var(--line, #263041)',
  borderBottom: '1px solid var(--line, #263041)',
  cursor: 'row-resize',
  touchAction: 'none',
  zIndex: 5,
};

const sBarRow: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 6,
  marginTop: 2,
};

const sReadout: CSSProperties = {
  minWidth: 44,
  fontFamily: '"JetBrains Mono", Consolas, monospace',
  fontSize: 11,
  fontWeight: 700,
  color: 'var(--fg-0, #e8ecf1)',
};

const sTick: CSSProperties = {
  position: 'absolute',
  top: 0,
  bottom: 0,
  width: 1,
  background: 'rgba(232, 236, 241, 0.45)',
};

const sTickLabels: CSSProperties = {
  position: 'relative',
  height: 10,
  marginTop: 1,
};

const sTickLabel: CSSProperties = {
  position: 'absolute',
  transform: 'translateX(-50%)',
  fontSize: 7,
  letterSpacing: '0.04em',
  color: 'var(--fg-3, #6a727d)',
};

const flagChip: CSSProperties = {
  padding: '1px 6px',
  borderRadius: 3,
  border: '1px solid var(--line, #263041)',
  fontSize: 9,
  fontWeight: 700,
  letterSpacing: '0.06em',
  color: 'var(--fg-2, #9aa3ae)',
};

const sBarShell: CSSProperties = {
  flex: 1,
  position: 'relative',
  height: 7,
  borderRadius: 3,
  background: 'rgba(8, 12, 18, 0.9)',
  border: '1px solid var(--line, #263041)',
  overflow: 'hidden',
};

const sBarFill: CSSProperties = {
  height: '100%',
  borderRadius: 2,
  background: 'linear-gradient(90deg, #2c8f5b, #3fd08a 65%, var(--accent, #4aa3df))',
};

const dividerGrip: CSSProperties = {
  width: 48,
  height: 2,
  borderRadius: 1,
  background: 'var(--fg-3, #6a727d)',
};

const flag: CSSProperties = {
  position: 'absolute',
  top: 8,
  left: 10,
  zIndex: 6,
  width: 218,
  boxSizing: 'border-box',
  display: 'flex',
  flexDirection: 'column',
  gap: 3,
  padding: '5px 10px',
  borderRadius: 6,
  border: '1px solid var(--line, #32373f)',
  background: 'rgba(27, 30, 35, 0.88)',
};

const flagActive: CSSProperties = {
  borderColor: 'var(--accent, #4aa3df)',
};

const flagBadge: CSSProperties = {
  padding: '1px 7px',
  borderRadius: 3,
  background: 'var(--fg-3, #6a727d)',
  color: '#141414',
  fontSize: 11,
  fontWeight: 800,
  letterSpacing: '0.04em',
};

const flagBadgeActive: CSSProperties = {
  background: 'var(--accent, #4aa3df)',
};

const popBtn: CSSProperties = {
  minHeight: 40,
  borderRadius: 6,
  border: '1px solid var(--line, #32373f)',
  background: 'var(--bg-2, #1c2129)',
  color: 'var(--fg-0, #e8ecf1)',
  fontSize: 16,
  fontWeight: 700,
  cursor: 'pointer',
};

const flagVfo: CSSProperties = {
  // Clamp the compact VfoDisplay into the 218px flag column.
  width: '100%',
  minWidth: 0,
  overflow: 'hidden',
  fontSize: 21,
};

const flagActiveTag: CSSProperties = {
  fontSize: 9,
  fontWeight: 700,
  letterSpacing: '0.14em',
  color: 'var(--accent, #4aa3df)',
};
