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
import type { CSSProperties, PointerEvent as ReactPointerEvent } from 'react';
import { Panadapter } from '../Panadapter';
import { Waterfall } from '../Waterfall';
import { AnalogMeterPanel } from '../analog-meter/AnalogMeterPanel';
import { FilterMiniPan } from '../filter/FilterMiniPan';
import { useConnectionStore } from '../../state/connection-store';
import { getReceiverVfoHz, rxIndexOf, type ReceiverKey } from '../../state/receiver-state';

const DIVIDER_H = 10;

export function G2RxStack() {
  // Fraction of the stack height given to RX1 (the rest goes to RX2).
  const [split, setSplit] = useState(0.55);
  const rootRef = useRef<HTMLDivElement | null>(null);
  const dragging = useRef(false);

  const onDividerDown = useCallback((e: ReactPointerEvent) => {
    dragging.current = true;
    (e.target as HTMLElement).setPointerCapture(e.pointerId);
  }, []);
  const onDividerMove = useCallback((e: ReactPointerEvent) => {
    if (!dragging.current || !rootRef.current) return;
    const r = rootRef.current.getBoundingClientRect();
    const f = (e.clientY - r.top) / Math.max(1, r.height);
    setSplit(Math.min(0.8, Math.max(0.2, f)));
  }, []);
  const onDividerUp = useCallback(() => {
    dragging.current = false;
  }, []);

  return (
    <div ref={rootRef} style={stack}>
      <RxPane receiver="A" heightPct={split * 100} />
      <div
        style={divider}
        onPointerDown={onDividerDown}
        onPointerMove={onDividerMove}
        onPointerUp={onDividerUp}
        onPointerCancel={onDividerUp}
      >
        <span style={dividerGrip} />
      </div>
      <RxPane receiver="B" heightPct={(1 - split) * 100} />
      {/* Instrument cards (commit 4 of the approved frame): the analog
          S-meter — face untouched, the Zeus signature — and the bandwidth
          filter display (FilterMiniPan; it splits per receiver internally
          when RX2 is enabled). Cards float over the stack's top-right so
          they cost no pane height; the panadapters keep full width. */}
      <div style={meterCard}>
        <AnalogMeterPanel />
      </div>
      <div style={filterCard}>
        <FilterMiniPan />
      </div>
    </div>
  );
}

function RxPane({ receiver, heightPct }: { receiver: ReceiverKey; heightPct: number }) {
  const rxIndex = rxIndexOf(receiver);
  const active = useConnectionStore((s) => s.focusedRxIndex === rxIndex);
  const setFocusedRxIndex = useConnectionStore((s) => s.setFocusedRxIndex);
  const vfoHz = useConnectionStore((s) => getReceiverVfoHz(s, receiver));

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
      style={{ ...pane, height: `calc(${heightPct}% - ${DIVIDER_H / 2}px)` }}
      onPointerDownCapture={onCapturePointerDown}
    >
      <div style={paneSpectrum}>
        <Panadapter receiver={receiver} multiRx={false} />
      </div>
      <div style={paneWaterfall}>
        <Waterfall receiver={receiver} />
      </div>
      <div style={{ ...flag, ...(active ? flagActive : null) }}>
        <span style={{ ...flagBadge, ...(active ? flagBadgeActive : null) }}>
          RX{rxIndex + 1}
        </span>
        <span style={flagFreq}>{formatMHz(vfoHz)}</span>
        {active ? <span style={flagActiveTag}>ACTIVE</span> : null}
      </div>
    </div>
  );
}

function formatMHz(hz: number): string {
  const mhz = hz / 1e6;
  const [int, frac = ''] = mhz.toFixed(6).split('.');
  return `${int}.${frac.slice(0, 3)}.${frac.slice(3, 6)} MHz`;
}

const meterCard: CSSProperties = {
  position: 'absolute',
  top: 8,
  right: 12,
  zIndex: 7,
  width: 300,
  height: 170,
  overflow: 'hidden',
  borderRadius: 8,
  border: '1px solid var(--line, #32373f)',
  background: 'rgba(20, 23, 28, 0.92)',
  boxShadow: '0 8px 28px rgba(0,0,0,0.55)',
};

const filterCard: CSSProperties = {
  position: 'absolute',
  top: 186,
  right: 12,
  zIndex: 7,
  width: 300,
  maxHeight: 170,
  overflow: 'hidden',
  borderRadius: 8,
  border: '1px solid var(--line, #32373f)',
  background: 'rgba(20, 23, 28, 0.92)',
  boxShadow: '0 8px 28px rgba(0,0,0,0.55)',
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
  flex: '0 0 44%',
  minHeight: 0,
  display: 'flex',
};

const paneWaterfall: CSSProperties = {
  position: 'relative',
  flex: '1 1 auto',
  minHeight: 0,
  display: 'flex',
};

const divider: CSSProperties = {
  flex: `0 0 ${DIVIDER_H}px`,
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  background: 'var(--bg-1, #1b1e23)',
  borderTop: '1px solid var(--line, #32373f)',
  borderBottom: '1px solid var(--line, #32373f)',
  cursor: 'row-resize',
  touchAction: 'none',
  zIndex: 5,
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
  alignItems: 'center',
  gap: 8,
  padding: '5px 10px',
  borderRadius: 6,
  border: '1px solid var(--line, #32373f)',
  background: 'rgba(27, 30, 35, 0.88)',
  pointerEvents: 'none',
};

const flagActive: CSSProperties = {
  borderColor: 'rgba(242, 193, 29, 0.55)',
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
  background: 'var(--accent, #f2c11d)',
};

const flagFreq: CSSProperties = {
  fontFamily: '"JetBrains Mono", Consolas, monospace',
  fontSize: 15,
  fontWeight: 600,
  color: 'var(--fg-0, #e8ecf1)',
};

const flagActiveTag: CSSProperties = {
  fontSize: 9,
  fontWeight: 700,
  letterSpacing: '0.14em',
  color: 'var(--accent, #f2c11d)',
};
