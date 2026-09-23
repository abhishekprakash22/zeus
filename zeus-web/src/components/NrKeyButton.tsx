// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Assignable NR key for the G2 touch deck: one press steps
// Off → NR (EMNR) → NR (Neural) → Off, so the two modes anyone actually
// reaches for are on the front glass without opening the NB·NR page. The
// other modes (ANR, RNNoise, SBNR) stay reachable there. Neural drops out of
// the cycle when the engine can't run it, so the key never offers a mode that
// would fail.
//
// The label carries the state — 'NR' when off, 'NR1'..'NR5' when active — and
// the key lights like every other deck toggle, so the current mode is readable
// at a glance from across the shack.

import { useCallback, useRef } from 'react';
import { setNr, setReceiver } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { getReceiverNr } from '../state/receiver-state';
import { NR_KEY_LABEL, nextNrMode, nrModeTitle } from './nr-cycle';

export function NrKeyButton() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  // NR is per receiver: the key acts on the FOCUSED pane, the way AGC-T and
  // AF already do. RX1 keeps the flat /api/rx/nr path; RX2+ set their own.
  const focusedRx = useConnectionStore((s) => s.focusedRxIndex);
  const nr = useConnectionStore((s) => getReceiverNr(s, focusedRx));
  const applyState = useConnectionStore((s) => s.applyState);
  const nr3Available = useConnectionStore((s) => s.wdspNr3RnnrAvailable);
  const nr3ModelName = useConnectionStore((s) => s.nr3ModelName);
  const nnrAvailable = useConnectionStore((s) => s.wdspNnrAvailable);
  const inflight = useRef<AbortController | null>(null);

  const cycle = useCallback(() => {
    if (!connected || !nr) return;
    const next = nextNrMode(nr.nrMode, nr3Available && !!nr3ModelName, nnrAvailable);
    inflight.current?.abort();
    const ac = new AbortController();
    inflight.current = ac;
    const nextCfg = { ...nr, nrMode: next };
    const call =
      focusedRx === 0
        ? setNr(nextCfg, ac.signal)
        : setReceiver(focusedRx, { nr: nextCfg }, ac.signal);
    call
      .then((s) => {
        if (!ac.signal.aborted) applyState(s);
      })
      .catch(() => {
        /* the next state poll reconciles */
      });
  }, [connected, focusedRx, nr, nr3Available, nr3ModelName, nnrAvailable, applyState]);

  const mode = nr?.nrMode ?? 'Off';
  const on = mode !== 'Off';

  return (
    <button
      type="button"
      // .btn.active is the app's own on-state (themed tokens, border, glow) —
      // an inline background is NOT the same thing and read as a foreign
      // bright-blue slab next to CTUN/PS/TUN on the deck.
      className={`btn ghost${on ? ' active' : ''}`}
      onClick={cycle}
      disabled={!connected}
      title={`${nrModeTitle(mode)} — press to step through the noise reduction modes`}
      aria-label={`Noise reduction: ${nrModeTitle(mode)}`}
    >
      {NR_KEY_LABEL[mode]}
    </button>
  );
}
