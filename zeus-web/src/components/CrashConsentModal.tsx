// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useEffect, useState } from 'react';

import { answerSupportAgreement, getSupportAvailability } from '../api/support';

export default function CrashConsentModal() {
  const [visible, setVisible] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const ctrl = new AbortController();
    (async () => {
      try {
        const s = await getSupportAvailability(ctrl.signal);
        if (!ctrl.signal.aborted) {
          setVisible(s.agreementVersion < s.currentAgreementVersion);
        }
      } catch {
        if (!ctrl.signal.aborted) {
          setVisible(false);
        }
      }
    })();
    return () => ctrl.abort();
  }, []);

  const answer = async (optIn: boolean) => {
    setBusy(true);
    setError(null);
    try {
      await answerSupportAgreement(optIn);
      setVisible(false);
    } catch (e) {
      setError((e as Error).message || 'Could not save your answer.');
    } finally {
      setBusy(false);
    }
  };

  if (!visible) return null;

  return (
    <div
      className="modal-backdrop"
      style={{
        position: 'fixed',
        inset: 0,
        background: 'color-mix(in srgb, var(--bg-inset) 72%, transparent)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        zIndex: 10000,
        padding: 16,
      }}
    >
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby="crash-consent-title"
        style={{
          maxWidth: 520,
          width: 'min(92vw, 520px)',
          padding: 20,
          background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
          border: '1px solid var(--line)',
          borderRadius: 8,
          color: 'var(--fg-0)',
          fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
          boxShadow:
            '0 18px 40px color-mix(in srgb, var(--bg-inset) 70%, transparent)',
          display: 'flex',
          flexDirection: 'column',
          gap: 14,
        }}
      >
        <h2
          id="crash-consent-title"
          style={{
            margin: 0,
            fontSize: 15,
            fontWeight: 700,
            letterSpacing: 0,
            color: 'var(--fg-0)',
          }}
        >
          Help improve Zeus
        </h2>

        <p style={{ margin: 0, fontSize: 13, color: 'var(--fg-1)', lineHeight: 1.55 }}>
          Automatically send crash logs to the developers? If Zeus crashes, a
          crash report (logs and a diagnostics snapshot) is sent so the problem
          can be fixed without you re-creating it. You can change this any time
          in Settings → Server → Remote Diagnostics.
        </p>

        {error && (
          <div
            role="alert"
            style={{
              padding: '8px 10px',
              background: 'var(--tx-soft)',
              border: '1px solid var(--tx)',
              borderRadius: 4,
              fontSize: 12,
              color: 'var(--fg-0)',
              lineHeight: 1.45,
            }}
          >
            {error}
          </div>
        )}

        <div
          style={{
            display: 'flex',
            justifyContent: 'flex-end',
            gap: 8,
            flexWrap: 'wrap',
          }}
        >
          <button
            type="button"
            className="btn ghost sm"
            disabled={busy}
            onClick={() => void answer(false)}
          >
            No thanks
          </button>
          <button
            type="button"
            className="btn sm active"
            disabled={busy}
            onClick={() => void answer(true)}
          >
            Yes, send crash reports
          </button>
        </div>
      </div>
    </div>
  );
}
