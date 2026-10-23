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
// The filter card's display, with a selectable view per receiver: Passband
// (the existing mini-pan, passed as children) or one of the audio views.
// Selected with the header chip (opens a grid), a horizontal swipe on an
// audio view, the wheel over the page dots (Shift+wheel on the display), or
// the G2 FILTER VIEW key. While keyed, the audio views follow the TX mic.

import { useEffect, useLayoutEffect, useRef, useState, type ReactNode, type WheelEvent } from 'react';
import { createPortal } from 'react-dom';
import { useConnectionStore } from '../state/connection-store';
import {
  getReceiverFilterHighHz,
  getReceiverFilterLowHz,
  getReceiverMode,
  rxIndexOf,
  type ReceiverKey,
} from '../state/receiver-state';
import { useTxStore } from '../state/tx-store';
import { AF_TX_SOURCE, useAfLease } from './af-analysis';
import { AfView } from './AfView';
import {
  FILTER_VIEWS,
  effectiveView,
  stepView,
  useFilterViewStore,
  viewsFor,
  type FilterView,
} from './filter-view-store';

const SWIPE_MIN_PX = 40;

export function FilterDisplay({ receiver, children }: { receiver: ReceiverKey; children: ReactNode }) {
  const rx = rxIndexOf(receiver);
  const mode = useConnectionStore((s) => getReceiverMode(s, receiver)) as string | undefined;
  const lowHz = useConnectionStore((s) => getReceiverFilterLowHz(s, receiver));
  const highHz = useConnectionStore((s) => getReceiverFilterHighHz(s, receiver));
  const cwPitchHz = useConnectionStore((s) => s.cwPitchHz);
  const chosen = useFilterViewStore((s) => s.views[rx] ?? 'pass');
  const setView = useFilterViewStore((s) => s.set);
  const hydrate = useFilterViewStore((s) => s.hydrate);
  const mox = useTxStore((s) => s.moxOn);
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState<{ top: number; right: number } | null>(null);
  const swipe = useRef<{ x: number; y: number } | null>(null);
  const chipRef = useRef<HTMLButtonElement | null>(null);
  const stripRef = useRef<HTMLDivElement | null>(null);

  // The view strip floats in a portal under the chip (fixed position), so the
  // card's own bounds can never clip it (a small filter card used to cut off
  // CW pitch and Levels). It closes on any outside press, Escape, scroll or
  // resize.
  useLayoutEffect(() => {
    if (!open || !chipRef.current) return;
    const r = chipRef.current.getBoundingClientRect();
    setPos({ top: r.bottom + 4, right: Math.max(8, window.innerWidth - r.right) });
  }, [open]);
  useEffect(() => {
    if (!open) return;
    const close = (e: Event) => {
      const t = e.target as Node | null;
      if (t && (stripRef.current?.contains(t) || chipRef.current?.contains(t))) return;
      setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    const dismiss = () => setOpen(false);
    document.addEventListener('pointerdown', close, true);
    document.addEventListener('keydown', onKey);
    window.addEventListener('resize', dismiss);
    window.addEventListener('scroll', dismiss, true);
    return () => {
      document.removeEventListener('pointerdown', close, true);
      document.removeEventListener('keydown', onKey);
      window.removeEventListener('resize', dismiss);
      window.removeEventListener('scroll', dismiss, true);
    };
  }, [open]);

  useEffect(() => {
    void hydrate();
  }, [hydrate]);

  const view = effectiveView(chosen, mode);
  const audio = view !== 'pass';
  const tx = mox && audio && view !== 'cw';
  const source = tx ? AF_TX_SOURCE : rx;
  useAfLease(source, audio);

  const list = viewsFor(mode);
  const meta = FILTER_VIEWS.find((v) => v.id === view)!;
  const pick = (v: FilterView) => {
    setView(rx, v);
    setOpen(false);
  };
  const step = (dir: 1 | -1) => setView(rx, stepView(view, mode, dir));
  const onWheelDots = (e: WheelEvent) => {
    e.preventDefault();
    step(e.deltaY > 0 ? 1 : -1);
  };
  const onWheelDisplay = (e: WheelEvent) => {
    if (!e.shiftKey) return;
    e.preventDefault();
    step(e.deltaY > 0 ? 1 : -1);
  };

  return (
    <div className="fv-wrap" data-testid={`filter-display-${rx}`} onWheel={onWheelDisplay}>
      {audio ? (
        <div
          className="fv-audio"
          onPointerDown={(e) => {
            swipe.current = { x: e.clientX, y: e.clientY };
          }}
          onPointerUp={(e) => {
            const s = swipe.current;
            swipe.current = null;
            if (!s) return;
            const dx = e.clientX - s.x;
            const dy = e.clientY - s.y;
            if (Math.abs(dx) >= SWIPE_MIN_PX && Math.abs(dx) > 1.5 * Math.abs(dy)) step(dx < 0 ? 1 : -1);
          }}
          onPointerCancel={() => {
            swipe.current = null;
          }}
        >
          <AfView
            view={view as Exclude<FilterView, 'pass'>}
            source={source}
            rx={rx}
            tx={tx}
            filterLowHz={lowHz}
            filterHighHz={highHz}
            cwPitchHz={cwPitchHz}
            agcReferred={!tx && (mode ?? '').toUpperCase() !== 'FREEDV'}
          />
        </div>
      ) : (
        children
      )}

      <button
        ref={chipRef}
        type="button"
        className={`fv-chip${tx ? ' fv-chip--tx' : ''}`}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={`Filter display: ${meta.label}`}
        title={meta.label}
        onClick={() => setOpen((o) => !o)}
      >
        {tx && <span className="fv-tx">TX</span>}
        <span aria-hidden>{meta.icon}</span>
        <span>{meta.short}</span>
        <span aria-hidden className="fv-caret">
          ▾
        </span>
      </button>

      {open &&
        pos &&
        createPortal(
          <div
            ref={stripRef}
            className="fv-strip"
            role="menu"
            style={{ top: pos.top, right: pos.right }}
            data-testid="filter-view-strip"
          >
            {FILTER_VIEWS.map((v) => {
              const available = list.includes(v.id);
              return (
                <button
                  key={v.id}
                  type="button"
                  role="menuitemradio"
                  aria-checked={v.id === view}
                  aria-label={available ? v.label : `${v.label} (CW only)`}
                  title={available ? v.label : `${v.label} — CW modes only`}
                  disabled={!available}
                  className={`fv-item${v.id === view ? ' fv-item--on' : ''}`}
                  onClick={() => available && pick(v.id)}
                >
                  <span aria-hidden className="fv-ic">
                    {v.icon}
                  </span>
                  <span className="fv-short">{v.short}</span>
                </button>
              );
            })}
          </div>,
          document.body,
        )}

      <div className="fv-dots" onWheel={onWheelDots} aria-hidden>
        {list.map((id) => (
          <span key={id} className={`fv-dot${id === view ? ' fv-dot--on' : ''}`} />
        ))}
      </div>
    </div>
  );
}

/** FILTER VIEW key helpers (G2 deck): step the focused receiver's view, or back to Passband. */
export function stepFocusedFilterView(): void {
  const s = useConnectionStore.getState();
  const rx = s.focusedRxIndex ?? 0;
  const mode = getReceiverMode(s, rx) as string | undefined;
  const store = useFilterViewStore.getState();
  store.set(rx, stepView(effectiveView(store.get(rx), mode), mode, 1));
}

export function resetFocusedFilterView(): void {
  const rx = useConnectionStore.getState().focusedRxIndex ?? 0;
  useFilterViewStore.getState().set(rx, 'pass');
}
