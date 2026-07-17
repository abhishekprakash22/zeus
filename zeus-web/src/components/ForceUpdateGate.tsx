// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { Download } from 'lucide-react';
import type { RepoUpdateStatus } from '../api/client';
import { openExternalUrl } from './report-problem/openExternalUrl';

const DOWNLOADS_PAGE = 'https://openhpsdrzeus.com/download';

function updateUrl(status: RepoUpdateStatus): string | null {
  return status.releaseDownloadUrl ?? status.releaseUrl;
}

function reasonText(status: RepoUpdateStatus): string {
  if (status.forceReason === 'downgrade') {
    return 'A newer Zeus version was previously installed on this machine. Install the current release before continuing.';
  }
  return 'This Zeus build is below the minimum supported version for the current release channel.';
}

export function ForceUpdateGate({ status }: { status: RepoUpdateStatus }) {
  const url = updateUrl(status);
  const required = status.minVersion ?? status.latestVersion ?? 'latest';

  return (
    <main
      aria-labelledby="force-update-title"
      style={{
        minHeight: '100vh',
        width: '100%',
        display: 'grid',
        placeItems: 'center',
        padding: 24,
        background: 'var(--bg-app)',
        color: 'var(--fg-1)',
      }}
    >
      <section
        style={{
          width: 'min(560px, 100%)',
          display: 'flex',
          flexDirection: 'column',
          gap: 18,
          padding: 24,
          borderRadius: 'var(--r-lg)',
          border: '1px solid var(--panel-border)',
          background: 'var(--panel-top)',
          boxShadow: 'var(--panel-shadow)',
        }}
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <div
            style={{
              fontSize: 11,
              fontWeight: 700,
              letterSpacing: '0.12em',
              textTransform: 'uppercase',
              color: 'var(--power)',
            }}
          >
            UPDATE REQUIRED
          </div>
          <h1
            id="force-update-title"
            style={{ margin: 0, fontSize: 24, lineHeight: 1.1, color: 'var(--fg-0)' }}
          >
            Install Zeus {required}
          </h1>
          <p style={{ margin: 0, fontSize: 13, lineHeight: 1.5, color: 'var(--fg-2)' }}>
            {reasonText(status)}
          </p>
        </div>

        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'minmax(92px, auto) 1fr',
            gap: '8px 14px',
            padding: 12,
            borderRadius: 'var(--r-md)',
            background: 'var(--bg-inset)',
            border: '1px solid var(--panel-border)',
            fontSize: 12,
          }}
        >
          <span style={{ color: 'var(--fg-3)', fontWeight: 700 }}>Current</span>
          <span style={{ color: 'var(--fg-1)', fontFamily: 'monospace' }}>
            {status.installedVersion ?? 'unknown'}
          </span>
          <span style={{ color: 'var(--fg-3)', fontWeight: 700 }}>Required</span>
          <span style={{ color: 'var(--fg-1)', fontFamily: 'monospace' }}>{required}</span>
        </div>

        <button
          type="button"
          className="btn sm active"
          onClick={() => openExternalUrl(url ?? DOWNLOADS_PAGE)}
          style={{ alignSelf: 'flex-start', minHeight: 34 }}
        >
          <Download size={14} strokeWidth={2.2} aria-hidden />
          UPDATE NOW
        </button>
        {!url && (
          <p style={{ margin: 0, fontSize: 12, lineHeight: 1.5, color: 'var(--fg-3)' }}>
            If the button does not open a page, browse to{' '}
            <span style={{ fontFamily: 'monospace', color: 'var(--fg-1)', userSelect: 'all' }}>
              {DOWNLOADS_PAGE}
            </span>{' '}
            from any device and install the current release.
          </p>
        )}
      </section>
    </main>
  );
}
