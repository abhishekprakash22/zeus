// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// UpdatesPanel — Settings -> Updates. Reports the latest PRODUCTION build from
// the Zeus download domain (downloads.openhpsdrzeus.com, published from `main`
// only) and opens the matching installer / package / AppImage / tarball for this
// platform. Source checkouts see the same domain status; they update their
// source manually with scripts/update.*.

import { useCallback, useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { FpgaFlashSection } from './FpgaFlashSection';
import { isRemoteMode } from '../remote/remote-client';
import { P2AppUpdateSection } from './P2AppUpdateSection';
import {
  fetchUpdateStatus,
  getUpdateApplyStatus,
  postUpdateApply,
  type RepoUpdateStatus,
  type UpdateApplyStatusDto,
} from '../api/client';
import { isApplyActive, rideApply } from './update-apply-ride';

const labelStyle: CSSProperties = { fontSize: 11, fontWeight: 600, letterSpacing: '0.06em', color: 'var(--fg-2)' };
const valueStyle: CSSProperties = { fontSize: 12, color: 'var(--fg-1)', fontFamily: 'monospace' };
const hintStyle: CSSProperties = { fontSize: 10, lineHeight: 1.4, color: 'var(--fg-3)' };
const linkStyle: CSSProperties = { color: 'var(--accent)', textDecoration: 'none' };

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div style={{ display: 'flex', gap: 10, alignItems: 'baseline' }}>
      <span style={{ ...labelStyle, minWidth: 92 }}>{label}</span>
      <span style={valueStyle}>{children}</span>
    </div>
  );
}

function fmtChecked(iso: string | null): string {
  if (!iso) return '';
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return '';
  const secs = Math.max(0, Math.round((Date.now() - t) / 1000));
  if (secs < 60) return `${secs}s ago`;
  const mins = Math.round(secs / 60);
  return mins < 60 ? `${mins}m ago` : `${Math.round(mins / 60)}h ago`;
}

function fmtBytes(bytes: number | null): string {
  if (bytes == null || !Number.isFinite(bytes) || bytes <= 0) return '';
  const mib = bytes / (1024 * 1024);
  return `${mib.toFixed(mib >= 10 ? 0 : 1)} MB`;
}

function openExternal(url: string | null | undefined) {
  if (!url) return;
  window.open(url, '_blank', 'noopener,noreferrer');
}

function statusLabel(status: RepoUpdateStatus): string {
  if (status.forceUpdate) {
    return 'Update required';
  }
  if (status.updateAvailable && status.latestVersion) {
    return `Version ${status.latestVersion} available`;
  }
  if (!status.latestVersion && !status.error) {
    return 'Checking releases';
  }
  if (!status.error) {
    return 'Up to date';
  }
  return 'Status unknown';
}

