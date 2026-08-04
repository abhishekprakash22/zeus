// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// DeepCW waterfall presentation (single-channel): the concept-mockup modes,
// scoped to the one signal phase 1 decodes — the tuned RX. A callout card is
// pinned at the VFO's on-screen position with the transcript tail streaming
// through it, and each freshly decoded character also "rides" downward from
// the callout, fading with age — the inline-text mode, approximated with a
// CSS drift rather than hard-locking to the waterfall's row rate. The
// per-frequency multi-station lanes arrive with the Pi-side skimmer.

import { useSyncExternalStore } from 'react';
import { useCwDecodeStore } from '../state/cw-decode-store';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';

const TAIL_CHARS = 26;

/** Cheap subscription to the pan geometry we need (center + span). */
function usePanGeometry(): { centerHz: number; spanHz: number } {
  return useSyncExternalStore(
    (cb) => useDisplayStore.subscribe(cb),
    () => {
      const s = useDisplayStore.getState();
      const spanHz = s.panDb && s.hzPerPixel > 0 ? s.panDb.length * s.hzPerPixel : 0;
      return geomCache(Number(s.centerHz), spanHz);
    },
  );
}
let lastGeom = { centerHz: 0, spanHz: 0 };
function geomCache(centerHz: number, spanHz: number) {
  if (lastGeom.centerHz !== centerHz || lastGeom.spanHz !== spanHz)
    lastGeom = { centerHz, spanHz };
  return lastGeom;
}

export function CwDecodeWaterfallOverlay() {
  const enabled = useCwDecodeStore((s) => s.enabled);
  const overlayEnabled = useCwDecodeStore((s) => s.overlayEnabled);
  const status = useCwDecodeStore((s) => s.status);
  const transcript = useCwDecodeStore((s) => s.transcript);
  const lastCharAt = useCwDecodeStore((s) => s.lastCharAt);
  const setPanelOpen = useCwDecodeStore((s) => s.setPanelOpen);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const { centerHz, spanHz } = usePanGeometry();

  if (!enabled || !overlayEnabled || spanHz <= 0) return null;
  const frac = (vfoHz - (centerHz - spanHz / 2)) / spanHz;
  if (frac < 0.02 || frac > 0.98) return null; // tuned signal off-screen

  const leftPct = frac * 100;
  const fresh = Date.now() - lastCharAt < 1500;
  const tail = transcript.slice(-TAIL_CHARS);

  return (
    <div className="cwdec-ovl" aria-hidden>
      <div
        className={`cwdec-callout ${fresh ? 'fresh' : ''}`}
        style={{ left: `${leftPct}%` }}
        onClick={() => setPanelOpen(true)}
        title="Neural CW decode of the tuned signal — click for the full transcript"
      >
        <div className="cwdec-callout-head">
          <span className={`cwdec-callout-dot ${status === 'running' ? 'on' : ''}`} />
          <span>CW⌁ DECODE</span>
        </div>
        <div className="cwdec-callout-txt">
          <bdi>{tail || '\u00a0'}</bdi>
        </div>
        <span className="cwdec-callout-tail" />
      </div>
      <div className="cwdec-ride" style={{ left: `${leftPct}%` }}>
              </div>
    </div>
  );
}


/** Mode B, correctly homed (field report: chars fell out of the pan and
 *  vanished behind the waterfall canvas). This layer mounts INSIDE
 *  WaterfallSurface, so inset:0 is exactly the waterfall for every renderer
 *  (WebGL and the WebGPU heightfield) and every layout (single, grid,
 *  stitched): decoded characters are born at the top of the waterfall at the
 *  tuned signal's x-position and ride down OVER the scroll, as designed. */
export function CwDecodeDriftLayer({ receiver }: { receiver?: number }) {
  const enabled = useCwDecodeStore((s) => s.enabled);
  const overlayEnabled = useCwDecodeStore((s) => s.overlayEnabled);
  const recentChars = useCwDecodeStore((s) => s.recentChars);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const { centerHz, spanHz } = usePanGeometry();

  if ((receiver ?? 0) !== 0) return null;
  if (!enabled || !overlayEnabled || spanHz <= 0) return null;
  const frac = (vfoHz - (centerHz - spanHz / 2)) / spanHz;
  if (frac < 0.02 || frac > 0.98) return null;

  return (
    <div className="cwdec-ovl cwdec-drift-layer" aria-hidden>
      {recentChars.map((c) =>
        c.ch === ' ' ? null : (
          <span
            key={c.id}
            className="cwdec-fall"
            style={{ left: `calc(${frac * 100}% + 10px)` }}
          >
            {c.ch}
          </span>
        ),
      )}
    </div>
  );
}
