// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// SSTV tuning aid on the RX1 panadapter: while SSTV is engaged, mark where
// the SSTV tones land for the current dial — 1200 Hz sync, 1500 Hz black,
// 2300 Hz white — and shade the 1500–2300 Hz picture band. On USB/DIGU the
// audio tone f sits at dial + f; on LSB/DIGL at dial − f. A signal whose sync
// trace sits on the SYNC line is tuned right; the per-picture VIS offset in
// the SSTV window says by how much it isn't.

import { useSyncExternalStore } from 'react';
import { useSstvStore } from '../state/sstv-store';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';

const MARKS = [
  { hz: 1200, label: 'SYNC' },
  { hz: 1500, label: 'BLK' },
  { hz: 2300, label: 'WHT' },
] as const;

let lastGeom = { centerHz: 0, spanHz: 0 };
function usePanGeometry(): { centerHz: number; spanHz: number } {
  return useSyncExternalStore(
    (cb) => useDisplayStore.subscribe(cb),
    () => {
      const s = useDisplayStore.getState();
      const spanHz = s.panDb && s.hzPerPixel > 0 ? s.panDb.length * s.hzPerPixel : 0;
      const centerHz = Number(s.centerHz);
      if (lastGeom.centerHz !== centerHz || lastGeom.spanHz !== spanHz)
        lastGeom = { centerHz, spanHz };
      return lastGeom;
    },
  );
}

/** Screen fraction (0..1) of an RF frequency, or null off-screen. */
function fracOf(hz: number, centerHz: number, spanHz: number): number | null {
  const f = (hz - (centerHz - spanHz / 2)) / spanHz;
  return f < 0 || f > 1 ? null : f;
}

export function SstvTuneOverlay() {
  const engaged = useSstvStore((s) => s.panelOpen);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const mode = useConnectionStore((s) => s.mode);
  const { centerHz, spanHz } = usePanGeometry();

  if (!engaged || spanHz <= 0) return null;
  const sign = mode === 'LSB' || mode === 'DIGL' ? -1 : mode === 'USB' || mode === 'DIGU' ? 1 : 0;
  if (sign === 0) return null; // not on a sideband — no meaningful tone map

  const band = [vfoHz + sign * 1500, vfoHz + sign * 2300].map((hz) =>
    fracOf(hz, centerHz, spanHz),
  );
  const bandLeft = band[0] != null && band[1] != null ? Math.min(band[0], band[1]) : null;
  const bandWidth = band[0] != null && band[1] != null ? Math.abs(band[1] - band[0]) : null;

  return (
    <div className="sstv-tune-ovl" aria-hidden>
      {bandLeft != null && bandWidth != null && (
        <div
          className="sstv-tune-band"
          style={{ left: `${bandLeft * 100}%`, width: `${bandWidth * 100}%` }}
        />
      )}
      {MARKS.map((m) => {
        const f = fracOf(vfoHz + sign * m.hz, centerHz, spanHz);
        if (f == null) return null;
        return (
          <div key={m.hz} className={`sstv-tune-mark ${m.label === 'SYNC' ? 'sync' : ''}`} style={{ left: `${f * 100}%` }}>
            <span className="sstv-tune-label">{m.label}</span>
          </div>
        );
      })}
    </div>
  );
}
