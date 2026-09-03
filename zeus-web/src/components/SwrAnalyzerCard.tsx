// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// SwrAnalyzerCard — Option B: the full analyzer face. Band picker, points,
// RUN SWEEP, a live-drawing SWR curve with the minimum and 2:1-span
// markers, and the previous sweep as a dashed overlay — the trimming
// workflow: adjust the antenna, re-sweep, watch the dip move.

import { useCallback, useEffect, useMemo, useState } from 'react';

type Pt = { hz: number; swr: number | null };
type Sweep = {
  band: string;
  points: Pt[];
  minSwrHz: number | null;
  minSwr: number | null;
  span2Low: number | null;
  span2High: number | null;
} | null;
type SweepStatus = {
  phase: string;
  band: string | null;
  progress: { done: number; total: number };
  live: Pt[];
  current: Sweep;
  previous: Sweep;
  error: string | null;
  bands: string[];
};

const W = 620;
const H = 220;
const L = 40;
const B = 195;
const TOP = 12;

function toPath(pts: Pt[], f0: number, f1: number, maxSwr: number): string {
  const usable = pts.filter((p) => p.swr !== null);
  if (usable.length === 0) return '';
  const x = (hz: number) => L + ((hz - f0) / (f1 - f0)) * (W - L - 10);
  const y = (s: number) => B - (Math.min(s, maxSwr) - 1) * ((B - TOP) / (maxSwr - 1));
  return usable
    .map((p, i) => `${i === 0 ? 'M' : 'L'}${x(p.hz).toFixed(1)} ${y(p.swr as number).toFixed(1)}`)
    .join(' ');
}

