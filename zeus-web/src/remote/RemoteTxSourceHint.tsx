// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Remote TX-source hint (field: 'mic is not working on iPad' — the mic was
// working; the radio's TX audio source was armed to the front-panel mic, and
// the single-select ingest gate silently dropped the remote Opus uplink, so
// the operator debugged permissions and reloads for an hour when the real fix
// was one settings flip). A remote operator keying MOX deserves to be TOLD the
// radio is listening to a different microphone. On the rising MOX edge in a
// remote session this re-reads the TX audio front-end through the tunnel and,
// if the armed source is not Host, shows a dismissable warning. Advisory only
// — it never changes the source: arbitration is a desk decision by design.

import { useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import { useTxStore } from '../state/tx-store';
import { useAudioStore, type TxAudioSource } from '../state/audio-store';
import { isRemoteMode } from './remote-client';

const SOURCE_LABEL: Record<TxAudioSource, string> = {
  Host: 'Host',
  RadioMic: 'front-panel mic',
  RadioLineIn: 'rear line-in',
  RadioBalancedXlr: 'balanced XLR',
};

export function RemoteTxSourceHint() {
  const moxOn = useTxStore((s) => s.moxOn);
  const [warnSource, setWarnSource] = useState<TxAudioSource | null>(null);
  const [dismissed, setDismissed] = useState(false);

  useEffect(() => {
    if (!moxOn || !isRemoteMode()) return;
    let stale = false;
    // Re-read through the tunnel on every keying: the source is a desk-side
    // setting that can change between overs, and the lazy audio-store load
    // only runs when the settings panel opens.
    void useAudioStore
      .getState()
      .load()
      .then(() => {
        if (stale) return;
        const src = useAudioStore.getState().settings.source;
        setWarnSource(src === 'Host' ? null : src);
      })
      .catch(() => {
        /* advisory only — never block or noise up a failing tunnel */
      });
    return () => {
      stale = true;
    };
  }, [moxOn]);

  if (!moxOn || dismissed || warnSource === null || !isRemoteMode()) return null;
  return (
    <div role="alert" style={chip}>
      <span>
        ⚠ TX source is <b>{SOURCE_LABEL[warnSource]}</b> — your voice is not
        reaching the air. Switch the TX audio input to Host in the radio&apos;s
        audio settings.
      </span>
      <button
        type="button"
        aria-label="Dismiss"
        style={dismissBtn}
        onClick={() => setDismissed(true)}
      >
        ✕
      </button>
    </div>
  );
}

const chip: CSSProperties = {
  position: 'fixed',
  top: 104, // below the tap-for-audio chip slot (64px); they may co-exist
  left: '50%',
  transform: 'translateX(-50%)',
  zIndex: 10001,
  display: 'flex',
  alignItems: 'center',
  gap: 10,
  maxWidth: 'min(92vw, 560px)',
  padding: '8px 12px',
  borderRadius: 10,
  background: 'rgba(24,16,8,0.94)',
  color: '#ffd9a1',
  border: '1px solid var(--warn, #ffb13c)',
  font: '600 12px/1.35 system-ui, sans-serif',
  boxShadow: '0 4px 16px rgba(0,0,0,0.4)',
};

const dismissBtn: CSSProperties = {
  flex: '0 0 auto',
  border: 'none',
  background: 'transparent',
  color: 'inherit',
  font: '700 13px/1 system-ui, sans-serif',
  cursor: 'pointer',
  padding: '2px 4px',
};