export function UpdatesPanel() {
  const [status, setStatus] = useState<RepoUpdateStatus | null>(null);
  const [checking, setChecking] = useState(false);
  const [result, setResult] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  // An in-place update restarts the radio's own process — it can only be
  // driven from the radio. The host denies /api/app from a remote session, and
  // a remote client has nothing to reload into afterwards. Declared before the
  // callbacks that close over it.
  const remoteMode = isRemoteMode();
  const [apply, setApply] = useState<UpdateApplyStatusDto | null>(null);
  const [applying, setApplying] = useState(false);

  // One ride at a time, whether we started the install or adopted one that
  // another screen started. Never reset on the reload path — the page dies.
  const riding = useRef(false);

  const followApply = useCallback(async (first: UpdateApplyStatusDto | null) => {
    if (riding.current) return;
    riding.current = true;
    if (first) setApply(first);
    await rideApply({
      onStatus: setApply,
      onFailed: (cur) => {
        riding.current = false;
        setApplying(false);
        setResult(cur.error ?? 'Update failed.');
      },
      onRestarting: () =>
        setApply({ phase: 'restarting', percent: 100, targetVersion: null, error: null }),
    });
    riding.current = false;
  }, []);

  // In-place install driver: kick the server-side apply, poll its phase, and
  // when the server dies for the restart, keep probing until it's back — then
  // hard-reload so the SPA matches the new build.
  const doInstall = useCallback(() => {
    void (async () => {
      setApplying(true);
      setResult(null);
      // Guard the poll loop below: a DENIED apply must never be read as
      // "the server went away to restart". Two missed polls put the UI into
      // a permanent fake 'restarting' phase waiting for a reboot that was
      // never started.
      if (remoteMode) {
        setApplying(false);
        setResult('Updates can only be installed at the radio.');
        return;
      }
      // The offer on THIS screen may be stale: another screen may already
      // have installed this version (and restarted the radio) since our last
      // check. Re-check before acting so a stale button never re-runs a
      // finished install. A failed re-check falls through — the server-side
      // apply fails properly on its own if something is really wrong.
      let fresh: RepoUpdateStatus | null = null;
      try {
        fresh = await fetchUpdateStatus(true);
      } catch {
        /* status unavailable — let the apply itself decide */
      }
      if (fresh) {
        setStatus(fresh);
        if (!fresh.updateAvailable && !fresh.forceUpdate) {
          setApplying(false);
          setResult('Already up to date — this update was installed from another screen.');
          return;
        }
      }
      try {
        const { ok, status: st } = await postUpdateApply();
        setApply(st);
        if (!ok) {
          setApplying(false);
          setResult(st.error ?? 'In-place update is not available on this install.');
          return;
        }
        await followApply(null);
      } catch (err) {
        setApplying(false);
        setResult(String(err));
      }
    })();
  }, [remoteMode, followApply]);

  // Another screen may have started the install — the server runs at most one
  // apply, so an active phase seen here is that same run. Adopt it: show the
  // same progress line, disable the button, and ride through the restart to
  // the reload, instead of offering a second INSTALL that races the first.
  // Checked on mount and on a slow poll while the panel is open.
  useEffect(() => {
    if (remoteMode) return;
    let disposed = false;
    const adoptIfRunning = async () => {
      if (disposed || riding.current) return;
      try {
        const st = await getUpdateApplyStatus();
        if (disposed || riding.current || !isApplyActive(st.phase)) return;
        setApplying(true);
        setResult(null);
        await followApply(st);
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
  }, [remoteMode, followApply]);

  const check = useCallback(async (fetch: boolean) => {
    setChecking(true);
    setLoadError(null);
    try {
      setStatus(await fetchUpdateStatus(fetch));
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : 'Failed to query update status');
    } finally {
      setChecking(false);
    }
  }, []);

  // Quick local-only read on mount (no network), then a fetch in the background
  // so the panel paints instantly but still reflects the real release state.
  useEffect(() => {
    void (async () => {
      await check(false);
      await check(true);
    })();
  }, [check]);

  const doUpdate = () => {
    if (!status) return;
    const url = status.releaseDownloadUrl ?? status.releaseUrl;
    openExternal(url);
    setResult(
      url
        ? status.releaseDownloadUrl
          ? `Opened ${status.releaseAssetName ?? 'the latest ANAN Core download'}.`
          : 'Opened the ANAN Core downloads page.'
        : 'No download is available for this platform.',
    );
  };

  const action = status?.updateAction ?? 'none';
  const updateAvailable = status?.updateAvailable ?? false;
  const canUpdate = (action === 'download' || action === 'openRelease') && !remoteMode;
  const assetSize = fmtBytes(status?.releaseAssetSizeBytes ?? null);

  return (
    <div style={{ maxWidth: 640 }}>
      <h3
        style={{
          margin: '0 0 14px',
          fontSize: 11,
          fontWeight: 700,
          letterSpacing: '0.12em',
          textTransform: 'uppercase',
          color: 'var(--fg-2)',
        }}
      >
        SOFTWARE UPDATES
      </h3>

      {loadError && (
        <div style={{ fontSize: 12, color: 'var(--tx)', marginBottom: 12 }}>{loadError}</div>
      )}

      {status && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
          <section style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            <Row label="Installed">{status.installedVersion ?? 'unknown'}</Row>
            <Row label="Latest">{status.latestVersion ?? (checking ? 'checking...' : '-')}</Row>
            {status.minVersion && <Row label="Required">{status.minVersion}</Row>}
            <Row label="Platform">
              {(status.runtimePlatform ?? 'unknown')}/{status.runtimeArchitecture ?? 'unknown'}
            </Row>
            {status.releaseUrl && (
              <Row label="Release">
                <a href={status.releaseUrl} target="_blank" rel="noreferrer" style={linkStyle}>
                  {status.releaseName ?? 'ANAN Core downloads'}
                </a>
              </Row>
            )}
            {status.releaseAssetName && (
              <Row label="Download">
                {status.releaseAssetName}
                {assetSize && (
                  <span style={{ color: 'var(--fg-3)', fontFamily: 'inherit' }}> - {assetSize}</span>
                )}
              </Row>
            )}
          </section>

          {status.isGitRepo && (
            <div style={hintStyle}>
              Running from source{status.branch ? ` — ${status.branch}` : ''}
              {status.currentShortSha ? ` @ ${status.currentShortSha}` : ''}. Update the source
              with <code>scripts/update.ps1</code> (Windows) or <code>scripts/update.sh</code>{' '}
              (macOS/Linux), then restart ANAN Core.
            </div>
          )}

          <section
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 10,
              padding: '10px 12px',
              borderRadius: 'var(--r-sm)',
              border: '1px solid var(--panel-border)',
              background: 'var(--panel-top)',
            }}
          >
            <span
              style={{
                fontSize: 13,
                fontWeight: 700,
                color: status.forceUpdate || updateAvailable ? 'var(--power)' : 'var(--fg-1)',
              }}
            >
              {statusLabel(status)}
            </span>
            {status.checkedUtc && (
              <span style={{ ...hintStyle, marginLeft: 'auto' }}>checked {fmtChecked(status.checkedUtc)}</span>
            )}
          </section>

          {status.error && (
            <div style={{ fontSize: 11, color: 'var(--tx)' }}>{status.error}</div>
          )}

          {status.forceUpdate && (
            <div style={{ fontSize: 11, color: 'var(--tx)', lineHeight: 1.5 }}>
              {status.forceReason === 'downgrade'
                ? 'A newer ANAN Core version was previously installed on this machine.'
                : 'This ANAN Core build is below the minimum supported version.'}
            </div>
          )}

          <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <button
              type="button"
              className="btn sm"
              disabled={checking}
              onClick={() => void check(true)}
            >
              {checking ? 'CHECKING...' : 'CHECK FOR UPDATES'}
            </button>
            {remoteMode ? (
              <span style={{ fontSize: 11, opacity: 0.85 }}>
                Updates are installed at the radio.
              </span>
            ) : (
              <button
                type="button"
                className="btn sm active"
                disabled={!canUpdate || checking || applying}
                onClick={() => (action === 'download' ? doInstall() : doUpdate())}
                title={
                  action === 'download'
                    ? 'Download, verify, and install the update in place, then restart ANAN Core'
                    : action === 'openRelease'
                      ? 'Open the ANAN Core downloads page'
                      : 'Already up to date'
                }
              >
                {applying ? 'INSTALLING...' : action === 'download' ? 'INSTALL & RESTART' : 'UPDATE NOW'}
              </button>
            )}
            {apply && applying && (
              <span style={{ fontSize: 11, opacity: 0.85 }}>
                {apply.phase === 'downloading'
                  ? `downloading… ${apply.percent.toFixed(0)}%`
                  : apply.phase === 'restarting'
                    ? 'restarting ANAN Core… this page will reload'
                    : `${apply.phase}…`}
              </span>
            )}
          </div>

          {result && (
            <div style={{ fontSize: 12, color: 'var(--fg-1)', lineHeight: 1.5 }}>{result}</div>
          )}

          <div style={hintStyle}>
            Update now opens the latest installer or package for this platform. Run the downloaded
            update and restart ANAN Core to apply it.
          </div>
        </div>
      )}

      <P2AppUpdateSection />
      <FpgaFlashSection />
    </div>
  );
}