export function SwrAnalyzerCard() {
  const [status, setStatus] = useState<SweepStatus | null>(null);
  const [band, setBand] = useState<string>('');
  const [points, setPoints] = useState(80);
  const [confirmed, setConfirmed] = useState(false);
  const [showPrev, setShowPrev] = useState(true);
  const [actionError, setActionError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      const r = await fetch('/api/swr-sweep/status');
      if (r.ok) setStatus((await r.json()) as SweepStatus);
    } catch {
      /* next poll */
    }
  }, []);

  useEffect(() => {
    void refresh();
    const running = status?.phase === 'Running';
    const id = window.setInterval(() => void refresh(), running ? 400 : 3000);
    return () => window.clearInterval(id);
  }, [refresh, status?.phase]);

  const running = status?.phase === 'Running';
  const cur = status?.current ?? null;
  const prev = status?.previous ?? null;
  const livePts = running ? status?.live ?? [] : cur?.points ?? [];

  const domain = useMemo((): [number, number] | null => {
    const src = livePts.length > 0 ? livePts : cur?.points ?? [];
    const first = src[0];
    const last = src[src.length - 1];
    if (!first || !last) return null;
    return [first.hz, last.hz];
  }, [livePts, cur]);

  const run = async () => {
    setActionError(null);
    const r = await fetch('/api/swr-sweep/start', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ confirm: 'antenna-connected', band: band || undefined, points }),
    });
    if (!r.ok) {
      const body = (await r.json().catch(() => null)) as { error?: string } | null;
      setActionError(body?.error ?? `sweep failed (${r.status})`);
    }
    void refresh();
  };

  const maxSwr = 5;
  const gy = (s: number) => B - (s - 1) * ((B - TOP) / (maxSwr - 1));
  const gx = (hz: number) =>
    domain ? L + ((hz - domain[0]) / (domain[1] - domain[0])) * (W - L - 10) : L;

  return (
    <section>
      <div className="mb-2 flex items-center justify-between gap-3">
        <h3 className="pa-section-h">SWR analyzer</h3>
        {status && (
          <span className="text-xs opacity-70">
            {status.phase}
            {running ? ` · ${status.progress.done}/${status.progress.total}` : ''}
          </span>
        )}
      </div>
      <div className="pa-card space-y-3 p-3 text-xs">
        <p className="opacity-80">
          Sweeps a low-power TUN carrier across one band and plots SWR from the
          radio&apos;s own bridge. Set TUN drive for roughly <b>2–5 W</b> (the
          bridge needs &gt;2 W to read true). In-band only, ~15–25 s per sweep,
          the VFO returns where you left it. This <b>transmits into the antenna
          under test</b>.
        </p>
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.currentTarget.checked)} />
          The antenna (or load) under test is connected
        </label>
        <div className="flex flex-wrap items-center gap-2">
          <select value={band} onChange={(e) => setBand(e.currentTarget.value)}>
            <option value="">Current band</option>
            {(status?.bands ?? []).map((b) => (
              <option key={b} value={b}>{b}</option>
            ))}
          </select>
          <label className="flex items-center gap-1">
            points
            <input
              type="number"
              min={20}
              max={150}
              value={points}
              style={{ width: 64 }}
              onChange={(e) => setPoints(Number(e.currentTarget.value) || 80)}
            />
          </label>
          <button type="button" className="btn sm" disabled={!confirmed || running} onClick={() => void run()}>
            Run sweep
          </button>
          {running && (
            <button type="button" className="btn sm" onClick={() => void fetch('/api/swr-sweep/abort', { method: 'POST' }).then(refresh)}>
              Abort
            </button>
          )}
          {prev && (
            <label className="flex items-center gap-1">
              <input type="checkbox" checked={showPrev} onChange={(e) => setShowPrev(e.currentTarget.checked)} />
              compare last
            </label>
          )}
        </div>
        {(actionError ?? status?.error) && <p className="text-red-400">{actionError ?? status?.error}</p>}
        {domain && (
          <svg viewBox={`0 0 ${W} ${H}`} style={{ width: '100%', height: 'auto' }}>
            <line x1={L} y1={B} x2={W - 10} y2={B} stroke="currentColor" opacity={0.4} />
            <line x1={L} y1={TOP} x2={L} y2={B} stroke="currentColor" opacity={0.4} />
            {[1.5, 2, 3, 4].map((s) => (
              <g key={s}>
                <line x1={L} y1={gy(s)} x2={W - 10} y2={gy(s)} stroke="currentColor" opacity={0.12} strokeDasharray="4 4" />
                <text x={4} y={gy(s) + 3} fontSize={10} fill="currentColor" opacity={0.6}>{s.toFixed(1)}</text>
              </g>
            ))}
            {showPrev && prev && (
              <path d={toPath(prev.points, domain[0], domain[1], maxSwr)} fill="none" stroke="#F0997B" strokeWidth={1.5} strokeDasharray="5 4" opacity={0.9} />
            )}
            <path d={toPath(livePts, domain[0], domain[1], maxSwr)} fill="none" stroke="#378ADD" strokeWidth={2.2} />
            {cur?.span2Low != null && cur.span2High != null && !running && (
              <>
                <line x1={gx(cur.span2Low)} y1={gy(2)} x2={gx(cur.span2Low)} y2={B} stroke="#378ADD" strokeDasharray="2 3" opacity={0.7} />
                <line x1={gx(cur.span2High)} y1={gy(2)} x2={gx(cur.span2High)} y2={B} stroke="#378ADD" strokeDasharray="2 3" opacity={0.7} />
              </>
            )}
            {cur?.minSwrHz != null && cur.minSwr != null && !running && (
              <>
                <circle cx={gx(cur.minSwrHz)} cy={gy(Math.min(cur.minSwr, maxSwr))} r={4} fill="#185FA5" />
                <text x={gx(cur.minSwrHz) + 8} y={gy(Math.min(cur.minSwr, maxSwr)) - 6} fontSize={11} fill="currentColor">
                  {cur.minSwr.toFixed(2)} @ {(cur.minSwrHz / 1e6).toFixed(3)}
                </text>
              </>
            )}
            <text x={L} y={H - 8} fontSize={10} fill="currentColor" opacity={0.6}>{(domain[0] / 1e6).toFixed(3)}</text>
            <text x={W - 60} y={H - 8} fontSize={10} fill="currentColor" opacity={0.6}>{(domain[1] / 1e6).toFixed(3)}</text>
          </svg>
        )}
        {cur && !running && cur.span2Low != null && cur.span2High != null && (
          <p className="opacity-80">
            2:1 span {((cur.span2Low ?? 0) / 1e6).toFixed(3)}–{((cur.span2High ?? 0) / 1e6).toFixed(3)} MHz
            ({(((cur.span2High ?? 0) - (cur.span2Low ?? 0)) / 1e3).toFixed(0)} kHz)
          </p>
        )}
      </div>
    </section>
  );
}
