// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Remote link-quality chip. Rendered by RemoteGate once the session is
// unlocked; polls pc.getStats() once a second and shows the four numbers an
// operator actually needs to judge the link:
//   bars  — overall verdict (good / fair / poor) from RTT, audio loss, fps
//   RTT   — selected ICE candidate pair round-trip (ms)
//   loss  — inbound RX-audio packet loss over the last second (%)
//   fps   — display frames that actually arrived (host paces/drops under
//           backpressure — this is the number that predicts a stutter)
//   RELAY — lit when the selected pair rides TURN (CGNAT carriers); a relay
//           path is fine, but it's the first thing to know when RTT is high.
// Pure observer: reads stats, never touches the connection.

import { useEffect, useState, type CSSProperties } from 'react';
import type { RemoteConnection } from './connect';
import { takeDisplayFrameCount } from './remote-client';

type Verdict = 'good' | 'fair' | 'poor' | 'none';

interface LinkSample {
  rttMs: number | null;
  lossPct: number | null;
  jitterMs: number | null;
  fps: number;
  relay: boolean;
  verdict: Verdict;
}

const EMPTY: LinkSample = { rttMs: null, lossPct: null, jitterMs: null, fps: 0, relay: false, verdict: 'none' };

function judge(rttMs: number | null, lossPct: number | null, fps: number): Verdict {
  const rtt = rttMs ?? 0;
  const loss = lossPct ?? 0;
  if (rtt > 400 || loss >= 5 || fps < 5) return 'poor';
  if (rtt > 150 || loss >= 1 || fps < 10) return 'fair';
  return 'good';
}

interface StatsSnapshot {
  received: number;
  lost: number;
}

export function RemoteLinkChip({ conn }: { conn: RemoteConnection | null }) {
  const [s, setS] = useState<LinkSample>(EMPTY);

  useEffect(() => {
    if (!conn) return;
    const pc = conn.pc;
    let prev: StatsSnapshot | null = null;
    let cancelled = false;

    const tick = async () => {
      if (cancelled || pc.connectionState === 'closed') return;
      const fps = takeDisplayFrameCount();
      let rttMs: number | null = null;
      let lossPct: number | null = null;
      let jitterMs: number | null = null;
      let relay = false;
      try {
        const report = await pc.getStats();
        let selectedPairId: string | null = null;
        report.forEach((r: RTCStats & Record<string, unknown>) => {
          if (r.type === 'transport' && typeof r.selectedCandidatePairId === 'string')
            selectedPairId = r.selectedCandidatePairId;
        });
        report.forEach((r: RTCStats & Record<string, unknown>) => {
          if (r.type === 'candidate-pair') {
            const isSelected = selectedPairId ? r.id === selectedPairId : r.nominated === true && r.state === 'succeeded';
            if (!isSelected) return;
            if (typeof r.currentRoundTripTime === 'number') rttMs = Math.round(r.currentRoundTripTime * 1000);
            const local = typeof r.localCandidateId === 'string' ? report.get(r.localCandidateId) as (RTCStats & Record<string, unknown>) | undefined : undefined;
            const remote = typeof r.remoteCandidateId === 'string' ? report.get(r.remoteCandidateId) as (RTCStats & Record<string, unknown>) | undefined : undefined;
            relay = local?.candidateType === 'relay' || remote?.candidateType === 'relay';
          } else if (r.type === 'inbound-rtp' && r.kind === 'audio') {
            const received = typeof r.packetsReceived === 'number' ? r.packetsReceived : 0;
            const lost = typeof r.packetsLost === 'number' ? r.packetsLost : 0;
            if (typeof r.jitter === 'number') jitterMs = Math.round(r.jitter * 1000);
            if (prev) {
              const dr = received - prev.received;
              const dl = lost - prev.lost;
              const total = dr + dl;
              lossPct = total > 0 ? Math.max(0, (100 * dl) / total) : 0;
            }
            prev = { received, lost };
          }
        });
      } catch {
        /* stats unavailable this tick — keep last sample's shape, update fps */
      }
      if (!cancelled) setS({ rttMs, lossPct, jitterMs, fps, relay, verdict: judge(rttMs, lossPct, fps) });
    };

    const t = setInterval(() => { void tick(); }, 1000);
    void tick();
    return () => { cancelled = true; clearInterval(t); };
  }, [conn]);

  if (!conn) return null;

  const color = s.verdict === 'good' ? 'var(--ok, #4ade80)'
    : s.verdict === 'fair' ? '#ffb545'
    : s.verdict === 'poor' ? 'var(--tx, #ff5a4d)'
    : 'var(--fg-3, #545c66)';
  const lit = s.verdict === 'good' ? 4 : s.verdict === 'fair' ? 2 : s.verdict === 'poor' ? 1 : 0;
  const title = `RTT ${s.rttMs ?? '–'} ms · jitter ${s.jitterMs ?? '–'} ms · audio loss ${s.lossPct == null ? '–' : s.lossPct.toFixed(1)}% · ${s.fps} fps${s.relay ? ' · via TURN relay' : ' · direct'}`;

  return (
    <div style={chip} title={title} aria-label={`Remote link ${s.verdict}: ${title}`}>
      <span style={barsWrap}>
        {[0, 1, 2, 3].map((i) => (
          <span
            key={i}
            style={{ ...bar, height: 4 + i * 2, background: i < lit ? color : 'var(--fg-3, #545c66)', opacity: i < lit ? 1 : 0.45 }}
          />
        ))}
      </span>
      <span style={{ ...num, color }}>{s.rttMs == null ? '–' : s.rttMs}<span style={unit}>ms</span></span>
      <span style={num}>{s.lossPct == null ? '–' : s.lossPct.toFixed(0)}<span style={unit}>%</span></span>
      <span style={num}>{s.fps}<span style={unit}>fps</span></span>
      {s.relay && <span style={tag}>RELAY</span>}
    </div>
  );
}

const chip: CSSProperties = {
  position: 'fixed',
  top: 6,
  right: 8,
  zIndex: 470,
  display: 'inline-flex',
  alignItems: 'center',
  gap: 7,
  padding: '3px 7px',
  borderRadius: 4,
  background: 'rgba(12, 16, 22, 0.82)',
  border: '1px solid var(--line-strong, #3d4552)',
  fontFamily: 'var(--mono, ui-monospace, monospace)',
  fontSize: 10,
  fontWeight: 700,
  letterSpacing: '0.04em',
  color: 'var(--fg-2, #9aa3ad)',
  pointerEvents: 'none',
  userSelect: 'none',
};

const barsWrap: CSSProperties = { display: 'inline-flex', alignItems: 'flex-end', gap: 1.5, height: 10 };
const bar: CSSProperties = { display: 'inline-block', width: 3, borderRadius: 1 };
const num: CSSProperties = { minWidth: 0 };
const unit: CSSProperties = { fontSize: 8, fontWeight: 600, opacity: 0.7, marginLeft: 1 };
const tag: CSSProperties = {
  fontSize: 8,
  fontWeight: 800,
  letterSpacing: '0.08em',
  padding: '0 4px',
  borderRadius: 3,
  color: 'var(--accent, #4aa3df)',
  border: '1px solid var(--accent, #4aa3df)',
};
