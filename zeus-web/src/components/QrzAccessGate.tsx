// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { useEffect, useState, type FormEvent } from 'react';
import { KeyRound, LogOut, RefreshCw, ShieldCheck } from 'lucide-react';
import { useQrzStore } from '../state/qrz-store';
import { useUserAccessStore } from '../state/user-access-store';
import type { ZeusUserSession } from '../api/users';

function statusValue(value: string | null | undefined): string {
  return value && value.trim().length > 0 ? value : 'none';
}

function AccessRows({ session }: { session: ZeusUserSession | null }) {
  const qrzConnected = useQrzStore((s) => s.connected);
  const qrzHome = useQrzStore((s) => s.home);
  const qrzHasXml = useQrzStore((s) => s.hasXmlSubscription);

  return (
    <div className="auth-status-grid">
      <div>
        <span>QRZ session</span>
        <strong>{qrzConnected || session?.qrzConnected ? 'signed in' : 'required'}</strong>
      </div>
      <div>
        <span>QRZ username</span>
        <strong>{statusValue(session?.callsign ?? qrzHome?.callsign)}</strong>
      </div>
      <div>
        <span>App access</span>
        <strong>{session?.accessAllowed ? 'allowed' : 'blocked'}</strong>
      </div>
      <div>
        <span>Role</span>
        <strong>{session?.isAdmin ? 'admin' : 'operator'}</strong>
      </div>
      <div>
        <span>Subscription</span>
        <strong>{statusValue(session?.subscriptionStatus)}</strong>
      </div>
      <div>
        <span>QRZ XML</span>
        <strong>{qrzHasXml || session?.hasQrzXmlSubscription ? 'active' : 'basic'}</strong>
      </div>
    </div>
  );
}

export function QrzAccessGate({ adminMode = false }: { adminMode?: boolean }) {
  const rememberedUsername = useQrzStore((s) => s.rememberedUsername);
  const loginInFlight = useQrzStore((s) => s.loginInFlight);
  const loginError = useQrzStore((s) => s.loginError);
  const connected = useQrzStore((s) => s.connected);
  const qrzHome = useQrzStore((s) => s.home);
  const session = useUserAccessStore((s) => s.session);
  const checked = useUserAccessStore((s) => s.checked);
  const loading = useUserAccessStore((s) => s.loading);
  const accessError = useUserAccessStore((s) => s.error);
  const refreshSession = useUserAccessStore((s) => s.refreshSession);
  const loginWithQrz = useUserAccessStore((s) => s.loginWithQrz);
  const logout = useUserAccessStore((s) => s.logout);

  const [username, setUsername] = useState(rememberedUsername);
  const [password, setPassword] = useState('');

  useEffect(() => {
    void refreshSession();
  }, [refreshSession]);

  useEffect(() => {
    if (!username && rememberedUsername) setUsername(rememberedUsername);
  }, [rememberedUsername, username]);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    const ok = await loginWithQrz(username.trim(), password);
    if (ok) setPassword('');
  }

  const busy = loginInFlight || loading;
  const denial = session?.denialReason ?? accessError ?? loginError;
  const qrzSignedIn = connected || session?.qrzConnected === true;
  const qrzUsername = statusValue(session?.callsign ?? qrzHome?.callsign);
  const title = qrzSignedIn
    ? adminMode ? 'Admin QRZ Session' : 'QRZ Account Active'
    : adminMode ? 'Admin QRZ Login' : 'QRZ Login Required';
  const subtitle = qrzSignedIn
    ? `${qrzUsername} is the Zeus username.`
    : 'QRZ callsign is the Zeus username.';

  return (
    <div className="auth-shell">
      <section className="auth-dialog" aria-labelledby="qrz-auth-title">
        <div className="auth-dialog-head">
          <div className="auth-dialog-icon" aria-hidden>
            <KeyRound size={22} strokeWidth={2.2} />
          </div>
          <div>
            <h1 id="qrz-auth-title">{title}</h1>
            <p>{subtitle}</p>
          </div>
        </div>

        <AccessRows session={session} />

        {!checked && (
          <div className="auth-banner auth-banner--muted">Checking stored QRZ session...</div>
        )}
        {denial && (
          <div className="auth-banner auth-banner--warn">{denial}</div>
        )}

        {qrzSignedIn ? (
          <div className="auth-actions">
            <button
              type="button"
              className="btn sm active"
              onClick={() => void refreshSession()}
              disabled={busy}
            >
              <RefreshCw size={14} strokeWidth={2.2} aria-hidden />
              {busy ? 'CHECKING' : 'REFRESH ACCESS'}
            </button>
            <button
              type="button"
              className="btn sm"
              onClick={() => void logout()}
              disabled={busy}
            >
              <LogOut size={14} strokeWidth={2.2} aria-hidden />
              SIGN OUT
            </button>
          </div>
        ) : (
          <form className="auth-form" onSubmit={onSubmit}>
            <label>
              <span>QRZ username</span>
              <input
                value={username}
                onChange={(e) => setUsername(e.target.value.toUpperCase())}
                autoComplete="username"
                spellCheck={false}
                disabled={busy}
              />
            </label>
            <label>
              <span>Password</span>
              <input
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoComplete="current-password"
                disabled={busy}
              />
            </label>
            <div className="auth-actions">
              <button
                type="submit"
                className="btn sm active"
                disabled={busy || !username.trim() || !password}
              >
                <ShieldCheck size={14} strokeWidth={2.2} aria-hidden />
                {busy ? 'SIGNING IN' : 'SIGN IN'}
              </button>
            </div>
          </form>
        )}
      </section>
    </div>
  );
}
