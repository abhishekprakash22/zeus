// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Neural CW decoder pop-out (DeepCW) — DiversityWindow's sibling: fixed,
// draggable, Escape closes, hidden (not stopped) under Settings. The CW
// transport button enables the decoder and opens this window; closing the
// window does NOT stop decoding (the transcript keeps growing) — the CW
// button is the on/off.

import { useCallback, useEffect, useRef, useState } from 'react';
import { useCwDecodeStore } from '../state/cw-decode-store';
import { useLayoutStore } from '../state/layout-store';

const WIDTH = 380;

let windowClaimed = false;

/** First mounted instance wins; duplicates (a render branch mounts this
 *  pattern twice) render nothing instead of stacking identical windows. */
function useWindowClaim(): boolean {
  const [primary, setPrimary] = useState(false);
  useEffect(() => {
    if (windowClaimed) return;
    windowClaimed = true;
    setPrimary(true);
    return () => {
      windowClaimed = false;
    };
  }, []);
  return primary;
}

export function CwDecodeWindow() {
  const primary = useWindowClaim();
  const open = useCwDecodeStore((s) => s.panelOpen);
  const enabled = useCwDecodeStore((s) => s.enabled);
  const status = useCwDecodeStore((s) => s.status);
  const error = useCwDecodeStore((s) => s.error);
  const transcript = useCwDecodeStore((s) => s.transcript);
  const lastCharAt = useCwDecodeStore((s) => s.lastCharAt);
  const setPanelOpen = useCwDecodeStore((s) => s.setPanelOpen);
  const setEnabled = useCwDecodeStore((s) => s.setEnabled);
  const overlayEnabled = useCwDecodeStore((s) => s.overlayEnabled);
  const setOverlayEnabled = useCwDecodeStore((s) => s.setOverlayEnabled);
  const clear = useCwDecodeStore((s) => s.clear);
  const settingsViewOpen = useLayoutStore((s) => s.settingsViewOpen);
  const [pos, setPos] = useState({ x: 24, y: 96 });
  const scrollRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [transcript]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      const t = e.target as HTMLElement | null;
      if (t?.closest('input, textarea, select, [contenteditable="true"]')) return;
      setPanelOpen(false);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, setPanelOpen]);

  const drag = useRef<{ id: number; sx: number; sy: number; ox: number; oy: number } | null>(null);
  const down = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      if ((e.target as HTMLElement).closest('[data-no-drag]')) return;
      e.currentTarget.setPointerCapture(e.pointerId);
      drag.current = { id: e.pointerId, sx: e.clientX, sy: e.clientY, ox: pos.x, oy: pos.y };
    },
    [pos],
  );
  const move = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    const d = drag.current;
    if (!d || d.id !== e.pointerId) return;
    setPos({
      x: Math.min(window.innerWidth - 80, Math.max(-WIDTH + 80, d.ox + e.clientX - d.sx)),
      y: Math.min(window.innerHeight - 40, Math.max(48, d.oy + e.clientY - d.sy)),
    });
  }, []);
  const up = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    if (drag.current?.id === e.pointerId) drag.current = null;
  }, []);

  if (!primary || !open || settingsViewOpen) return null;
  const fresh = Date.now() - lastCharAt < 1500;

  return (
    <div
      className="cwdec-window"
      style={{
        // Critical layout inlined: the pop-out must be a fixed overlay even
        // if a stale service-worker cache serves last release's stylesheet
        // (the exact failure that made v0.15.10's window invisible — it
        // rendered unstyled in page flow, below the fold).
        position: 'fixed',
        left: pos.x,
        top: pos.y,
        width: WIDTH,
        zIndex: 420,
        background: '#101318',
        border: '1px solid #31353d',
        borderRadius: 8,
        boxShadow: '0 12px 40px rgba(0,0,0,.55)',
        display: 'flex',
        flexDirection: 'column',
        overflow: 'hidden',
      }}
      role="dialog"
      aria-label="Neural CW decoder"
    >
      <div className="cwdec-header" onPointerDown={down} onPointerMove={move} onPointerUp={up}>
        <span className={`dw-dot ${status === 'running' ? 'on' : ''}`} />
        <span className="dw-title">CW DECODE · DEEPCW</span>
        <span className="cwdec-status">
          {status === 'loading' ? 'loading model…' : status === 'error' ? 'error' : status}
        </span>
        <span className="dw-spacer" />
        <button
          type="button"
          className={`cwdec-btn ${overlayEnabled ? 'on' : ''}`}
          data-no-drag
          title="Show the decode callout on the waterfall at the tuned frequency"
          onClick={() => setOverlayEnabled(!overlayEnabled)}
        >
          WF
        </button>
        <button type="button" className="cwdec-btn" data-no-drag onClick={clear}>
          CLEAR
        </button>
        <button
          type="button"
          className={`cwdec-btn ${enabled ? 'on' : ''}`}
          data-no-drag
          onClick={() => setEnabled(!enabled)}
        >
          {enabled ? 'ON' : 'OFF'}
        </button>
        <button type="button" className="dw-close" data-no-drag onClick={() => setPanelOpen(false)} aria-label="Close">
          ✕
        </button>
      </div>
      {status === 'error' && <div className="cwdec-error">{error}</div>}
      <div className="cwdec-scroll" ref={scrollRef}>
        <span className="cwdec-text">{transcript || '\u00a0'}</span>
        <span className={`cwdec-cursor ${fresh ? 'fresh' : ''}`}>▊</span>
      </div>
      <div className="cwdec-foot">
        neural decode of the tuned RX audio · e04 DeepCW engine (AGPL-3.0)
      </div>
    </div>
  );
}

/** Transport toggle: enables the decoder and opens the window; glows while
 *  decoding is live even if the window is closed. */
export function CwDecodeToggleButton() {
  const enabled = useCwDecodeStore((s) => s.enabled);
  const setEnabled = useCwDecodeStore((s) => s.setEnabled);
  return (
    <button
      type="button"
      className={`btn ghost cwdec-toggle ${enabled ? 'engaged' : ''}`}
      title="Neural CW decoder (DeepCW) — press to start/stop decoding the tuned signal"
      onClick={() => setEnabled(!enabled)}
    >
      CW⌁
    </button>
  );
}
