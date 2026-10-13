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
// "Check my settings" — pick what is wrong, and the radio checks the settings
// and conditions that commonly cause it (GET /api/diagnostics/check). Runs on
// the radio: no AI, nothing sent anywhere. Likely causes first, each with its
// fix; the settings that look fine are listed too, collapsed, so you can see
// what was checked.

import { useEffect, useState, type CSSProperties } from 'react';

type Symptom = { id: string; label: string; group: string };

export type SettingsCheckResult = {
  checkId: string;
  title: string;
  verdict: 'Ok' | 'Check' | 'Likely' | number;
  value: string;
  explanation: string;
  fix: string;
  manualRef: string | null;
};

/** The wire carries the enum as a name (string-enum converter) or a number. */
export function verdictOf(v: SettingsCheckResult['verdict']): 'Ok' | 'Check' | 'Likely' {
  if (v === 'Likely' || v === 2) return 'Likely';
  if (v === 'Check' || v === 1) return 'Check';
  return 'Ok';
}

export async function runSettingsCheck(symptomId: string): Promise<SettingsCheckResult[]> {
  const r = await fetch(`/api/diagnostics/check?symptom=${encodeURIComponent(symptomId)}`);
  if (!r.ok) throw new Error(`The radio answered HTTP ${r.status}.`);
  const body = (await r.json()) as unknown;
  return Array.isArray(body) ? (body as SettingsCheckResult[]) : [];
}

export function SettingsCheckPanel({ compact = false }: { compact?: boolean }) {
  const [symptoms, setSymptoms] = useState<Symptom[]>([]);
  const [symptom, setSymptom] = useState('');
  const [results, setResults] = useState<SettingsCheckResult[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showOk, setShowOk] = useState(false);

  useEffect(() => {
    let cancelled = false;
    fetch('/api/diagnostics/symptoms')
      .then((r) => (r.ok ? r.json() : []))
      .then((list: unknown) => {
        if (cancelled || !Array.isArray(list)) return;
        const s = list as Symptom[];
        setSymptoms(s);
        if (s.length > 0) setSymptom((cur) => cur || s[0]!.id);
      })
      .catch(() => {});
    return () => {
      cancelled = true;
    };
  }, []);

  const run = () => {
    if (!symptom || busy) return;
    setBusy(true);
    setError(null);
    runSettingsCheck(symptom)
      .then((rs) => {
        setResults(rs);
        setShowOk(false);
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : 'The check failed.'))
      .finally(() => setBusy(false));
  };

  const problems = (results ?? []).filter((r) => verdictOf(r.verdict) !== 'Ok');
  const fine = (results ?? []).filter((r) => verdictOf(r.verdict) === 'Ok');

  return (
    <div style={compact ? wrapCompact : wrap} data-testid="settings-check">
      {!compact && <div style={label}>CHECK MY SETTINGS</div>}
      <p style={hint}>
        Pick what&apos;s wrong and the radio checks the settings and conditions that commonly cause it. This runs on
        the radio — no AI, nothing is sent anywhere.
      </p>
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <select value={symptom} onChange={(e) => setSymptom(e.target.value)} style={select} aria-label="Problem">
          {symptoms.map((s) => (
            <option key={s.id} value={s.id}>
              {s.group} — {s.label}
            </option>
          ))}
        </select>
        <button type="button" className="btn sm active" style={btn} disabled={!symptom || busy} onClick={run}>
          {busy ? 'CHECKING…' : 'CHECK'}
        </button>
      </div>
      {error && <div style={{ ...hint, color: '#f0c08a' }}>{error}</div>}
      {results && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          {problems.length === 0 ? (
            <div style={okBanner}>Nothing in the usual suspects looks wrong for this problem.</div>
          ) : (
            problems.map((r, i) => <Row key={`${r.checkId}-${i}`} r={r} />)
          )}
          {fine.length > 0 && (
            <button type="button" style={linkBtn} onClick={() => setShowOk((v) => !v)}>
              {showOk ? 'Hide' : 'Show'} the {fine.length} setting{fine.length === 1 ? '' : 's'} that look fine
            </button>
          )}
          {showOk && fine.map((r, i) => <Row key={`ok-${r.checkId}-${i}`} r={r} />)}
        </div>
      )}
    </div>
  );
}

function Row({ r }: { r: SettingsCheckResult }) {
  const v = verdictOf(r.verdict);
  const tone = v === 'Likely' ? likely : v === 'Check' ? check : ok;
  return (
    <div style={{ ...row, borderColor: tone.border }} data-testid={`check-${v.toLowerCase()}`}>
      <div style={{ display: 'flex', gap: 8, alignItems: 'baseline', flexWrap: 'wrap' }}>
        <span style={{ ...badge, background: tone.bg, color: tone.fg }}>
          {v === 'Likely' ? 'LIKELY CAUSE' : v === 'Check' ? 'CHECK' : 'OK'}
        </span>
        <span style={{ fontWeight: 600 }}>{r.title}</span>
        <span style={{ fontFamily: 'var(--font-mono, ui-monospace, monospace)', color: '#cfe0ee' }}>{r.value}</span>
      </div>
      {v !== 'Ok' && (
        <>
          <div style={detail}>{r.explanation}</div>
          <div style={detail}>
            <b>Fix:</b> {r.fix}
          </div>
          {r.manualRef && <div style={{ ...detail, color: '#9fd0f0' }}>📖 Manual: {r.manualRef}</div>}
        </>
      )}
    </div>
  );
}

const likely = { bg: '#5a2a12', fg: '#ffcf9e', border: '#8a4a22' };
const check = { bg: '#4a3d12', fg: '#f0dc8a', border: '#6b5a22' };
const ok = { bg: '#163a2a', fg: '#9fe0bf', border: '#24493a' };
const wrap: CSSProperties = { display: 'flex', flexDirection: 'column', gap: 10, maxWidth: 680, padding: '6px 0' };
const wrapCompact: CSSProperties = { display: 'flex', flexDirection: 'column', gap: 8 };
const label: CSSProperties = { fontSize: 10, letterSpacing: '0.08em', color: 'var(--fg-3, #8a94a3)' };
const hint: CSSProperties = { fontSize: 12, lineHeight: 1.45, color: 'var(--fg-3, #8a94a3)', margin: 0 };
const select: CSSProperties = { minHeight: 40, fontSize: 14, flex: '1 1 260px' };
const btn: CSSProperties = { minHeight: 40, minWidth: 96 };
const row: CSSProperties = {
  border: '1px solid',
  borderRadius: 8,
  padding: '8px 10px',
  background: 'rgba(14,24,36,0.6)',
  fontSize: 13,
  color: '#d8e4f0',
};
const badge: CSSProperties = { fontSize: 10, letterSpacing: '0.06em', padding: '2px 6px', borderRadius: 4, fontWeight: 700 };
const detail: CSSProperties = { fontSize: 12.5, lineHeight: 1.45, marginTop: 4 };
const okBanner: CSSProperties = { ...row, borderColor: ok.border, color: '#9fe0bf' };
const linkBtn: CSSProperties = {
  alignSelf: 'flex-start',
  background: 'transparent',
  border: 'none',
  color: '#9fb4c8',
  fontSize: 12,
  textDecoration: 'underline dotted',
  cursor: 'pointer',
  padding: '4px 0',
};
