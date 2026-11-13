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
// Memory channels drawn on the RX1 panadapter: a dashed line at each stored
// frequency in view and a small flag with its name. Clicking the flag recalls
// the channel (mode, filter, then frequency) on RX1. Deleting is from the
// radial menu: open it on the marker and choose Forget.

import { useEffect } from 'react';
import { useDisplayStore } from '../../state/display-store';
import { recallMemory, useMemoryStore } from '../../state/memory-store';

export function MemoryMarkerOverlay() {
  const centerHz = useDisplayStore((s) => s.centerHz);
  const hzPerPixel = useDisplayStore((s) => s.hzPerPixel);
  const width = useDisplayStore((s) => s.panDb?.length ?? 0);
  const channels = useMemoryStore((s) => s.channels);

  useEffect(() => {
    void useMemoryStore.getState().hydrate();
  }, []);

  if (!width || hzPerPixel <= 0 || channels.length === 0) return null;
  const spanHz = width * hzPerPixel;
  const startHz = Number(centerHz) - spanHz / 2;
  const visible = channels.filter((m) => m.hz >= startHz && m.hz <= startHz + spanHz);
  if (visible.length === 0) return null;

  return (
    <>
      {visible.map((m) => {
        const pct = ((m.hz - startHz) / spanHz) * 100;
        return (
          <div key={m.id}>
            <div className="mem-marker" style={{ left: `${pct}%` }} aria-hidden />
            <button
              type="button"
              className="mem-flag"
              style={{ left: `${pct}%` }}
              title={`${m.name} — ${(m.hz / 1e6).toFixed(6)} MHz ${m.mode}. Click to recall; right-click the marker for Forget.`}
              onClick={() => void recallMemory(m)}
            >
              ★ {m.name}
            </button>
          </div>
        );
      })}
    </>
  );
}
