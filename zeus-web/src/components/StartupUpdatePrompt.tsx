// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useEffect, useRef, useState } from 'react';
import type { RepoUpdateStatus } from '../api/client';
import { fetchUpdateStatus, getUpdateApplyStatus, postUpdateApply } from '../api/client';
import { isApplyActive, rideApply } from './update-apply-ride';

type Props = {
  status: RepoUpdateStatus | null;
  onDismiss: () => void;
  onOpenSettings: () => void;
};

function updateUrl(status: RepoUpdateStatus): string | null {
  return status.releaseDownloadUrl ?? status.releaseUrl;
}

export function StartupUpdatePrompt({ status, onDismiss, onOpenSettings }: Props) {
  const [visible, setVisible] = useState(false);
  const [applying, setApplying] = useState(false);
  const [phaseText, setPhaseText] = useState<string | null>(null);
  // One ride at a time, whether we started the install or adopted one that
  // another screen started. Never reset on the reload path — the page dies.
  const riding = useRef(false);

  useEffect(() => {
    if (!status || status.forceUpdate) return;
    const timer = setTimeout(() => setVisible(true), 100);
    return () => clearTimeout(timer);
  }, [status]);

  // Another screen may have started this install already — the server runs at
  // most one apply, so an active phase seen here is that same run. Adopt it:
  // show its progress instead of offering UPDATE NOW, and ride through the
  // restart to the reload. Checked on mount and on a slow poll while the
  // toast is up.
  useEffect(() => {
    if (!status || status.forceUpdate) return;
    let disposed = false;
    const adoptIfRunning = async () => {
      if (disposed || riding.current) return;
      try {
        const st = await getUpdateApplyStatus();
        if (disposed || riding.current || !isApplyActive(st.phase)) return;
        riding.current = true;
        setApplying(true);
        setPhaseText(`${st.phase}… ${Math.round(st.percent ?? 0)}%`);
        await rideApply({
          onStatus: (cur) => setPhaseText(`${cur.phase}… ${Math.round(cur.percent ?? 0)}%`),
          onFailed: (cur) => {
            riding.current = false;
            setApplying(false);
            setPhaseText(cur.error ?? 'Update failed — see Settings → Updating.');
          },
          onRestarting: () => setPhaseText('restarting…'),
        });
        riding.current = false;
      } catch {
        /* status unavailable — nothing to adopt */
      }
    };
    void adoptIfRunning();
    const t = setInterval(() => void adoptIfRunning(), 3000);
    return () => {
      disposed = true;
      clearInterval(t);
    };
  }, [status]);

  if (!status || status.forceUpdate) return null;

  const url = updateUrl(status);
  const latest = status.latestVersion ?? status.releaseTag ?? 'latest';

  // Field: UPDATE NOW here used to window.open() the release asset — a
  // browser download that never installs anything, while the Updating
  // panel's button did the real in-place apply. The toast now drives the
  // SAME server-side apply (postUpdateApply + poll + reload-when-back),
  // showing its phase inline. The browser-download behaviour survives only
  // as the fallback when in-place apply is unsupported on this install.
  const openUpdate = () => {
    if (url) window.open(url, '_blank', 'noopener,noreferrer');
    onDismiss();
  };

  const installNow = () => {
    if (applying || riding.current) return;
    setApplying(true);
    setPhaseText('starting…');
    void (async () => {
      // The offer on THIS screen may be stale: another screen may already
      // have installed this version (and restarted the radio) since the
      // status behind this toast was fetched. Re-check before acting so a
      // stale UPDATE NOW never re-runs a finished install.
      try {
        const fresh = await fetchUpdateStatus(true);
        if (!fresh.updateAvailable) {
          setApplying(false);
          setPhaseText('Already up to date — this update was installed from another screen.');
          setTimeout(onDismiss, 2500);
          return;
        }
      } catch {
        /* status unavailable — let the apply itself decide */
      }
      try {
        const { ok } = await postUpdateApply();
        if (!ok) {
          // In-place apply unavailable (portable/dev install) — fall back to
          // the old open-the-asset behaviour so the button still helps.
          setApplying(false);
          openUpdate();
          return;
        }
        riding.current = true;
        await rideApply({
          onStatus: (cur) => setPhaseText(`${cur.phase}… ${Math.round(cur.percent ?? 0)}%`),
          onFailed: (cur) => {
            riding.current = false;
            setApplying(false);
            setPhaseText(cur.error ?? 'Update failed — see Settings → Updating.');
          },
          onRestarting: () => setPhaseText('restarting…'),
        });
        riding.current = false;
      } catch {
        setApplying(false);
        setPhaseText('Could not start the update — see Settings → Updating.');
      }
    })();
  };

  return (
    <div
      role="status"
      aria-live="polite"
      style={{
        position: 'fixed',
        top: 16,
        right: 16,
        zIndex: 10000,
        maxWidth: 420,
        width: 'calc(100% - 32px)',
        transform: `translateY(${visible ? '0' : '-120%'})`,
        transition: 'transform 0.25s ease-out',
      }}
    >
      <div
        style={{
          display: 'flex',
          flexDirection: 'column',
          gap: 10,
          padding: 14,
          borderRadius: 'var(--r-md)',
          border: '1px solid var(--panel-border)',
          background: 'var(--panel-top)',
          color: 'var(--fg-1)',
          boxShadow: '0 10px 28px rgba(0, 0, 0, 0.35)',
        }}
      >
        <div>
          <div
            style={{
              fontSize: 11,
              fontWeight: 700,
              letterSpacing: '0.12em',
              textTransform: 'uppercase',
              color: 'var(--power)',
              marginBottom: 4,
            }}
          >
            UPDATE AVAILABLE
          </div>
          <div style={{ fontSize: 13, fontWeight: 700 }}>
            ANAN Core {latest} is ready
          </div>
          <div style={{ fontSize: 11, color: 'var(--fg-3)', lineHeight: 1.4, marginTop: 3 }}>
            Installed {status.installedVersion ?? 'unknown'}
            {status.releaseAssetName ? ` - ${status.releaseAssetName}` : ''}
          </div>
          {phaseText && (
            <div style={{ fontSize: 11, color: 'var(--accent)', lineHeight: 1.4, marginTop: 4 }}>
              {phaseText}
            </div>
          )}
        </div>

        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, flexWrap: 'wrap' }}>
          <button type="button" className="btn sm" onClick={onDismiss} disabled={applying}>
            LATER
          </button>
          <button
            type="button"
            className="btn sm"
            onClick={() => {
              onOpenSettings();
              onDismiss();
            }}
          >
            DETAILS
          </button>
          <button type="button" className="btn sm active" onClick={installNow} disabled={applying}>
            {applying ? 'INSTALLING…' : 'UPDATE NOW'}
          </button>
        </div>
      </div>
    </div>
  );
}
