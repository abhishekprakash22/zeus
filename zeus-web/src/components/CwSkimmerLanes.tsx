// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// DeepCW skimmer lanes (phase 2) — the concept mockup's mode A, multi-station:
// each channel the Pi-side skimmer is decoding pins a callout to its ABSOLUTE
// frequency on the panadapter, text streaming as the net emits it, staggered
// into two rows so neighbours don't collide. Click a lane to QSY there.
//
// Pitch -> absolute frequency is the demodulator's algebra, owned here because
// the frontend knows VFO/mode/CW-pitch:
//   CWU: abs = vfo + (pitch - cwPitch)     CWL: abs = vfo + (cwPitch - pitch)
//   USB: abs = vfo + pitch                 LSB: abs = vfo - pitch
// Other modes hide the lanes (the skimmer only makes sense on CW-ish audio).

import { setVfo } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useCwSkimStore } from '../state/cw-skim-store';
import { useDisplayStore } from '../state/display-store';

const TAIL_CHARS = 22;

function usePanGeometry(): { centerHz: number; spanHz: number } {
  const centerHz = useDisplayStore((s) => Number(s.centerHz));
  const width = useDisplayStore((s) => s.width);
  const hzPerPixel = useDisplayStore((s) => s.hzPerPixel);
  return { centerHz, spanHz: width > 0 && hzPerPixel > 0 ? width * hzPerPixel : 0 };
}

function pitchToAbsHz(
  mode: string,
  vfoHz: number,
  cwPitchHz: number,
  pitchHz: number,
): number | null {
  switch (mode) {
    case 'CWU':
      return vfoHz + (pitchHz - cwPitchHz);
    case 'CWL':
      return vfoHz + (cwPitchHz - pitchHz);
    case 'USB':
    case 'DIGU':
      return vfoHz + pitchHz;
    case 'LSB':
    case 'DIGL':
      return vfoHz - pitchHz;
    default:
      return null;
  }
}

export function CwSkimmerLanes() {
  const enabled = useCwSkimStore((s) => s.enabled);
  const channels = useCwSkimStore((s) => s.channels);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const mode = useConnectionStore((s) => s.mode);
  const cwPitchHz = useConnectionStore((s) => s.cwPitchHz);
  const { centerHz, spanHz } = usePanGeometry();

  if (!enabled || spanHz <= 0) return null;
  const list = Object.values(channels).filter((c) => c.active);
  if (list.length === 0) return null;

  const now = Date.now();
  return (
    <div className="cwdec-ovl" aria-hidden>
      {list.map((c, i) => {
        const absHz = pitchToAbsHz(mode, vfoHz, cwPitchHz, c.pitchHz);
        if (absHz == null) return null;
        const frac = (absHz - (centerHz - spanHz / 2)) / spanHz;
        if (frac < 0.02 || frac > 0.98) return null;
        const fresh = now - c.lastCharAt < 1500;
        const tail = c.decodable ? c.text.slice(-TAIL_CHARS) : '· outside decode band ·';
        return (
          <div
            key={c.id}
            className={`cwdec-callout cwskim-lane ${fresh ? 'fresh' : ''}`}
            style={{ left: `${frac * 100}%`, top: `${6 + (i % 2) * 46}px` }}
            onClick={() => void setVfo(Math.round(absHz)).catch(() => undefined)}
            title={`CW at ${(absHz / 1e6).toFixed(4)} MHz · ${c.snrDb.toFixed(0)} dB — click to tune`}
          >
            <div className="cwdec-callout-head">
              <span className={`cwdec-callout-dot ${c.decodable ? 'on' : ''}`} />
              <span>{(absHz / 1e3).toFixed(1)} · {c.snrDb.toFixed(0)} dB</span>
            </div>
            <div className="cwdec-callout-text">{tail || '\u00a0'}</div>
            <span className="cwdec-callout-tail" />
          </div>
        );
      })}
    </div>
  );
}
