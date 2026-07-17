// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Eye, EyeOff, Puzzle, Search, Settings, X } from 'lucide-react';
import { LogbookWorkspace } from '../../components/design/LogbookWorkspace';
import { useLoggerStore } from '../../state/logger-store';
import { logbookPluginUnavailableReason, useLogbookPluginStore } from '../../state/logbook-plugin-store';
import { useLayoutStore } from '../../state/layout-store';
import { useQrzStore } from '../../state/qrz-store';
import { useWorkspace } from '../WorkspaceContext';
import {
  getLotwStatus,
  saveLotwCredentials,
  saveLotwSettings,
  syncLotwConfirmations,
  uploadLotwEntries,
  type LotwStatus,
} from '../../api/lotw';

const QRZ_HINT_KEY = 'zeus.logbook.qrzHintDismissed';

function readHintDismissed(): boolean {
  try { return localStorage.getItem(QRZ_HINT_KEY) === '1'; }
  catch { return false; }
}

function LogbookSettingsPopover({
  anchor,
  onClose,
}: {
  anchor: DOMRect;
  onClose: () => void;
}) {
  const popRef = useRef<HTMLDivElement | null>(null);
  const qrzConnected = useQrzStore((s) => s.connected);
  const qrzCall = useQrzStore((s) => s.home?.callsign ?? null);
  const openSettings = useLayoutStore((s) => s.setSettingsView);
  const selectedIds = useLoggerStore((s) => s.selectedIds);
  const refreshEntries = useLoggerStore((s) => s.refreshEntries);
  const [status, setStatus] = useState<LotwStatus | null>(null);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [stationLocation, setStationLocation] = useState('');
  const [rig, setRig] = useState('');
  const [antenna, setAntenna] = useState('');
  const [power, setPower] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    void getLotwStatus().then((s) => {
      if (!alive) return;
      setStatus(s);
      setStationLocation(s.stationLocation ?? '');
      setRig(s.stationDefaults.rig ?? '');
      setAntenna(s.stationDefaults.antenna ?? '');
      setPower(s.stationDefaults.txPowerW != null ? String(s.stationDefaults.txPowerW) : '');
    }).catch((err) => alive && setMessage(err instanceof Error ? err.message : 'Could not load LoTW status'));
    return () => { alive = false; };
  }, []);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    const onDown = (event: MouseEvent) => {
      const target = event.target;
      if (target instanceof Node && popRef.current?.contains(target)) return;
      onClose();
    };
    document.addEventListener('keydown', onKey);
    document.addEventListener('mousedown', onDown);
    return () => {
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('mousedown', onDown);
    };
  }, [onClose]);

  const left = Math.max(8, Math.min(window.innerWidth - 380, anchor.right - 360));
  const top = Math.max(8, Math.min(window.innerHeight - 520, anchor.bottom + 8));

  const saveLotw = async () => {
    setBusy(true);
    setMessage(null);
    try {
      if ((username.trim() || password) && (!username.trim() || !password)) {
        throw new Error('LoTW username and password are both required to update credentials');
      }
      const defaults = await saveLotwSettings({
        stationLocation,
        stationDefaults: {
          rig: rig.trim() || null,
          antenna: antenna.trim() || null,
          txPowerW: power.trim() ? Number(power) : null,
        },
      });
      let next = defaults;
      if (username.trim() || password) {
        next = await saveLotwCredentials({
          username: username.trim() || null,
          password: password || null,
          stationLocation,
        });
      }
      setStatus({ ...next, stationDefaults: defaults.stationDefaults });
      setUsername('');
      setPassword('');
      setMessage('LoTW settings saved');
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'Could not save LoTW settings');
    } finally {
      setBusy(false);
    }
  };

  const syncNow = async () => {
    setBusy(true);
    setMessage(null);
    try {
      const result = await syncLotwConfirmations();
      setMessage(result.message);
      setStatus((s) => s ? { ...s, lastSyncUtc: result.lastSyncUtc, lastQslSince: result.lastQslSince, lastResult: result.message } : s);
      await refreshEntries();
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'Confirmation sync failed');
    } finally {
      setBusy(false);
    }
  };

  const upload = async () => {
    setBusy(true);
    setMessage(null);
    try {
      const ids = [...selectedIds];
      const result = await uploadLotwEntries(ids.length ? ids : null);
      setMessage(result.message);
      await refreshEntries();
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'LoTW upload failed');
    } finally {
      setBusy(false);
    }
  };

  return createPortal(
    <div
      ref={popRef}
      className="logbook-settings-popover"
      style={{ left, top }}
      role="dialog"
      aria-label="Logbook Settings"
    >
      <div className="lbsp-head">
        <strong>Logbook Settings</strong>
        <button type="button" onClick={onClose} aria-label="Close logbook settings"><X size={14} /></button>
      </div>
      <section className="lbsp-section">
        <h4>QRZ</h4>
        {qrzConnected ? (
          <p><span className="lbsp-ok">Connected</span> as <strong>{qrzCall}</strong>. The logbook uses the QRZ key from Settings → QRZ.</p>
        ) : (
          <>
            <p>Connect your QRZ account for callsign lookups, log publishing and the globe.</p>
            <button type="button" className="btn sm active" onClick={() => { openSettings(true, 'qrz'); onClose(); }}>
              Open QRZ Settings
            </button>
          </>
        )}
      </section>
      <section className="lbsp-section">
        <h4>LoTW</h4>
        <label>Username<input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" /></label>
        <label>Password<input value={password} onChange={(e) => setPassword(e.target.value)} type="password" autoComplete="current-password" placeholder={status?.configured ? 'Stored' : ''} /></label>
        <label>TQSL Station Location<input value={stationLocation} onChange={(e) => setStationLocation(e.target.value)} /></label>
        <div className="lbsp-actions">
          <button type="button" className="btn sm active" disabled={busy} onClick={saveLotw}>Save</button>
          <button type="button" className="btn sm" disabled={busy} onClick={syncNow}>Sync confirmations now</button>
          <button type="button" className="btn sm" disabled={busy} onClick={upload}>Upload new QSOs</button>
        </div>
        <p className="lbsp-muted">
          Last sync {status?.lastSyncUtc ? new Date(status.lastSyncUtc).toLocaleString() : 'never'}.
          {status?.tqslPath ? ` TQSL: ${status.tqslPath}` : ' TQSL not found; upload will export ADIF for manual signing.'}
        </p>
      </section>
      <section className="lbsp-section">
        <h4>Station defaults</h4>
        <label>Rig<input value={rig} onChange={(e) => setRig(e.target.value)} /></label>
        <label>Antenna<input value={antenna} onChange={(e) => setAntenna(e.target.value)} /></label>
        <label>TX power W<input value={power} onChange={(e) => setPower(e.target.value)} inputMode="decimal" /></label>
      </section>
      {message && <div className="lbsp-message">{message}</div>}
    </div>,
    document.body,
  );
}

