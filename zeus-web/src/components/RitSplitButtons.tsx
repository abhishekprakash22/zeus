// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// SPLIT + RIT transport controls — the real ones, replacing the upstream
// placeholder stubs. Backends already shipped: POST /api/tx/vfo (Thetis
// semantics: SPLIT = TX carrier follows VFO B) and POST /api/rx/rit
// (RX1 demod offset, ±99999 Hz, dial untouched). Server-authoritative
// state arrives through the normal state frames (connection-store
// ritEnabled/ritHz/splitEnabled).

import { useConnectionStore } from '../state/connection-store';

function post(url: string, body: unknown): void {
  void fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).catch(() => {});
}

const RIT_STEP_HZ = 10;
const RIT_MAX_HZ = 99_999;
const clamp = (v: number) => Math.max(-RIT_MAX_HZ, Math.min(RIT_MAX_HZ, v));

export function SplitButton() {
  const splitEnabled = useConnectionStore((s) => s.splitEnabled);
  return (
    <button
      type="button"
      className={`btn ghost split-toggle ${splitEnabled ? 'engaged' : ''}`}
      title="Split: transmit on VFO B (receive stays on A)"
      onClick={() => post('/api/tx/vfo', { txVfo: splitEnabled ? 0 : 1 })}
    >
      SPLIT
    </button>
  );
}

export function RitButton() {
  const ritEnabled = useConnectionStore((s) => s.ritEnabled);
  const ritHz = useConnectionStore((s) => s.ritHz);
  const step = (d: number) => post('/api/rx/rit', { hz: clamp(ritHz + d) });
  return (
    <span className="rit-cluster">
      <button
        type="button"
        className={`btn ghost rit-toggle ${ritEnabled ? 'engaged' : ''}`}
        title="RIT: offset RX1 tuning without moving the dial"
        onClick={() => post('/api/rx/rit', { enabled: !ritEnabled })}
      >
        RIT
      </button>
      {ritEnabled && (
        <span
          className="rit-chip"
          title="Scroll or use −/+ (10 Hz). Middle-click zeroes."
          onWheel={(e) => step(e.deltaY < 0 ? RIT_STEP_HZ : -RIT_STEP_HZ)}
          onAuxClick={(e) => {
            if (e.button === 1) post('/api/rx/rit', { hz: 0 });
          }}
        >
          <button type="button" data-no-drag onClick={() => step(-RIT_STEP_HZ)}>
            −
          </button>
          <span className="rit-val">
            {ritHz > 0 ? '+' : ''}
            {ritHz}
          </span>
          <button type="button" data-no-drag onClick={() => step(RIT_STEP_HZ)}>
            +
          </button>
        </span>
      )}
    </span>
  );
}
