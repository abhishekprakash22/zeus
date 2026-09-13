// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Assignable NR key for the G2 touch deck: one press steps
// Off → NR1 → NR2 → NR3 → NR4 → NR5 → Off, so noise reduction is reachable
// from the front glass without opening the NB·NR page. Modes the engine can't
// run are skipped by the shared cycle (NR3 needs libwdsp's RNNoise export and
// a loaded model; NR5 needs the NNR setters), so the key never offers a mode
// that would fail.
//
// The label carries the state — 'NR' when off, 'NR1'..'NR5' when active — and
// the key lights like every other deck toggle, so the current mode is readable
// at a glance from across the shack.

import { useCallback, useRef } from 'react';
import { setNr } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { NR_LABEL, nextNrMode, nrModeTitle } from './nr-cycle';

export function NrKeyButton() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const nr = useConnectionStore((s) => s.nr);
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
    setNr({ ...nr, nrMode: next }, ac.signal)
      .then((s) => {
        if (!ac.signal.aborted) applyState(s);
      })
      .catch(() => {
        /* the next state poll reconciles */
      });
  }, [connected, nr, nr3Available, nr3ModelName, nnrAvailable, applyState]);

  const mode = nr?.nrMode ?? 'Off';
  const on = mode !== 'Off';

  return (
    <button
      type="button"
      className="btn ghost"
      style={on ? { background: 'var(--accent, #4aa3df)', color: '#08101d' } : undefined}
      onClick={cycle}
      disabled={!connected}
      title={`${nrModeTitle(mode)} — press to step through the noise reduction modes`}
      aria-label={`Noise reduction: ${nrModeTitle(mode)}`}
    >
      {NR_LABEL[mode]}
    </button>
  );
}
