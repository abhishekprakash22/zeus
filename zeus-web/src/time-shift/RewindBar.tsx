// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// The rewind bar on RX1's panadapter (shown while rewind is switched on):
// a timeline of what the radio is holding, with LIVE at the right. Drag it
// back — or use the step keys — to hear the past; RX1's dial, mode and filter
// still work, so you can tune around inside the recorded spectrum. While a
// replay plays, a badge says so plainly: the audio is not live.

import { useEffect, useRef, useState } from 'react';
import { formatDelay, useTimeShiftStore } from './time-shift';

const SEEK_DEBOUNCE_MS = 120;

export function RewindBar() {
  const status = useTimeShiftStore((s) => s.status);
  const replay = useTimeShiftStore((s) => s.replay);
  const nudge = useTimeShiftStore((s) => s.nudge);
  const pause = useTimeShiftStore((s) => s.pause);
  const live = useTimeShiftStore((s) => s.live);
  // While the operator drags, the slider shows their value, not the radio's.
  const [drag, setDrag] = useState<number | null>(null);
  const seekTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    void useTimeShiftStore.getState().hydrate();
    return () => {
      if (seekTimer.current) clearTimeout(seekTimer.current);
    };
  }, []);

  if (!status.enabled) return null;

  const held = Math.max(0, status.bufferedSeconds);
  const delay = drag ?? (status.replaying ? Math.min(status.delaySeconds, held) : 0);
  const canRewind = held >= 1;

  const seek = (seconds: number) => {
    setDrag(seconds);
    if (seekTimer.current) clearTimeout(seekTimer.current);
    seekTimer.current = setTimeout(() => {
      seekTimer.current = null;
      const done = () => setDrag(null);
      if (seconds <= 0.05) void live().then(done);
      else void replay(seconds).then(done);
    }, SEEK_DEBOUNCE_MS);
  };

  return (
    <>
      {status.replaying && (
        <div className={`rw-badge${status.paused ? ' rw-badge--paused' : ''}`} role="status">
          <span className="rw-dot" aria-hidden />
          {status.paused ? 'REPLAY PAUSED' : 'REPLAY'} <span className="rw-time">{formatDelay(status.delaySeconds)}</span>
          <span className="rw-note">
            {status.outOfWindow ? 'this frequency was not being received then' : 'not live'}
          </span>
        </div>
      )}
      <div className="rw-bar" aria-label="Rewind">
        <button
          type="button"
          className="rw-btn"
          disabled={!canRewind}
          title="Back 10 seconds"
          onClick={() => void nudge(10)}
        >
          ⏪ 10
        </button>
        <button
          type="button"
          className="rw-btn"
          disabled={!status.replaying}
          title={status.paused ? 'Resume the replay' : 'Pause the replay'}
          aria-pressed={status.paused}
          onClick={() => void pause(!status.paused)}
        >
          {status.paused ? '▶' : '⏸'}
        </button>
        <input
          className="rw-slider"
          type="range"
          min={0}
          max={Math.max(1, held)}
          step={0.5}
          // Right end = live, left end = the oldest audio held.
          value={Math.max(1, held) - delay}
          disabled={!canRewind}
          aria-label="Replay position"
          aria-valuetext={formatDelay(delay)}
          onChange={(e) => seek(Math.max(1, held) - Number(e.currentTarget.value))}
        />
        <span className="rw-read" title={`${held.toFixed(0)} s held of ${status.capacitySeconds.toFixed(0)} s`}>
          {status.replaying || drag != null ? formatDelay(delay) : `${held.toFixed(0)} s`}
        </span>
        <button
          type="button"
          className="rw-btn"
          disabled={!status.replaying}
          title="Forward 10 seconds"
          onClick={() => void nudge(-10)}
        >
          10 ⏩
        </button>
        <button
          type="button"
          className={`rw-btn rw-live${status.replaying ? '' : ' rw-live--on'}`}
          disabled={!status.replaying}
          title="Back to live reception"
          onClick={() => void live()}
        >
          LIVE
        </button>
      </div>
    </>
  );
}
