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
// Spectrum radial menu. Opened from the panadapter / waterfall by right-click,
// the pen's barrel button, or a two-finger tap. The centre is "Tune": it shows
// where the dial will land — on the nearby signal's mode-aware tuning point
// when one is under the press (snap), else on the click grid — and the ring
// carries the actions that need a frequency: split, RX2, notch, memory, spot.

import { useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { createPortal } from 'react-dom';
import { setRx2, setTxVfo, setVfoB, type RadioStateDto } from '../../api/client';
import { isPlausibleCallsign, postClusterSpot } from '../../api/memories';
import { useConnectionStore } from '../../state/connection-store';
import { useDxClusterStore } from '../../state/dxcluster-store';
import { memoryNear, useMemoryStore } from '../../state/memory-store';
import { useNotchStore } from '../../state/notch-store';
import { useRadialMenuStore, type RadialContext } from '../../state/radial-menu-store';
import { useVfoLockStore } from '../../state/vfo-lock-store';
import {
  CENTER_SIZE_PX,
  clampCenter,
  formatDialHz,
  ITEM_SIZE_PX,
  MEMORY_HIT_PX,
  radialItems,
  ringOffsets,
  spotFrequencyHz,
  type RadialItemId,
} from './radial-items';

// Default notch width when dropped from the menu — same as a click-notch.
const MENU_NOTCH_WIDTH_HZ = 150;
const FLASH_MS = 700;

function applyStateSafe(p: Promise<RadioStateDto>): Promise<void> {
  return p.then((s) => {
    useConnectionStore.getState().applyState(s);
  });
}

function filterFor(rxIndex: number): { lo: number | null; hi: number | null } {
  const s = useConnectionStore.getState();
  if (rxIndex === 0) return { lo: s.filterLowHz, hi: s.filterHighHz };
  const r = s.receivers.find((x) => x.index === rxIndex);
  return r ? { lo: r.filterLowHz, hi: r.filterHighHz } : { lo: null, hi: null };
}

export function SpectrumRadialMenu() {
  const ctx = useRadialMenuStore((s) => s.open);
  if (!ctx) return null;
  // Remount per open so form / flash state never leaks between presses.
  return createPortal(<RadialMenuBody key={`${ctx.clientX},${ctx.clientY},${ctx.rawHz}`} ctx={ctx} />, document.body);
}

function RadialMenuBody({ ctx }: { ctx: RadialContext }) {
  const close = useRadialMenuStore((s) => s.close);
  const locked = useVfoLockStore((s) => s.locked);
  const maxReceivers = useConnectionStore((s) => s.maxReceivers);
  const clusterState = useDxClusterStore((s) => s.status?.state);
  const channels = useMemoryStore((s) => s.channels);
  const rootRef = useRef<HTMLDivElement | null>(null);
  const centerRef = useRef<HTMLButtonElement | null>(null);
  const [flash, setFlash] = useState<string | null>(null);
  const [spotForm, setSpotForm] = useState(false);

  useEffect(() => {
    void useMemoryStore.getState().hydrate();
    centerRef.current?.focus({ preventScroll: true });
  }, []);

  // Close on Escape, on a press anywhere outside, and when the window changes
  // under the menu (resize / blur) — the frequency it captured is stale then.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      e.preventDefault();
      if (spotForm) setSpotForm(false);
      else close();
    };
    const onDown = (e: PointerEvent) => {
      if (rootRef.current && e.target instanceof Node && rootRef.current.contains(e.target)) return;
      close();
    };
    const onGone = () => close();
    window.addEventListener('keydown', onKey, true);
    window.addEventListener('pointerdown', onDown, true);
    window.addEventListener('resize', onGone);
    window.addEventListener('blur', onGone);
    return () => {
      window.removeEventListener('keydown', onKey, true);
      window.removeEventListener('pointerdown', onDown, true);
      window.removeEventListener('resize', onGone);
      window.removeEventListener('blur', onGone);
    };
  }, [close, spotForm]);

  const memoryHit = useMemo(
    () => (ctx.panRxIndex === 0 ? memoryNear(channels, ctx.rawHz, ctx.hzPerPx * MEMORY_HIT_PX) : null),
    [channels, ctx.panRxIndex, ctx.rawHz, ctx.hzPerPx],
  );
  const items = radialItems(ctx, {
    locked,
    hasRx2: maxReceivers >= 2,
    clusterConnected: clusterState === 'Connected',
    memoryHit,
  });
  const offsets = ringOffsets(items.length);
  const center = clampCenter(
    ctx.clientX,
    ctx.clientY,
    typeof window !== 'undefined' ? window.innerWidth : 1024,
    typeof window !== 'undefined' ? window.innerHeight : 768,
  );

  const flashThenClose = (text: string) => {
    setFlash(text);
    window.setTimeout(close, FLASH_MS);
  };

  const run = (id: RadialItemId) => {
    switch (id) {
      case 'split':
        close();
        void applyStateSafe(setVfoB(ctx.tuneHz))
          .then(() => applyStateSafe(setTxVfo('B')))
          .catch(() => {});
        return;
      case 'rx2':
        close();
        void applyStateSafe(setRx2({ enabled: true, vfoBHz: ctx.tuneHz })).catch(() => {});
        return;
      case 'notch':
        close();
        useNotchStore.getState().addNotch(ctx.carrierHz ?? ctx.rawHz, MENU_NOTCH_WIDTH_HZ);
        return;
      case 'memory': {
        const f = filterFor(ctx.rxIndex);
        void useMemoryStore
          .getState()
          .store({ hz: ctx.tuneHz, mode: ctx.mode, filterLowHz: f.lo, filterHighHz: f.hi })
          .then((ok) => flashThenClose(ok ? 'Stored' : 'Not stored'));
        return;
      }
      case 'forget':
        if (!memoryHit) return;
        void useMemoryStore
          .getState()
          .remove(memoryHit.id)
          .then((ok) => flashThenClose(ok ? 'Deleted' : 'Not deleted'));
        return;
      case 'spot':
        setSpotForm(true);
        return;
    }
  };

  const rootStyle: CSSProperties = { left: center.x, top: center.y };

  return (
    <div
      ref={rootRef}
      className="srm"
      style={rootStyle}
      role="menu"
      aria-label={`Spectrum actions at ${formatDialHz(ctx.tuneHz)} MHz`}
      onContextMenu={(e) => e.preventDefault()}
    >
      {spotForm ? (
        <SpotForm ctx={ctx} onCancel={() => setSpotForm(false)} onSent={() => flashThenClose('Spotted')} />
      ) : (
        <>
          {items.map((it, i) => {
            const o = offsets[i]!;
            return (
              <button
                key={it.id}
                type="button"
                role="menuitem"
                className={`srm-item${it.id === 'forget' ? ' srm-item--warn' : ''}`}
                style={{
                  left: o.dx - ITEM_SIZE_PX / 2,
                  top: o.dy - ITEM_SIZE_PX / 2,
                  width: ITEM_SIZE_PX,
                  height: ITEM_SIZE_PX,
                  animationDelay: `${i * 18}ms`,
                }}
                title={it.hint}
                aria-label={`${it.label}: ${it.hint}`}
                disabled={it.disabled || flash !== null}
                onClick={() => run(it.id)}
              >
                <span className="srm-ic" aria-hidden>
                  {it.icon}
                </span>
                <span className="srm-lbl">{it.label}</span>
              </button>
            );
          })}
          <button
            ref={centerRef}
            type="button"
            role="menuitem"
            className={`srm-center${ctx.snapped ? ' srm-center--snap' : ''}`}
            style={{
              left: -CENTER_SIZE_PX / 2,
              top: -CENTER_SIZE_PX / 2,
              width: CENTER_SIZE_PX,
              height: CENTER_SIZE_PX,
            }}
            disabled={locked || flash !== null}
            title={locked ? 'VFO locked' : ctx.snapped ? 'Tune to the signal' : 'Tune here'}
            onClick={() => {
              ctx.tune();
              close();
            }}
          >
            {flash ? (
              <span className="srm-flash">{flash}</span>
            ) : (
              <>
                <span className="srm-tag">{ctx.snapped ? 'SNAP' : 'TUNE'}</span>
                <span className="srm-hz">{formatDialHz(ctx.tuneHz)}</span>
                {ctx.rxIndex !== 0 && <span className="srm-rx">RX{ctx.rxIndex + 1}</span>}
              </>
            )}
          </button>
        </>
      )}
    </div>
  );
}

