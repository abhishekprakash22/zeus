// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// MON — the front-panel switch for the TX monitor that has existed, fully
// plumbed and headless, since issue #106's follow-up: POST /api/tx/monitor
// makes the engine demodulate the post-CFIR TX IQ back to audio, so the
// operator hears the WHOLE chain (mic → EQ → Leveler → VST → CFC → ALC →
// bandpass) at the true TX bandwidth — with MOX on, or as a silent-key
// preview with MOX off (VST fed, meters live). RX audio is suppressed while
// monitoring. Session-only by discipline: resets off each connect, like MOX.

import { useState } from 'react';
import { setTxMonitor } from '../api/client';
import { useTxStore } from '../state/tx-store';

export function TxMonitorButton() {
  const enabled = useTxStore((s) => s.txMonitorEnabled);
  const setEnabled = useTxStore((s) => s.setTxMonitorEnabled);
  const [busy, setBusy] = useState(false);

  const toggle = () => {
    if (busy) return;
    const next = !enabled;
    setBusy(true);
    setEnabled(next); // optimistic; radio-state confirms
    void setTxMonitor(next)
      .then((state) => setEnabled(state.txMonitorEnabled))
      .catch(() => setEnabled(!next))
      .finally(() => setBusy(false));
  };

  return (
    <button
      type="button"
      className={`btn ghost mon-toggle ${enabled ? 'engaged' : ''}`}
      title={
        enabled
          ? 'TX monitor ON — you are hearing your own transmit chain (RX audio suppressed). Press to return to receive audio.'
          : 'TX monitor: hear your own transmit audio through the full chain at true TX bandwidth. Works with MOX off as a preview — meters and VST run without keying.'
      }
      aria-pressed={enabled}
      onClick={toggle}
    >
      MON
    </button>
  );
}
