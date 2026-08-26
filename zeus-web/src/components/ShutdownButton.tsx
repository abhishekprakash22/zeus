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
import { getSessionPassword, isRemoteMode } from '../remote/remote-client';
/** Clean Pi power-off from the connect popup — the appliance's last rite.
 * House two-press discipline (SHUT DOWN -> SURE?, self-disarming after 10 s
 * — 3 s proved too short on the touchscreen for the second tap to land)
 * because this button turns the station off; the server refuses while TX is
 * keyed. After acceptance the UI announces the shutdown and stops — the next
 * thing this screen shows is the boot after the power button. */
export function ShutdownButton() {
  const [armed, setArmed] = useState(false);
  const [state, setState] = useState<'idle' | 'down' | 'error'>('idle');
  const [err, setErr] = useState('');
  // Remote power-off guard (field request): from a remote session, arming
  // SHUT DOWN additionally demands the session password re-typed, under a
  // warning that nobody without physical access can turn the radio back on.
  // The tunnel is already authenticated — this is informed consent against
  // fat fingers and lent devices, not extra cryptography.
  const remote = isRemoteMode();
  const [pwEntry, setPwEntry] = useState('');

  useEffect(() => {
    if (!armed) return;
    const id = window.setTimeout(() => { setArmed(false); setPwEntry(''); }, remote ? 30000 : 10000);
    return () => window.clearTimeout(id);
  }, [armed, remote]);

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
          if (remote) {
            const expected = getSessionPassword();
            if (!expected || pwEntry !== expected) {
              setErr('password required to shut down remotely');
              setState('error');
              return;
            }
          }
          setArmed(false);
          setPwEntry('');
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
      {armed && remote && (
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, marginLeft: 6 }}>
          <input
            type="password"
            className="mono"
            placeholder="Session password"
            aria-label="Session password to confirm remote shutdown"
            value={pwEntry}
            onChange={(e) => { setPwEntry(e.currentTarget.value); if (state === 'error') setState('idle'); }}
            style={{ width: 130, padding: '3px 6px', fontSize: 11, borderRadius: 4, border: '1px solid var(--line-strong)', background: '#0c0c10', color: '#d8d8dc' }}
          />
          <span style={{ fontSize: 10, color: 'var(--tx)', maxWidth: 260 }}>
            Powers the radio OFF — unless someone has physical access to it, it cannot be switched back on remotely.
          </span>
        </span>
      )}
    </>
  );
}