function SpotForm({
  ctx,
  onCancel,
  onSent,
}: {
  ctx: RadialContext;
  onCancel: () => void;
  onSent: () => void;
}) {
  const [call, setCall] = useState('');
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const freqHz = spotFrequencyHz(ctx);
  const valid = isPlausibleCallsign(call);

  const submit = () => {
    if (!valid || busy) return;
    setBusy(true);
    setError(null);
    void postClusterSpot(freqHz, call, comment).then((r) => {
      setBusy(false);
      if (r.ok) onSent();
      else setError(r.error);
    });
  };

  return (
    <form
      className="srm-spot"
      onSubmit={(e) => {
        e.preventDefault();
        submit();
      }}
    >
      <div className="srm-spot-head">
        Spot <span className="srm-spot-hz">{(freqHz / 1000).toFixed(1)} kHz</span>
      </div>
      <input
        className="srm-spot-in"
        autoFocus
        placeholder="Callsign"
        value={call}
        maxLength={15}
        spellCheck={false}
        autoCapitalize="characters"
        aria-label="Callsign"
        onChange={(e) => setCall(e.currentTarget.value.toUpperCase())}
      />
      <input
        className="srm-spot-in"
        placeholder="Comment (optional)"
        value={comment}
        maxLength={30}
        aria-label="Comment"
        onChange={(e) => setComment(e.currentTarget.value)}
      />
      {error && <div className="srm-spot-err">{error}</div>}
      <div className="srm-spot-row">
        <button type="button" className="btn sm" onClick={onCancel}>
          Back
        </button>
        <button type="submit" className="btn sm active" disabled={!valid || busy}>
          {busy ? 'Sending…' : 'Send'}
        </button>
      </div>
    </form>
  );
}