export function LogbookPanel() {
  const { logbookActions } = useWorkspace();
  const pluginReady = useLogbookPluginStore((s) => s.installed && s.live);
  const loadCapabilities = useLoggerStore((s) => s.loadCapabilities);
  const qrzConnected = useQrzStore((s) => s.connected);
  const openSettings = useLayoutStore((s) => s.setSettingsView);
  const [searchText, setSearchText] = useState('');
  const [hideQrzPublished, setHideQrzPublished] = useState(false);
  const [settingsAnchor, setSettingsAnchor] = useState<DOMRect | null>(null);
  const [hintDismissed, setHintDismissed] = useState(readHintDismissed);
  const unavailableReason = logbookPluginUnavailableReason();
  const qrzPublishedCount = useLoggerStore((s) =>
    s.entries.reduce((count, entry) => count + (entry.qrzLogId || entry.qrzUploadedUtc ? 1 : 0), 0),
  );
  const query = searchText.trim();
  const qrzPublishedLabel = qrzPublishedCount === 1
    ? '1 QRZ-published QSO'
    : `${qrzPublishedCount} QRZ-published QSOs`;
  const qrzToggleTitle = hideQrzPublished
    ? `Show ${qrzPublishedLabel}`
    : qrzPublishedCount > 0
      ? `Hide ${qrzPublishedLabel}`
      : 'No QRZ-published QSOs to hide';
  const QrzVisibilityIcon = hideQrzPublished ? EyeOff : Eye;

  useEffect(() => {
    if (pluginReady) void loadCapabilities();
  }, [loadCapabilities, pluginReady]);

  return (
    <div className="logbook-panel">
      <div className="logbook-actions">
        <div className="log-search">
          <Search size={14} strokeWidth={2} aria-hidden="true" />
          <input
            type="search"
            value={searchText}
            onChange={(event) => setSearchText(event.target.value)}
            placeholder="Search logbook"
            aria-label="Search logbook"
            disabled={!pluginReady}
            spellCheck={false}
          />
          {query && (
            <button
              type="button"
              className="log-search-clear"
              onClick={() => setSearchText('')}
              aria-label="Clear logbook search"
              title="Clear search"
            >
              <X size={13} strokeWidth={2.2} aria-hidden="true" />
            </button>
          )}
        </div>
        <button
          type="button"
          className={`btn ghost sm logbook-visibility-toggle ${hideQrzPublished ? 'active' : ''}`}
          onClick={() => setHideQrzPublished((value) => !value)}
          disabled={!pluginReady || (qrzPublishedCount === 0 && !hideQrzPublished)}
          aria-label={qrzToggleTitle}
          aria-pressed={hideQrzPublished}
          title={qrzToggleTitle}
        >
          <QrzVisibilityIcon size={14} strokeWidth={2.2} aria-hidden="true" />
        </button>
        <button
          type="button"
          className="btn ghost sm logbook-settings-toggle"
          disabled={!pluginReady}
          onClick={(event) => setSettingsAnchor(event.currentTarget.getBoundingClientRect())}
          aria-label="Logbook settings"
          title="Logbook settings"
        >
          <Settings size={14} strokeWidth={2.2} aria-hidden="true" />
        </button>
        {logbookActions}
      </div>
      <div className="logbook-panel-body">
        {pluginReady ? (
          <>
            {!qrzConnected && !hintDismissed && (
              <div className="logbook-qrz-hint">
                <span>Connect QRZ for callsign lookups, log publishing and the globe.</span>
                <button type="button" className="btn sm active" onClick={() => openSettings(true, 'qrz')}>QRZ Settings</button>
                <button
                  type="button"
                  className="logbook-qrz-hint-x"
                  aria-label="Dismiss QRZ hint"
                  onClick={() => {
                    setHintDismissed(true);
                    try { localStorage.setItem(QRZ_HINT_KEY, '1'); } catch { /* ignore */ }
                  }}
                >
                  <X size={13} />
                </button>
              </div>
            )}
            <LogbookWorkspace searchText={searchText} hideQrzPublished={hideQrzPublished} />
          </>
        ) : (
          <div className="workspace-unavailable-panel" style={{ height: '100%' }}>
            <div className="workspace-unavailable-panel-icon" aria-hidden>
              <Puzzle size={18} />
            </div>
            <div className="workspace-unavailable-panel-copy">
              <div className="workspace-unavailable-panel-title">Logbook unavailable</div>
              <p>{unavailableReason}</p>
            </div>
          </div>
        )}
      </div>
      {settingsAnchor && (
        <LogbookSettingsPopover anchor={settingsAnchor} onClose={() => setSettingsAnchor(null)} />
      )}
    </div>
  );
}
