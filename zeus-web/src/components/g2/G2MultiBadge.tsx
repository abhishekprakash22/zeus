// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Front-panel MULTI-encoder badge (Laurence review item 2 / round 4).
//
// ALWAYS-MOUNTED main-screen chrome: App.tsx renders it whenever the G2
// layout is on. Round one mistakenly mounted it inside G2LayoutSection —
// which is the Settings→Display CARD about the layout, not the layout
// itself — so position:fixed floated it over the viewport only while that
// settings tab was open. The field report ("only shows on the display tab
// of the setup form") was verbatim correct.
//
// Two states:
//  - operate (default): quiet chip top-right, accent flash 1.2 s whenever
//    the assignment changes.
//  - select (multiEncoderSelecting): the MULTI press re-purposed the knob
//    to CHOOSE the function — the chip goes accent-solid and grows so the
//    choice is readable from across the shack; press again (or 5 s idle)
//    returns the knob to operating the chosen function.
//
// Renders nothing until the radio has published an assignment (desktop
// sessions with no front panel stay clean). pointer-events: none — it is
// a readout, never a control, and must not eat kiosk touches.

import { useEffect, useRef, useState } from 'react';
import { useTxStore } from '../../state/tx-store';

export function G2MultiBadge() {
  const name = useTxStore((s) => s.multiEncoderFunction);
  const selecting = useTxStore((s) => s.multiEncoderSelecting);
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
  const lit = selecting || flash;
  return (
    <div
      style={{
        position: 'fixed',
        top: 6,
        right: 10,
        // Above the floating cards and the CONTROLS side panel (z 460) — a
        // draggable card parked top-right must never hide the assignment.
        zIndex: 500,
        padding: selecting ? '6px 14px' : '3px 10px',
        borderRadius: 6,
        fontSize: selecting ? 15 : 11,
        fontWeight: selecting ? 600 : 400,
        letterSpacing: 0.6,
        border: '1px solid ' + (lit ? 'var(--accent, #4aa3df)' : '#2a3a52'),
        background: lit ? 'var(--accent, #4aa3df)' : 'rgba(13,21,38,0.85)',
        color: lit ? '#08111f' : '#9fc3e8',
        transition:
          'background 200ms, color 200ms, border-color 200ms, font-size 150ms, padding 150ms',
        pointerEvents: 'none',
      }}
    >
      {selecting ? 'SELECT · ' : 'MULTI · '}
      {name.toUpperCase()}
    </div>
  );
}
