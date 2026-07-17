// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { setPs, setPsMonitor } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useRadioStore } from '../state/radio-store';
import { useTxStore } from '../state/tx-store';
import { PsStatusPopover } from './PsStatusPopover';

// Connected board kinds that don't have a real PS feedback receiver. PS
// Monitor (post-PA loopback display source) is only meaningful where the
// board has a feedback path, so we don't auto-enable it for these.
const PS_MONITOR_UNSUPPORTED = new Set(['HermesLite2']);

/**
 * PureSignal master arm. Optimistic update with rollback on server refusal —
 * same pattern as MoxButton. Available on both Protocol 1 (HL2) and
 * Protocol 2 (G2 / Orion / Saturn); unsupported boards are rejected or
 * no-op gated by the backend wire path.
 */
export function PsToggleButton() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const psEnabled = useTxStore((s) => s.psEnabled);
  const psAuto = useTxStore((s) => s.psAuto);
  const psSingle = useTxStore((s) => s.psSingle);
  const psMonitorEnabled = useTxStore((s) => s.psMonitorEnabled);
  const setPsEnabled = useTxStore((s) => s.setPsEnabled);
  const setPsMonitorLocal = useTxStore((s) => s.setPsMonitorEnabled);
  const connectedBoard = useRadioStore((s) => s.selection.connected);

  const disabled = !connected;
  const tooltip = psEnabled
    ? 'PureSignal armed — predistortion active'
    : 'Arm PureSignal predistortion';

  const click = useCallback(() => {
    if (disabled) return;
    const next = !psEnabled;
    setPsEnabled(next);
    setPs({ enabled: next, auto: psAuto, single: psSingle }).catch(() => {
      setPsEnabled(!next);
    });
    // When arming PS, also turn on PS Monitor by default — operators almost
    // always want to see the post-PA loopback while PS is correcting, and
    // having it default off forced an extra trip to Settings every session.
    // Only auto-toggles up; disarming PS doesn't force the monitor off so
    // the operator can keep watching the trace if they had it on
    // pre-arming. Skip on boards without a real feedback receiver (HL2).
    if (
      next
      && !psMonitorEnabled
      && !PS_MONITOR_UNSUPPORTED.has(connectedBoard)
    ) {
      setPsMonitorLocal(true);
      setPsMonitor(true).catch(() => setPsMonitorLocal(false));
    }
  }, [
    disabled,
    psEnabled,
    psAuto,
    psSingle,
    psMonitorEnabled,
    connectedBoard,
    setPsEnabled,
    setPsMonitorLocal,
  ]);

  // Hover-pinned popover — appears above the button while either the button
  // or the popover itself is hovered (a small close delay lets the operator
  // slide their cursor off the button onto the popover without it
  // collapsing). Click still toggles the arm state; the popover is purely
  // informational. Native title tooltip is suppressed while armed so it
  // doesn't overlay the popover.
  //
  // Rendered through a portal to <body>: the transport bar scrolls its
  // control clusters horizontally (overflow-x: auto), and any non-visible
  // ancestor overflow clips absolutely-positioned descendants — an in-bar
  // anchor would render above the bar but be invisible. A fixed-position
  // portal keeps the popover immune to container overflow.
  const [open, setOpen] = useState(false);
  const [anchor, setAnchor] = useState<{ x: number; y: number } | null>(null);
  const wrapRef = useRef<HTMLSpanElement | null>(null);
  const closeTimer = useRef<number | null>(null);

  const cancelClose = useCallback(() => {
    if (closeTimer.current != null) {
      window.clearTimeout(closeTimer.current);
      closeTimer.current = null;
    }
  }, []);
  const scheduleClose = useCallback(() => {
    cancelClose();
    closeTimer.current = window.setTimeout(() => setOpen(false), 120);
  }, [cancelClose]);
  const openPopover = useCallback(() => {
    cancelClose();
    const rect = wrapRef.current?.getBoundingClientRect();
    if (rect) setAnchor({ x: rect.left + rect.width / 2, y: rect.top });
    setOpen(true);
  }, [cancelClose]);

  useEffect(() => () => cancelClose(), [cancelClose]);

  return (
    <span
      ref={wrapRef}
      className="ps-button-wrap"
      onMouseEnter={openPopover}
      onMouseLeave={scheduleClose}
      onFocus={openPopover}
      onBlur={scheduleClose}
    >
      <button
        type="button"
        disabled={disabled}
        onClick={click}
        className={`btn tx-btn ${psEnabled ? 'active' : ''}`}
        title={psEnabled ? undefined : tooltip}
        aria-describedby={open ? 'ps-status-popover' : undefined}
      >
        <span className={`led ${psEnabled ? 'on' : ''}`} style={{ marginRight: 8 }} />
        PS
      </button>
      {open && psEnabled && anchor
        ? createPortal(
            <span
              id="ps-status-popover"
              className="ps-popover-portal"
              style={{ left: anchor.x, top: anchor.y }}
              role="presentation"
              onMouseEnter={cancelClose}
              onMouseLeave={scheduleClose}
            >
              <PsStatusPopover />
            </span>,
            document.body,
          )
        : null}
    </span>
  );
}
