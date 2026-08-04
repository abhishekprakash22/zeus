// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Diversity pop-out — DigitalWindow's sibling: a fixed, draggable,
// always-on-top window summoned from the console DIV button. Null steering is
// a calibration activity (summon, steer, save, dismiss), so a pop-out fits
// the workflow better than a permanent tile; the workspace panel remains for
// operators who want it resident. Escape closes; hidden (not exited) while
// the Settings view is showing, mirroring DigitalWindow's z-order note.

import { useCallback, useEffect, useRef, useState } from 'react';
import { DiversityPanel } from './DiversityPanel';
import { useDiversityStore } from '../state/diversity-store';
import { useLayoutStore } from '../state/layout-store';

const WIDTH = 348;

export function DiversityWindow() {
  const open = useDiversityStore((s) => s.panelOpen);
  const enabled = useDiversityStore((s) => s.enabled);
  const setPanelOpen = useDiversityStore((s) => s.setPanelOpen);
  const settingsViewOpen = useLayoutStore((s) => s.settingsViewOpen);
  const [pos, setPos] = useState({ x: window.innerWidth - WIDTH - 24, y: 88 });

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      const t = e.target as HTMLElement | null;
      if (t?.closest('input, textarea, select, [contenteditable="true"]')) return;
      setPanelOpen(false);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, setPanelOpen]);

  const drag = useRef<{ id: number; sx: number; sy: number; ox: number; oy: number } | null>(
    null,
  );
  const onHeaderPointerDown = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const target = e.target as HTMLElement;
      if (target.closest('[data-no-drag]')) return;
      e.currentTarget.setPointerCapture(e.pointerId);
      drag.current = { id: e.pointerId, sx: e.clientX, sy: e.clientY, ox: pos.x, oy: pos.y };
    },
    [pos],
  );
  const onHeaderPointerMove = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    const d = drag.current;
    if (!d || d.id !== e.pointerId) return;
    const x = Math.min(window.innerWidth - 80, Math.max(-WIDTH + 80, d.ox + e.clientX - d.sx));
    const y = Math.min(window.innerHeight - 40, Math.max(48, d.oy + e.clientY - d.sy));
    setPos({ x, y });
  }, []);
  const onHeaderPointerUp = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    if (drag.current?.id === e.pointerId) drag.current = null;
  }, []);

  if (!open || settingsViewOpen) return null;

  return (
    <div
      className="diversity-window"
      style={{ left: pos.x, top: pos.y, width: WIDTH }}
      role="dialog"
      aria-label="Diversity null steering"
    >
      <div
        className="diversity-window-header"
        onPointerDown={onHeaderPointerDown}
        onPointerMove={onHeaderPointerMove}
        onPointerUp={onHeaderPointerUp}
      >
        <span className={`dw-dot ${enabled ? 'on' : ''}`} />
        <span className="dw-title">DIVERSITY · NULL STEERING</span>
        <span className="dw-spacer" />
        <button
          type="button"
          className="dw-close"
          data-no-drag
          onClick={() => setPanelOpen(false)}
          aria-label="Close diversity panel"
        >
          ✕
        </button>
      </div>
      <DiversityPanel />
    </div>
  );
}

/** Console toggle — lives in the transport bar. Lights while the combiner is
 *  engaged even when the window is closed, so an active null is never
 *  invisible chrome-state. */
export function DiversityToggleButton() {
  const open = useDiversityStore((s) => s.panelOpen);
  const enabled = useDiversityStore((s) => s.enabled);
  const setPanelOpen = useDiversityStore((s) => s.setPanelOpen);
  return (
    <button
      type="button"
      className={`btn ghost diversity-toggle ${open ? 'open' : ''} ${enabled ? 'engaged' : ''}`}
      onClick={() => setPanelOpen(!open)}
      title="Diversity null steering (RX1 + RX2 coherent combine)"
    >
      DIV
    </button>
  );
}
