// SPDX-License-Identifier: GPL-2.0-or-later
//
// P2AppUpdateSection — Settings -> Updates. The radio's own p2app, managed
// end to end: shows the supervisor's state (Supervised/Adopted/NoBinary/...)
// and offers UPDATE P2APP, which pulls Laurence Barker's Saturn repository
// and rebuilds p2app on the Pi (git pull, make clean, make — the same steps
// as update_saturn_code.sh). Zeus pauses its supervised p2app during the
// build and brings it back after, so the Discover tab's loopback row
// returns on the fresh binary without the operator touching a terminal.
import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import {
  getP2AppStatus,
  getP2AppUpdateStatus,
  postP2AppUpdate,
  type P2AppStatusDto,
  type P2AppUpdateStatusDto,
} from '../api/client';

const labelStyle: CSSProperties = { fontSize: 11, fontWeight: 600, letterSpacing: '0.06em', color: 'var(--fg-2)' };
const valueStyle: CSSProperties = { fontSize: 12, color: 'var(--fg-1)', fontFamily: 'monospace' };
const hintStyle: CSSProperties = { fontSize: 10, lineHeight: 1.4, color: 'var(--fg-3)' };

const ACTIVE_PHASES = ['pausing', 'cloning', 'pulling', 'building'];

export function P2AppUpdateSection() {
  const [sup, setSup] = useState<P2AppStatusDto | null>(null);
  const [upd, setUpd] = useState<P2AppUpdateStatusDto | null>(null);
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const timer = useRef<number | null>(null);

  const refresh = useCallback(async () => {
    try {
      const [s, u] = await Promise.all([getP2AppStatus(), getP2AppUpdateStatus()]);
      setSup(s);
      setUpd(u);
      return u;
    } catch {
      // Older backend without the endpoints — hide the section quietly.
      setSup(null);
      return null;
    }
  }, []);

  useEffect(() => {
    void refresh();
    return () => { if (timer.current != null) window.clearTimeout(timer.current); };
  }, [refresh]);

  const active = upd != null && (upd.running || ACTIVE_PHASES.includes(upd.phase));

  // Poll while a run is active.
  useEffect(() => {
    if (!active) return;
    timer.current = window.setTimeout(() => { void refresh(); }, 1500);
    return () => { if (timer.current != null) window.clearTimeout(timer.current); };
  }, [active, upd, refresh]);

  const start = useCallback(async () => {
    setStarting(true);
    setError(null);
    try {
      await postP2AppUpdate();
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'update failed to start');
    } finally {
      setStarting(false);
    }
  }, [refresh]);

  // Radio host without a supervisor (Windows desktop, old backend): nothing to show.
  if (sup == null || sup.mode === 'Disabled') return null;

  return (
    <section style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 14 }}>
      <div style={{ ...labelStyle, fontSize: 12 }}>P2APP (radio data plane)</div>
      <div style={{ display: 'flex', gap: 10, alignItems: 'baseline' }}>
        <span style={{ ...labelStyle, minWidth: 92 }}>STATUS</span>
        <span style={valueStyle}>
          {sup.mode}
          {sup.pid != null ? ` · pid ${sup.pid}` : ''}
        </span>
      </div>
      {sup.binaryPath && (
        <div style={{ display: 'flex', gap: 10, alignItems: 'baseline' }}>
          <span style={{ ...labelStyle, minWidth: 92 }}>BINARY</span>
          <span style={valueStyle}>{sup.binaryPath}</span>
        </div>
      )}
      {upd?.head && (
        <div style={{ display: 'flex', gap: 10, alignItems: 'baseline' }}>
          <span style={{ ...labelStyle, minWidth: 92 }}>SATURN HEAD</span>
          <span style={valueStyle}>{upd.head}</span>
        </div>
      )}
      <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <button
          type="button"
          className="btn sm active"
          disabled={starting || active}
          onClick={() => void start()}
          title="git pull Laurence Barker's Saturn repository and rebuild p2app on this radio (make clean; make). Zeus pauses its p2app during the build and restarts it after."
        >
          {active ? `${upd?.phase.toUpperCase()}...` : 'UPDATE P2APP'}
        </button>
        {upd?.phase === 'done' && !active && (
          <span style={{ fontSize: 11, color: 'var(--ok, #6c6)' }}>updated ✓</span>
        )}
        {(error || upd?.error) && (
          <span style={{ fontSize: 11, color: 'var(--tx)' }}>{error ?? upd?.error}</span>
        )}
        {upd?.rolledBack && !active && (
          <span style={{ fontSize: 11, color: 'var(--ok, #6c6)' }}>
            previous p2app restored — radio still working
          </span>
        )}
      </div>
      {active && upd && upd.log.length > 0 && (
        <pre
          style={{
            margin: 0, padding: 8, fontSize: 10, lineHeight: 1.5,
            fontFamily: 'monospace', color: 'var(--fg-2)',
            background: 'var(--bg-2, rgba(255,255,255,0.04))',
            borderRadius: 6, maxHeight: 120, overflowY: 'auto',
            whiteSpace: 'pre-wrap', wordBreak: 'break-all',
          }}
        >
          {upd.log.join('\n')}
        </pre>
      )}
      <div style={hintStyle}>
        Builds from github.com/laurencebarker/Saturn — the operator's own checkout when one
        exists, otherwise a Zeus-managed clone. The p2app on this radio is stopped during the
        build and returns automatically.
      </div>
    </section>
  );
}
