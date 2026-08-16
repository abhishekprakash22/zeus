// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// XdmaNativeRxControls — Phase 4a: the discovery row's START button for the
// PCIe-native Saturn. Two-press arm supplies the register-contention
// confirm (the stream owns registers p2app also owns, so the operator
// states the field is clear); while running the row shows the live
// demuxed rate and frame delivery, with STOP restoring the field.
// Full Connect (VFO routing, session workspace) is the 4b step — this
// button starts the engine + DDC0 pump that 4b will inhabit.

import { useCallback, useEffect, useRef, useState } from 'react';
import { fetchXdmaRx, startXdmaRx, stopXdmaRx } from '../api/client';
import type { XdmaRxStatus } from '../api/client';
import { getAudioClient } from '../audio/audio-client';

const START_RATE_KHZ = 48;
const START_HZ = 7_100_000;

// Field switch (2026-08-16): the display radio ships badge-only for now —
// START RX stays off the glass until the native path is factory-blessed.
// A stream that is ALREADY running (started via curl/API) still shows its
// live rate and STOP: an active register-plane owner must never be
// invisible or unstoppable from the screen. Flip to true to restore.
const NATIVE_RX_START_UI: boolean = false;

export function XdmaNativeRxControls({ statusLine }: { statusLine?: string }) {
  const [armed, setArmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<XdmaRxStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const disarmTimer = useRef<number | null>(null);

  const running = status?.running === true;

  // Poll while running (and once on mount to adopt an already-running stream).
  useEffect(() => {
    let cancelled = false;
    let timer: number | null = null;
    const poll = async () => {
      try {
        const s = await fetchXdmaRx();
        if (!cancelled) setStatus(s);
        if (!cancelled && s.running) timer = window.setTimeout(poll, 1000);
      } catch {
        /* status endpoint absent or transient — leave last state */
      }
    };
    void poll();
    return () => {
      cancelled = true;
      if (timer !== null) window.clearTimeout(timer);
    };
  }, [running]);

  const onStart = useCallback(async () => {
    if (!armed) {
      setArmed(true);
      setError(null);
      if (disarmTimer.current !== null) window.clearTimeout(disarmTimer.current);
      disarmTimer.current = window.setTimeout(() => setArmed(false), 4000);
      return;
    }
    setArmed(false);
    setBusy(true);
    setError(null);
    try {
      const s = await startXdmaRx(START_RATE_KHZ, START_HZ);
      setStatus(s);
      // The silence hunt's verdict: on a kiosk/browser install the desktop
      // NativeAudioSink stands down BY DESIGN and RX audio travels the
      // WebSocket to the BROWSER — which only listens once its audio
      // client starts, normally inside the connected workspace. At the
      // discovery screen nobody was listening to a perfectly demodulated
      // stream. This click is a user gesture, so the AudioContext is
      // allowed to start right here — same call the Connect flow makes.
      void getAudioClient().start();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }, [armed]);

  const onStop = useCallback(async () => {
    setBusy(true);
    try {
      void getAudioClient().stop();
      await stopXdmaRx();
      const s = await fetchXdmaRx().catch(() => null);
      setStatus(s ?? null);
    } finally {
      setBusy(false);
    }
  }, []);

  return (
    <div
      style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}
      title={statusLine ?? 'PCIe-native transport — RX pump + DSP engine, no p2app'}
    >
      <span className="label-xs" style={{ color: 'var(--accent, #6fc3ff)' }}>
        PCIe · DETECTED
      </span>
      {!running ? (
        !NATIVE_RX_START_UI ? null : (
        <>
          <button
            type="button"
            className="btn sm"
            disabled={busy}
            onClick={() => void onStart()}
            title={
              armed
                ? 'Native RX owns registers p2app also owns — press again to confirm p2app is stopped'
                : `Start native RX: DSP engine up, DDC0 follows your VFO (falls back to ${(START_HZ / 1e6).toFixed(1)} MHz) at ${START_RATE_KHZ} kHz — no p2app, no network`
            }
            style={armed ? { borderColor: '#e0a030', color: '#e0a030' } : undefined}
          >
            {armed ? 'p2app stopped? START' : 'START RX'}
          </button>
          {error !== null && (
            <span className="label-xs" style={{ color: 'var(--danger, #e06060)' }}>
              {error}
            </span>
          )}
        </>
        )
      ) : (
        <>
          <span className="label-xs" style={{ color: 'var(--ok, #6fdf8f)' }}>
            ● {status?.effectiveKsps?.toFixed(1) ?? '—'} kS/s · {status?.fedFrames ?? 0} frames
          </span>
          <button type="button" className="btn sm" disabled={busy} onClick={() => void onStop()}
            title="Stop the native stream and release the register plane">
            STOP
          </button>
        </>
      )}
    </div>
  );
}
