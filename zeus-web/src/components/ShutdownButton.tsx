// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Shared by the in-app radio selector AND the pre-connect launcher
// (ConnectPanel) — the operator ends sessions from both places.

import { useEffect, useState } from 'react';
import { shutdownPi } from '../api/client';
/** Clean Pi power-off from the connect popup — the appliance's last rite.
 * House two-press discipline (SHUT DOWN -> SURE?, self-disarming after 3 s)
 * because this button turns the station off; the server refuses while TX is
 * keyed. After acceptance the UI announces the shutdown and stops — the next
 * thing this screen shows is the boot after the power button. */
export function ShutdownButton() {
  const [armed, setArmed] = useState(false);
  const [state, setState] = useState<'idle' | 'down' | 'error'>('idle');
  const [err, setErr] = useState('');

  useEffect(() => {
    if (!armed) return;
    const id = window.setTimeout(() => setArmed(false), 3000);
    return () => window.clearTimeout(id);
  }, [armed]);

  if (state === 'down')
    return <span style={{ fontSize: 10, color: 'var(--tx)' }}>· shutting down — safe to power off when the screen goes dark</span>;

  return (
    <>
      {state === 'error' && (
        <span style={{ fontSize: 10, color: 'var(--tx)' }}>· {err}</span>
      )}
      <button
        type="button"
        className={`btn ghost shutdown-btn ${armed ? 'armed' : ''}`}
        title="Shut down the radio's Raspberry Pi cleanly. Two presses: arm, then power off. Refused while transmitting."
        onClick={() => {
          if (!armed) {
            setArmed(true);
            return;
          }
          setArmed(false);
          void shutdownPi()
            .then((r) => {
              if (r.ok) setState('down');
              else {
                setErr(r.error ?? 'refused');
                setState('error');
              }
            })
            .catch(() => {
              setErr('request failed');
              setState('error');
            });
        }}
      >
        {armed ? 'SURE?' : '⏻ SHUT DOWN'}
      </button>
    </>
  );
}
