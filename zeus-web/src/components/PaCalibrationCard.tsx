// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// PaCalibrationCard — the wizard face of /api/pa-cal. One band at a time,
// by design: the MEASURE button calibrates the band the radio is tuned to,
// using short TUN bursts at the operator's OWN reduced drive (the server
// normalizes by drive²), and the operator sequences bands with the band
// buttons they already know. Abort always available; Revert restores every
// gain the session touched.

import { useCallback, useEffect, useState } from 'react';

type CalRow = {
  band: string;
  beforeGainDb: number;
  targetWatts: number;
  measuredWatts: number | null;
  proposedGainDb: number | null;
  status: string;
};

type CalStatus = {
  phase: string;
  board: string;
  targetWatts: number;
  rows: CalRow[];
  lastFwdWatts: number | null;
  lastSwr: number | null;
  error: string | null;
};

async function post(path: string, body?: unknown): Promise<Response> {
  return fetch(path, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

export function PaCalibrationCard() {
  const [status, setStatus] = useState<CalStatus | null>(null);
  const [loadConfirmed, setLoadConfirmed] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      const r = await fetch('/api/pa-cal/status');
      if (r.ok) setStatus((await r.json()) as CalStatus);
    } catch {
      /* transient — next poll wins */
    }
  }, []);

  useEffect(() => {
    void refresh();
    const running = status?.phase === 'Running';
    const id = window.setInterval(() => void refresh(), running ? 500 : 2000);
    return () => window.clearInterval(id);
  }, [refresh, status?.phase]);

  const running = status?.phase === 'Running';
  const rows = status?.rows ?? [];

  const measure = async () => {
    setActionError(null);
    const r = await post('/api/pa-cal/measure', { confirm: 'i-have-a-rated-dummy-load' });
    if (!r.ok) {
      const body = (await r.json().catch(() => null)) as { error?: string } | null;
      setActionError(body?.error ?? `measure failed (${r.status})`);
    }
    void refresh();
  };

  return (
    <section>
      <div className="mb-2 flex items-center justify-between gap-3">
        <h3 className="pa-section-h">Calibrate — current band</h3>
        {status && (
          <span className="text-xs opacity-70">
            {status.board} · target {status.targetWatts} W · {status.phase}
          </span>
        )}
      </div>
      <div className="pa-card space-y-3 p-3 text-xs">
        <p className="opacity-80">
          Automatically sets this band&apos;s PA gain so 100% drive delivers rated
          power. Up to three short TUN bursts at your <b>current TUN drive</b> —
          set it low (20–30%); the math normalizes to 100%, so the load sees a
          fraction of rated power. Tune into each band and press MEASURE;
          SWR&nbsp;&gt;&nbsp;1.5 or a non-responding PA aborts unkeyed.
        </p>
        <label className="flex items-center gap-2">
          <input
            type="checkbox"
            checked={loadConfirmed}
            onChange={(e) => setLoadConfirmed(e.currentTarget.checked)}
          />
          A dummy load rated for this radio&apos;s full output is connected
        </label>
        <div className="flex items-center gap-2">
          <button
            type="button"
            className="btn sm"
            disabled={!loadConfirmed || running}
            onClick={() => void measure()}
          >
            Measure this band
          </button>
          {running && (
            <button type="button" className="btn sm" onClick={() => void post('/api/pa-cal/abort').then(refresh)}>
              Abort
            </button>
          )}
          {rows.length > 0 && !running && (
            <button
              type="button"
              className="btn sm"
              title="Restore every gain this session changed to its pre-calibration value"
              onClick={() => void post('/api/pa-cal/revert').then(refresh)}
            >
              Revert session
            </button>
          )}
          {running && (
            <span className="opacity-80">
              fwd {status?.lastFwdWatts?.toFixed(1) ?? '—'} W · SWR{' '}
              {status?.lastSwr?.toFixed(2) ?? '—'}
            </span>
          )}
        </div>
        {(actionError ?? status?.error) && (
          <p className="text-red-400">{actionError ?? status?.error}</p>
        )}
        {rows.length > 0 && (
          <table className="w-full text-left">
            <thead className="opacity-60">
              <tr>
                <th>Band</th>
                <th>Before dB</th>
                <th>Measured W</th>
                <th>New dB</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.band}>
                  <td>{r.band}</td>
                  <td>{r.beforeGainDb.toFixed(1)}</td>
                  <td>{r.measuredWatts?.toFixed(1) ?? '—'}</td>
                  <td>{r.proposedGainDb?.toFixed(2) ?? '—'}</td>
                  <td>{r.status}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </section>
  );
}
