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
// What hover-to-listen is playing, on RX1's panadapter: a marker line at the
// previewed frequency and a badge saying so (the main audio is ducked under
// it, so the operator should never wonder what they are hearing). When the
// pointer is somewhere the radio can't preview, the badge says why instead.

import { useEffect } from 'react';
import { useDisplayStore } from '../state/display-store';
import { formatDialHz } from '../components/radial/radial-items';
import { useHoverListenStore } from './hover-listen';

export function HoverListenBadge() {
  const enabled = useHoverListenStore((s) => s.enabled);
  const activeHz = useHoverListenStore((s) => s.activeHz);
  const reason = useHoverListenStore((s) => s.reason);
  const centerHz = Number(useDisplayStore((s) => s.centerHz));
  const hzPerPixel = useDisplayStore((s) => s.hzPerPixel);
  const bins = useDisplayStore((s) => s.panDb?.length ?? 0);

  useEffect(() => {
    void useHoverListenStore.getState().hydrate();
  }, []);

  if (!enabled || (activeHz == null && !reason)) return null;

  let linePct: number | null = null;
  if (activeHz != null && bins > 0 && hzPerPixel > 0) {
    const spanHz = bins * hzPerPixel;
    const pct = ((activeHz - (centerHz - spanHz / 2)) / spanHz) * 100;
    if (pct >= 0 && pct <= 100) linePct = pct;
  }

  return (
    <>
      {linePct != null && <div className="hl-line" style={{ left: `${linePct}%` }} aria-hidden />}
      <div className={`hl-badge${activeHz == null ? ' hl-badge--off' : ''}`} role="status">
        {activeHz != null ? (
          <>
            <span className="hl-dot" aria-hidden />
            LISTENING <span className="hl-hz">{formatDialHz(activeHz)}</span>
            <span className="hl-hint">click to tune</span>
          </>
        ) : (
          <>No preview — {reason}</>
        )}
      </div>
    </>
  );
}
