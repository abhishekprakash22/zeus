// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { useCallback } from 'react';
import { setRogerBeep } from '../api/client';
import { useTxStore } from '../state/tx-store';

export function RogerBeepButton() {
  const enabled = useTxStore((s) => s.rogerBeepEnabled);
  const setEnabled = useTxStore((s) => s.setRogerBeepEnabled);

  const click = useCallback(() => {
    const next = !enabled;
    setEnabled(next);
    setRogerBeep(next)
      .then((r) => setEnabled(r.rogerBeepEnabled))
      .catch(() => setEnabled(!next));
  }, [enabled, setEnabled]);

  return (
    <button
      type="button"
      onClick={click}
      className={`btn sm ${enabled ? 'active' : ''}`}
      title={enabled ? 'Roger beep on - sends a short tone when PTT releases' : 'Roger beep off'}
      aria-pressed={enabled}
      aria-label={enabled ? 'Disable roger beep' : 'Enable roger beep'}
    >
      <span className={`led ${enabled ? 'on' : ''}`} style={{ marginRight: 6 }} />
      {enabled ? 'ON' : 'OFF'}
    </button>
  );
}
