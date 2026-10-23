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
// Settings → Updates → Earlier versions. Roll back to the version the last
// update replaced (kept on the radio — instant, no download), or install any
// recent release. Both restart the radio through the same watchdog as an
// update: if the chosen build never comes up, the radio returns to this one.
// At the radio only; remote sessions see the list but cannot install.

import { useState, type CSSProperties } from 'react';
import {
  fetchReleaseList,
  postInstallRelease,
  postRollBack,
  type ReleaseListDto,
  type UpdateApplyStatusDto,
} from '../api/client';
import { isRemoteMode } from '../remote/remote-client';
import { rideApply } from './update-apply-ride';

type Pending = { kind: 'rollback' } | { kind: 'install'; tag: string; version: string } | null;

export function EarlierVersionsSection() {
  const remote = isRemoteMode();
  const [list, setList] = useState<ReleaseListDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<Pending>(null);
  const [apply, setApply] = useState<UpdateApplyStatusDto | null>(null);
  const busy = apply !== null;

  const load = async () => {
    setLoading(true);
    setError(null);
    try {
      const l = await fetchReleaseList();
      setList(l);
      if (l.error) setError(l.error);
    } catch {
      setError("Couldn't reach the radio.");
    } finally {
      setLoading(false);
    }
  };

  const start = async (p: NonNullable<Pending>) => {
    setConfirm(null);
    setError(null);
    try {
      const { ok, status } = p.kind === 'rollback' ? await postRollBack() : await postInstallRelease(p.tag);
      if (!ok) {
        setError(status.error ?? 'It could not start.');
        return;
      }
      setApply(status);
      await rideApply({
        onStatus: setApply,
        onFailed: (st) => {
          setApply(null);
          setError(st.error ?? 'It failed.');
        },
        onRestarting: () => setApply({ phase: 'restarting', percent: 100, targetVersion: null, error: null }),
      });
    } catch {
      setApply(null);
      setError("Couldn't reach the radio.");
    }
  };

  const confirmText =
    confirm?.kind === 'rollback'
      ? `Roll back to ${list?.previousVersion ?? 'the previous version'}?`
      : confirm?.kind === 'install'
        ? `Install ${confirm.version}?`
        : '';

  return (
    <div style={wrap} data-testid="earlier-versions">
      <div style={head}>EARLIER VERSIONS</div>
      <p style={hint}>
        Go back if an update misbehaves. The radio restarts on the chosen version; if it doesn&apos;t come up, it
        returns to this one by itself. Settings only newer versions understand are kept and come back when you update
        again.
      </p>

      {remote && <div style={hint}>Earlier versions are installed at the radio.</div>}

      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
        {!remote && list?.canRollBack && (
          <button
            type="button"
            className="btn sm"
            disabled={busy}
            onClick={() => setConfirm({ kind: 'rollback' })}
            title="Swap back to the version the last update replaced — kept on the radio, no download"
          >
            ROLL BACK TO {list.previousVersion ?? 'PREVIOUS'}
          </button>
        )}
        <button type="button" className="btn sm" disabled={loading || busy} onClick={() => void load()}>
          {loading ? 'LOADING…' : list ? 'REFRESH LIST' : 'SHOW EARLIER VERSIONS'}
        </button>
      </div>

      {confirm && (
        <div style={confirmBox} role="alertdialog" aria-label={confirmText}>
          <span>{confirmText} ANAN Core restarts on that version.</span>
          <button type="button" className="btn sm active" onClick={() => void start(confirm)}>
            CONFIRM
          </button>
          <button type="button" className="btn sm" onClick={() => setConfirm(null)}>
            CANCEL
          </button>
        </div>
      )}

      {apply && (
        <div style={hint} role="status">
          {apply.phase === 'downloading'
            ? `downloading ${apply.targetVersion ?? ''}… ${apply.percent.toFixed(0)}%`
            : apply.phase === 'restarting'
              ? 'restarting ANAN Core… this page will reload'
              : `${apply.phase}…`}
        </div>
      )}
      {error && (
        <div style={{ ...hint, color: 'var(--warn, #f0c08a)' }} role="status">
          {error}
        </div>
      )}

      {list && list.releases.length > 0 && (
        <div style={table}>
          {list.releases.map((r) => {
            const date = r.publishedAt ? new Date(r.publishedAt).toLocaleDateString() : '';
            const canInstallThis = !remote && list.canInstall && !!r.manifestUrl && !r.isInstalled;
            return (
              <div key={r.tag} style={row} data-testid={`release-${r.tag}`}>
                <span style={{ fontFamily: 'var(--font-mono, monospace)', minWidth: 64 }}>{r.version}</span>
                <span style={{ ...hint, minWidth: 90 }}>{date}</span>
                <span style={badge}>{r.isInstalled ? 'installed' : r.isOlder ? 'older' : 'newer'}</span>
                <span style={{ flex: 1 }} />
                {canInstallThis ? (
                  <button
                    type="button"
                    className="btn sm"
                    disabled={busy}
                    onClick={() => setConfirm({ kind: 'install', tag: r.tag, version: r.version })}
                  >
                    INSTALL
                  </button>
                ) : (
                  !r.isInstalled &&
                  r.pageUrl && (
                    <button
                      type="button"
                      className="btn sm"
                      onClick={() => window.open(r.pageUrl!, '_blank', 'noopener,noreferrer')}
                    >
                      RELEASE PAGE
                    </button>
                  )
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

const wrap: CSSProperties = { display: 'flex', flexDirection: 'column', gap: 8, marginTop: 14 };
const head: CSSProperties = { fontSize: 10, letterSpacing: '0.08em', color: 'var(--fg-3, #8a94a3)' };
const hint: CSSProperties = { fontSize: 12, lineHeight: 1.45, color: 'var(--fg-3, #8a94a3)', margin: 0 };
const confirmBox: CSSProperties = {
  display: 'flex',
  gap: 8,
  alignItems: 'center',
  flexWrap: 'wrap',
  padding: '8px 10px',
  border: '1px solid var(--line-soft, #2a3442)',
  borderRadius: 6,
  fontSize: 12.5,
};
const table: CSSProperties = { display: 'flex', flexDirection: 'column', gap: 4, maxHeight: 240, overflowY: 'auto' };
const row: CSSProperties = { display: 'flex', alignItems: 'center', gap: 10, minHeight: 36, fontSize: 13 };
const badge: CSSProperties = {
  fontSize: 10,
  letterSpacing: '0.06em',
  padding: '1px 6px',
  borderRadius: 4,
  border: '1px solid var(--line-soft, #2a3442)',
  color: 'var(--fg-2, #9aa6b3)',
};
