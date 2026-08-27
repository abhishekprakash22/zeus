// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// In-app password gate for remote (WebRTC) RX monitoring. Rendered only in
// remote mode (?remote=<CALLSIGN>). Prompts for the session password using the
// project's in-app dialog chrome (NEVER window.prompt — hard project rule),
// connects via the broker, and once the SPAKE2+ handshake unlocks it marks the
// display store connected and unmounts itself so the normal UI takes over.
// Connection errors (wrong password, radio offline, broker unreachable) surface
// inline with a retry.

import { useCallback, useEffect, useRef, useState } from 'react';
import { ConfirmDialog } from '../layout/ConfirmDialog';
import { useDisplayStore } from '../state/display-store';
import { useTxStore } from '../state/tx-store';
import { getRemoteCallsign, startRemoteClient } from './remote-client';
import { RemoteLinkChip } from './RemoteLinkChip';
import { RemoteTxSourceHint } from './RemoteTxSourceHint';
import type { RemoteConnection } from './connect';

type Phase = 'prompt' | 'connecting' | 'connected';

const pwKey = (callsign: string) => `zeus.remote.pw.${callsign}`;

export function RemoteGate() {
  const callsign = getRemoteCallsign() ?? '';
  const [password, setPassword] = useState('');
  const [phase, setPhase] = useState<Phase>('prompt');
  const [error, setError] = useState<string | null>(null);
  const [stage, setStage] = useState<string | null>(null);
  const [remember, setRemember] = useState(false);

  // Remembered password (field request #2): opt-in, per device+callsign,
  // 7-day expiry, saved only after a SUCCESSFUL unlock (never a guess), and
  // cleared the moment a connect fails with a wrong password. The manual
  // states the tradeoff: whoever holds this device can key the transmitter.
  // A valid remembered password should not just pre-fill the field — it
  // should take the operator straight in (field: 'asked for the password
  // every time I open a new page'). One attempt per mount; a failure lands
  // back on the prompt with the stored copy already cleared by the error
  // path, so a password changed on the radio can never loop.
  const [autoConnect, setAutoConnect] = useState(false);
  useEffect(() => {
    try {
      const raw = localStorage.getItem(pwKey(callsign));
      if (!raw) return;
      const j = JSON.parse(raw) as { pw?: string; exp?: number };
      if (j.pw && typeof j.exp === 'number' && Date.now() < j.exp) {
        setPassword(j.pw);
        setRemember(true);
        setAutoConnect(true);
      } else localStorage.removeItem(pwKey(callsign));
    } catch { /* storage unavailable — prompt as usual */ }
  }, [callsign]);
  const inputRef = useRef<HTMLInputElement | null>(null);
  const connRef = useRef<RemoteConnection | null>(null);

  // Focus the password field once the dialog's own focus trap settles.
  useEffect(() => {
    if (phase !== 'prompt') return;
    const t = setTimeout(() => inputRef.current?.focus(), 0);
    return () => clearTimeout(t);
  }, [phase]);

  // Tear the WebRTC session down if the gate unmounts mid-flight.
  useEffect(() => () => connRef.current?.close(), []);

  const connect = useCallback(() => {
    const pw = password;
    if (!pw) return;
    setError(null);
    setStage('Contacting the broker\u2026');
    setPhase('connecting');
    startRemoteClient(callsign, pw, (st) => setStage(st))
      .then((conn) => {
        try {
          if (remember) {
            localStorage.setItem(
              pwKey(callsign),
              JSON.stringify({ pw, exp: Date.now() + 7 * 24 * 3600 * 1000 }),
            );
          } else localStorage.removeItem(pwKey(callsign));
        } catch { /* storage unavailable */ }
        connRef.current = conn;
        // Flip the panadapter/UI to "connected" — display frames now arrive
        // over WebRTC and dispatchServerFrame feeds the same stores.
        useDisplayStore.getState().setConnected(true);
        setPhase('connected');
        // Connection-death watchdog (field: the UI froze forever after a
        // Wi-Fi hiccup). 'failed' is terminal — recover immediately.
        // 'disconnected' often self-heals across a roam, so give ICE a
        // grace window before declaring the session lost. Recovery returns
        // to the password prompt with the password still filled, one click
        // from reconnecting.
        const pc = conn.pc;
        let graceTimer: ReturnType<typeof setTimeout> | null = null;
        const lost = () => {
          if (graceTimer) { clearTimeout(graceTimer); graceTimer = null; }
          pc.removeEventListener('connectionstatechange', onState);
          conn.api.removeEventListener('close', onChannelClose);
          conn.control.removeEventListener('close', onChannelClose);
          try { connRef.current?.close(); } catch { /* already down */ }
          connRef.current = null;
          useDisplayStore.getState().setConnected(false);
          // The host un-keys a dropped session (TX lease + Close() dead-man);
          // mirror that here so the UI doesn't show a lit MOX — and so a
          // reconnect can never start with a stale key-down to re-assert.
          const tx = useTxStore.getState();
          if (tx.moxOn) tx.setMoxOn(false);
          if (tx.tunOn) tx.setTunOn(false);
          if (tx.localMicArmed) tx.setLocalMicArmed(false);
          setError('Connection lost — press Connect to resume.');
          setPhase('prompt');
        };
        // Zombie-session guard (field find: MON/mixer/band silently dead while
        // audio kept playing). The api and control channels can die
        // independently of the peer connection — an oversized message closes
        // just the channel. A session without its control paths is lost even
        // though media still flows: recover to the prompt instead of leaving
        // a radio you can hear but not command.
        const onChannelClose = () => lost();
        const onState = () => {
          if (pc.connectionState === 'failed') lost();
          else if (pc.connectionState === 'disconnected') {
            if (graceTimer == null) graceTimer = setTimeout(lost, 10_000);
          } else if (pc.connectionState === 'connected' && graceTimer != null) {
            clearTimeout(graceTimer);
            graceTimer = null;
          }
        };
        pc.addEventListener('connectionstatechange', onState);
        conn.api.addEventListener('close', onChannelClose);
        conn.control.addEventListener('close', onChannelClose);
      })
      .catch((err: unknown) => {
        const msg = err instanceof Error ? err.message : 'Connection failed.';
        if (/incorrect password/i.test(msg)) {
          try { localStorage.removeItem(pwKey(callsign)); } catch { /* ok */ }
        }
        setError(msg);
        setStage(null);
        setPhase('prompt');
      });
  }, [callsign, password]);

  useEffect(() => {
    if (autoConnect && phase === 'prompt' && password) {
      setAutoConnect(false);
      connect();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- fire once when the restore lands
  }, [autoConnect, password]);

  // Once unlocked the gate steps aside — only the link-quality chip remains.
  if (phase === 'connected')
    return (
      <>
        <RemoteLinkChip conn={connRef.current} />
        <RemoteTxSourceHint />
      </>
    );

  const connecting = phase === 'connecting';

  return (
    <ConfirmDialog
      title={`Remote · ${callsign}`}
      intent="primary"
      confirmLabel={connecting ? 'Connecting…' : 'Connect'}
      cancelLabel="Cancel"
      onCancel={() => {
        // No local app to fall back to in remote mode; closing the tab is the
        // operator's exit. Keep the gate up so they can retry.
        setError(null);
      }}
      onConfirm={connect}
    >
      <p>Enter the session password to monitor {callsign}'s radio.</p>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          if (!connecting) connect();
        }}
      >
        <input
          ref={inputRef}
          type="password"
          className="mono"
          autoComplete="current-password"
          placeholder="Session password"
          value={password}
          disabled={connecting}
          onChange={(e) => setPassword(e.currentTarget.value)}
          style={{
            width: '100%',
            padding: '6px 8px',
            borderRadius: 'var(--r-sm)',
            border: '1px solid var(--line-strong)',
            background: '#0c0c10',
            color: '#d8d8dc',
            fontSize: 13,
            outline: 'none',
          }}
        />
      </form>
      <label style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 8, fontSize: 12, color: 'var(--fg-2, #9aa3ad)', cursor: 'pointer' }}>
        <input
          type="checkbox"
          checked={remember}
          disabled={connecting}
          onChange={(e) => setRemember(e.currentTarget.checked)}
        />
        Remember on this device for 7 days
      </label>
      {connecting && stage && (
        <p aria-live="polite" style={{ marginTop: 8, fontSize: 12, color: 'var(--fg-2, #9aa3ad)' }}>
          {stage}
        </p>
      )}
      {error && (
        <p role="alert" style={{ color: 'var(--tx)', marginTop: 8 }}>
          {error}
        </p>
      )}
    </ConfirmDialog>
  );
}
