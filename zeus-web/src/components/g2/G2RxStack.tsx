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
import { AnalogMeterPanel } from '../analog-meter/AnalogMeterPanel';
import { FilterMiniPan } from '../filter/FilterMiniPan';
import { useConnectionStore } from '../../state/connection-store';
import { useRxMetersStore } from '../../state/rx-meters-store';
import { useDisplayStore, selectDisplaySlice } from '../../state/display-store';
import { getReceiverVfoHz, getReceiverMode, rxIndexOf, type ReceiverKey } from '../../state/receiver-state';

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

  return (
    <div style={stack}>
      <RxPane receiver="A" heightPct={rx2Enabled ? 50 : 100} />
      {rx2Enabled ? <RxPane receiver="B" heightPct={50} /> : null}
      {/* Instrument cards (commit 4 of the approved frame): the analog
          S-meter — face untouched, the Zeus signature — and the bandwidth
          filter display (FilterMiniPan; it splits per receiver internally
          when RX2 is enabled). Cards float over the stack's top-right so
          they cost no pane height; the panadapters keep full width. */}
      <G2Card title="S-METER" initial={{ x: -312, y: 8, w: 300, h: 170 }}>
        <AnalogMeterPanel sampleDbmOverride={sampleFocusedRxDbm} />
      </G2Card>
      <G2Card title="FILTER · RX1" initial={{ x: -312, y: 186, w: 300, h: 150 }}>
        <FilterMiniPan receiver="A" />
      </G2Card>
      {rx2Enabled ? (
        <G2Card title="FILTER · RX2" initial={{ x: -312, y: 372, w: 300, h: 150 }}>
          <FilterMiniPan receiver="B" />
        </G2Card>
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
  // Spectrum share of the pane (the requested per-receiver drag): 0.2-0.7.
  const [specFrac, setSpecFrac] = useState(0.44);
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
        <Waterfall receiver={receiver} />
      </div>
      <div style={{ ...flag, ...(active ? flagActive : null) }}>
        <div style={flagRow}>
          <span style={{ ...flagBadge, ...(active ? flagBadgeActive : null) }}>
            RX{rxIndex + 1}
          </span>
          {active ? <span style={flagActiveTag}>ACTIVE</span> : null}
        </div>
        <span style={flagFreq}>{formatMHz(vfoHz)}</span>
        <span style={flagMode}>{mode ?? ''}</span>
        <div style={sBarShell}>
          <div style={{ ...sBarFill, width: `${dbmToPct(barDbm)}%` }} />
        </div>
      </div>
    </div>
  );
}

function formatMHz(hz: number): string {
  const mhz = hz / 1e6;
  const [int, frac = ''] = mhz.toFixed(6).split('.');
  return `${int}.${frac.slice(0, 3)}.${frac.slice(3, 6)} MHz`;
}

/** Draggable + resizable floating card. x negative = anchored from the
 *  right edge (so defaults hug top-right like the approved frame). Drag by
 *  the title strip, resize by the corner handle; session-only positions. */
function G2Card({
  title,
  initial,
  children,
}: {
  title: string;
  initial: { x: number; y: number; w: number; h: number };
  children: ReactNode;
}) {
  const [box, setBox] = useState(initial);
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
        {title}
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

const sBarShell: CSSProperties = {
  height: 6,
  marginTop: 2,
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
  display: 'flex',
  flexDirection: 'column',
  gap: 3,
  padding: '5px 10px',
  borderRadius: 6,
  border: '1px solid var(--line, #32373f)',
  background: 'rgba(27, 30, 35, 0.88)',
  pointerEvents: 'none',
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

const flagFreq: CSSProperties = {
  fontFamily: '"JetBrains Mono", Consolas, monospace',
  fontSize: 21,
  fontWeight: 600,
  color: 'var(--fg-0, #e8ecf1)',
};

const flagActiveTag: CSSProperties = {
  fontSize: 9,
  fontWeight: 700,
  letterSpacing: '0.14em',
  color: 'var(--accent, #4aa3df)',
};
