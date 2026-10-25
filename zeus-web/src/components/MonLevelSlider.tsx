// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// MON level — the listening level for the TX monitor (MON). Attenuation only,
// −40..0 dB, default 0 dB. It scales what the operator HEARS while MON is on
// (receive-side preview), never the TX audio. Lowering it also lowers the
// loop gain on boards whose headphone output couples back into the mic input.
// Stored on the radio (/api/tx/monitor-level); the first mounted control
// hydrates the shared tx-store value once.

import { useCallback, useEffect, useRef } from 'react';
import { fetchTxMonitorLevel, setTxMonitorLevel } from '../api/client';
import { useTxStore } from '../state/tx-store';
import { useLiveSlider } from '../hooks/useLiveSlider';

export const MON_LEVEL_MIN = -40;
export const MON_LEVEL_MAX = 0;

let hydrateInFlight = false;

export function useTxMonitorLevel(): {
  db: number;
  push: (db: number) => void;
  flush: () => void;
} {
  const db = useTxStore((s) => s.txMonitorLevelDb);
  const loaded = useTxStore((s) => s.txMonitorLevelLoaded);
  const setDb = useTxStore((s) => s.setTxMonitorLevelDb);
  const previousOnError = useRef<number>(db);

  useEffect(() => {
    if (loaded || hydrateInFlight) return;
    hydrateInFlight = true;
    fetchTxMonitorLevel()
      .then((r) => {
        previousOnError.current = r.db;
        setDb(r.db, true);
      })
      .catch(() => {
        /* keep the default; the next mount retries */
      })
      .finally(() => {
        hydrateInFlight = false;
      });
  }, [loaded, setDb]);

  const liveSlider = useLiveSlider<number>({
    send: useCallback(
      (v: number, signal: AbortSignal) => {
        const prevValue = previousOnError.current;
        return setTxMonitorLevel(v, signal)
          .then((r) => {
            if (signal.aborted) return;
            previousOnError.current = r.db;
            setDb(r.db, true);
          })
          .catch((err) => {
            if (signal.aborted) return;
            if (err instanceof DOMException && err.name === 'AbortError') return;
            setDb(prevValue);
          });
      },
      [setDb],
    ),
  });

  const push = useCallback(
    (v: number) => {
      const clamped = Math.min(MON_LEVEL_MAX, Math.max(MON_LEVEL_MIN, v));
      setDb(clamped);
      liveSlider.push(Math.round(clamped));
    },
    [setDb, liveSlider],
  );

  return { db, push, flush: liveSlider.flush };
}

export function MonLevelSlider() {
  const { db, push, flush } = useTxMonitorLevel();

  const onInput = (e: React.FormEvent<HTMLInputElement>) => {
    push(Number(e.currentTarget.value));
  };

  return (
    <label
      className="knob-group"
      title="MON level — how loud you hear your TX chain while MON is on. Listening only; your transmitted audio is not changed."
    >
      <span className="label-xs">MON</span>
      <input
        type="range"
        min={MON_LEVEL_MIN}
        max={MON_LEVEL_MAX}
        step={0.5}
        value={db}
        onInput={onInput}
        onChange={onInput}
        onMouseUp={flush}
        onTouchEnd={flush}
        onKeyUp={flush}
        style={{ flex: 1, cursor: 'pointer', accentColor: 'var(--accent)' }}
      />
      <span className="mono" style={{ width: 52, textAlign: 'right', color: 'var(--fg-1)', fontSize: 11 }}>
        {Math.round(db)} dB
      </span>
    </label>
  );
}

/** Compact row for popovers that style their own label / value cells
 *  (the G2 pane popover). Same shared value and wire path as the slider. */
export function MonLevelRow(props: {
  rowStyle?: React.CSSProperties;
  labelStyle?: React.CSSProperties;
  valueStyle?: React.CSSProperties;
}) {
  const { db, push, flush } = useTxMonitorLevel();
  return (
    <label style={props.rowStyle}>
      <span style={props.labelStyle}>MON</span>
      <input
        type="range"
        min={MON_LEVEL_MIN}
        max={MON_LEVEL_MAX}
        step={1}
        value={db}
        style={{ flex: 1, minWidth: 0, width: '100%' }}
        onChange={(e) => push(Number(e.target.value))}
        onMouseUp={flush}
        onTouchEnd={flush}
        onKeyUp={flush}
      />
      <span style={props.valueStyle}>{Math.round(db)} dB</span>
    </label>
  );
}
