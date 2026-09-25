// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// SSTV pop-out — DiversityWindow's sibling: fixed-size, draggable, always on
// top. It is the SSTV mode: the SSTV button in the mode row (beside FT8/FT4/
// WSPR, via enter-digital) opens it, and closing it exits the mode — decoder
// off, prior sideband restored. The picture paints line by line as
// SstvService decodes it; when it ends, the straightened (slant-corrected)
// re-render replaces it and it lands in the gallery (PNG on disk).
//
// Below the picture: manual corrections for pictures whose recorded track is
// still in memory (slant, shift, "decode as"), delete, and the gallery strip.

import { useCallback, useEffect, useRef, useState } from 'react';
import { SSTV_PRESETS, useSstvStore } from '../state/sstv-store';
import { SstvSendPanel } from './SstvSendPanel';
import { useLayoutStore } from '../state/layout-store';
import { useConnectionStore } from '../state/connection-store';
import { useLoggerStore } from '../state/logger-store';
import { freqHzToBand } from '../state/spots-store';
import type { SstvImageMeta } from '../api/client';

const WIDTH = 440;
const SLANT_RANGE_PPM = 3000;
const ADJUST_DEBOUNCE_MS = 250;
/** SSTV reports are RSV (readability, signal, video); 595 is the clean-copy
 *  default operators exchange. */
const DEFAULT_RSV = '595';

function utcHm(ms: number): string {
  const d = new Date(ms);
  return `${String(d.getUTCHours()).padStart(2, '0')}:${String(d.getUTCMinutes()).padStart(2, '0')}z`;
}

function utcDate(ms: number): string {
  return new Date(ms).toISOString().slice(0, 10);
}

function describe(m: SstvImageMeta, live: boolean): string {
  const parts = [m.viaSync ? `${m.mode} (no VIS)` : m.mode];
  if (m.callsign) parts.push(`de ${m.callsign}`);
  parts.push(`${m.rowsDone}/${m.height}`);
  if (Math.abs(m.offsetHz) >= 1)
    parts.push(`${m.offsetHz > 0 ? '+' : ''}${Math.round(m.offsetHz)} Hz`);
  if (!live && m.clockErrorPpm !== 0)
    parts.push(`${m.clockErrorPpm > 0 ? '+' : ''}${m.clockErrorPpm} ppm`);
  if (m.dialHz > 0) parts.push(`${(m.dialHz / 1e6).toFixed(3)} ${m.sideBand}`);
  if (!live) parts.push(`${utcDate(m.startedUnixMs)} ${utcHm(m.startedUnixMs)}`);
  if (!live && m.endReason && m.endReason !== 'Complete') parts.push(m.endReason);
  return parts.join(' · ');
}

/** Slant / shift / decode-as for one finished picture. Sliders are local
 *  while dragging and commit (debounced) as absolute values. */
function AdjustBar({ meta }: { meta: SstvImageMeta }) {
  const modes = useSstvStore((s) => s.modes);
  const adjust = useSstvStore((s) => s.adjust);
  const remove = useSstvStore((s) => s.remove);
  const replyTo = useSstvStore((s) => s.replyTo);
  const [slant, setSlant] = useState(meta.slantPpm);
  const [shift, setShift] = useState(meta.shiftPx);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const pending = useRef<number | null>(null);

  // Follow the server when another view (or a reset) changes the picture.
  useEffect(() => {
    setSlant(meta.slantPpm);
    setShift(meta.shiftPx);
    setConfirmDelete(false);
  }, [meta.id, meta.slantPpm, meta.shiftPx]);

  const commit = useCallback(
    (next: { slantPpm?: number; shiftPx?: number; mode?: string }) => {
      if (pending.current != null) window.clearTimeout(pending.current);
      pending.current = window.setTimeout(() => {
        pending.current = null;
        void adjust(meta.id, next);
      }, ADJUST_DEBOUNCE_MS);
    },
    [adjust, meta.id],
  );
  useEffect(
    () => () => {
      if (pending.current != null) window.clearTimeout(pending.current);
    },
    [],
  );

  const disabled = !meta.adjustable;
  const lockedTitle = disabled
    ? 'Only the newest pictures of this session keep the recording needed to re-render'
    : undefined;
  const shiftRange = Math.round(meta.width / 8);

  return (
    <div className="sstv-adjust" title={lockedTitle}>
      <label className="sstv-adjust-row">
        <span>Slant</span>
        <input
          type="range"
          min={-SLANT_RANGE_PPM}
          max={SLANT_RANGE_PPM}
          step={10}
          value={slant}
          disabled={disabled}
          onChange={(e) => {
            const v = Number(e.target.value);
            setSlant(v);
            commit({ slantPpm: v, shiftPx: shift });
          }}
        />
        <span className="sstv-adjust-val">{`${slant > 0 ? '+' : ''}${slant} ppm`}</span>
      </label>
      <label className="sstv-adjust-row">
        <span>Shift</span>
        <input
          type="range"
          min={-shiftRange}
          max={shiftRange}
          step={1}
          value={shift}
          disabled={disabled}
          onChange={(e) => {
            const v = Number(e.target.value);
            setShift(v);
            commit({ slantPpm: slant, shiftPx: v });
          }}
        />
        <span className="sstv-adjust-val">{`${shift > 0 ? '+' : ''}${shift} px`}</span>
      </label>
      <div className="sstv-adjust-row">
        <span>Mode</span>
        <select
          className="sstv-select"
          value={meta.mode}
          disabled={disabled}
          onChange={(e) =>
            void adjust(meta.id, {
              mode: e.target.value,
              slantPpm: 0,
              shiftPx: 0,
            })
          }
          title="Decode the recording again as another mode"
        >
          {modes.map((m) => (
            <option key={m} value={m}>
              {m}
            </option>
          ))}
        </select>
        <button
          type="button"
          className="btn sm"
          disabled={disabled || (slant === 0 && shift === 0)}
          onClick={() => {
            setSlant(0);
            setShift(0);
            void adjust(meta.id, { slantPpm: 0, shiftPx: 0 });
          }}
        >
          RESET
        </button>
        <button
          type="button"
          className="btn sm"
          onClick={() => void replyTo(meta.id)}
          title="Reply: send this picture back with your report"
        >
          REPLY
        </button>
        <span className="sstv-spacer" />
        {confirmDelete ? (
          <>
            <button
              type="button"
              className="btn sm sstv-danger"
              onClick={() => void remove(meta.id)}
            >
              DELETE
            </button>
            <button type="button" className="btn sm" onClick={() => setConfirmDelete(false)}>
              KEEP
            </button>
          </>
        ) : (
          <button
            type="button"
            className="btn sm"
            onClick={() => setConfirmDelete(true)}
            title="Delete this picture from the gallery"
          >
            DELETE…
          </button>
        )}
      </div>
    </div>
  );
}

/** Log the QSO behind a received picture: the FSK-ID callsign pre-filled,
 *  RSV both ways, mode SSTV at the picture's dial and start time. */
function LogRow({ meta }: { meta: SstvImageMeta }) {
  const addLogEntry = useLoggerStore((s) => s.addLogEntry);
  const logError = useLoggerStore((s) => s.error);
  const [call, setCall] = useState(meta.callsign ?? '');
  const [sent, setSent] = useState(DEFAULT_RSV);
  const [rcvd, setRcvd] = useState(DEFAULT_RSV);
  const [state, setState] = useState<'idle' | 'busy' | 'done' | 'failed'>('idle');

  useEffect(() => {
    setCall(meta.callsign ?? '');
    setState('idle');
  }, [meta.id, meta.callsign]);

  const canLog = call.trim().length >= 3 && meta.dialHz > 0 && state !== 'busy' && state !== 'done';
  const log = async () => {
    setState('busy');
    const entry = await addLogEntry({
      callsign: call.trim().toUpperCase(),
      frequencyMhz: meta.dialHz / 1e6,
      band: freqHzToBand(meta.dialHz) ?? '',
      mode: 'SSTV',
      rstSent: sent.trim(),
      rstRcvd: rcvd.trim(),
      comment: `${meta.mode} picture`,
      qsoDateTimeUtc: new Date(meta.startedUnixMs).toISOString(),
    });
    setState(entry ? 'done' : 'failed');
  };

  return (
    <div className="sstv-adjust-row sstv-log">
      <span>Log</span>
      <input
        className="sstv-input sstv-call"
        value={call}
        placeholder="CALL"
        spellCheck={false}
        onChange={(e) => {
          setCall(e.target.value.toUpperCase());
          if (state !== 'busy') setState('idle');
        }}
      />
      <input
        className="sstv-input sstv-rsv"
        value={sent}
        title="RSV sent"
        onChange={(e) => setSent(e.target.value)}
      />
      <input
        className="sstv-input sstv-rsv"
        value={rcvd}
        title="RSV received"
        onChange={(e) => setRcvd(e.target.value)}
      />
      <button
        type="button"
        className={`btn sm ${state === 'done' ? 'active' : ''}`}
        disabled={!canLog}
        onClick={() => void log()}
        title={
          meta.dialHz > 0 ? 'Log this SSTV QSO' : 'No dial frequency recorded for this picture'
        }
      >
        {state === 'done' ? 'LOGGED' : 'LOG'}
      </button>
      {state === 'failed' && (
        <span className="sstv-log-err" title={logError ?? undefined}>
          {logError ?? 'failed'}
        </span>
      )}
    </div>
  );
}

export function SstvWindow() {
  const open = useSstvStore((s) => s.panelOpen);
  const enabled = useSstvStore((s) => s.enabled);
  const current = useSstvStore((s) => s.current);
  const images = useSstvStore((s) => s.images);
  const selectedId = useSstvStore((s) => s.selectedId);
  const pixels = useSstvStore((s) => s.pixels);
  const pixelsRev = useSstvStore((s) => s.pixelsRev);
  const galleryDir = useSstvStore((s) => s.galleryDir);
  const closeWorkspace = useSstvStore((s) => s.closeWorkspace);
  const setEnabled = useSstvStore((s) => s.setEnabled);
  const stopCurrent = useSstvStore((s) => s.stopCurrent);
  const select = useSstvStore((s) => s.select);
  const ensurePixels = useSstvStore((s) => s.ensurePixels);
  const tuneTo = useSstvStore((s) => s.tuneTo);
  const view = useSstvStore((s) => s.view);
  const setView = useSstvStore((s) => s.setView);
  const txBusy = useSstvStore((s) => s.tx?.transmitting === true);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const settingsViewOpen = useLayoutStore((s) => s.settingsViewOpen);
  const [pos, setPos] = useState({ x: window.innerWidth - WIDTH - 24, y: 88 });
  const canvasRef = useRef<HTMLCanvasElement | null>(null);

  const shownId = selectedId ?? current?.id ?? images[0]?.id ?? null;
  const shownMeta =
    shownId == null
      ? null
      : current?.id === shownId
        ? current
        : (images.find((i) => i.id === shownId) ?? null);
  const shownPx = shownId == null ? undefined : pixels[shownId];

  // The newest gallery picture shown by default (nothing selected, nothing
  // live) needs its pixels fetched once — without selecting it, so the
  // window keeps following the next live picture.
  useEffect(() => {
    if (open && shownId != null && !shownPx && shownId !== current?.id) ensurePixels(shownId);
  }, [open, shownId, shownPx, current?.id, ensurePixels]);

  useEffect(() => {
    const c = canvasRef.current;
    if (!c || !shownPx) return;
    if (c.width !== shownPx.width || c.height !== shownPx.height) {
      c.width = shownPx.width;
      c.height = shownPx.height;
    }
    c.getContext('2d')?.putImageData(
      new ImageData(shownPx.rgba, shownPx.width, shownPx.height),
      0,
      0,
    );
  }, [shownPx, pixelsRev, open]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      const t = e.target as HTMLElement | null;
      if (t?.closest('input, textarea, select, [contenteditable="true"]')) return;
      closeWorkspace();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, closeWorkspace]);

  const drag = useRef<{
    id: number;
    sx: number;
    sy: number;
    ox: number;
    oy: number;
  } | null>(null);
  const onHeaderPointerDown = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const target = e.target as HTMLElement;
      if (target.closest('[data-no-drag]')) return;
      e.currentTarget.setPointerCapture(e.pointerId);
      drag.current = {
        id: e.pointerId,
        sx: e.clientX,
        sy: e.clientY,
        ox: pos.x,
        oy: pos.y,
      };
    },
    [pos],
  );
  const onHeaderPointerMove = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    const d = drag.current;
    if (!d || d.id !== e.pointerId) return;
    const x = Math.min(window.innerWidth - 80, Math.max(-WIDTH + 80, d.ox + e.clientX - d.sx));
    const y = Math.min(window.innerHeight - 40, Math.max(48, d.oy + e.clientY - d.sy));
    setPos({ x, y });
  }, []);
  const onHeaderPointerUp = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    if (drag.current?.id === e.pointerId) drag.current = null;
  }, []);

  if (!open || settingsViewOpen) return null;

  const live = shownMeta != null && current?.id === shownMeta.id;
  const status = shownMeta
    ? describe(shownMeta, live)
    : enabled
      ? 'Listening for a VIS header…'
      : 'Decoder off';

  return (
    <div
      className="sstv-window"
      style={{ left: pos.x, top: pos.y, width: WIDTH }}
      role="dialog"
      aria-label="SSTV receive"
    >
      <div
        className="diversity-window-header"
        onPointerDown={onHeaderPointerDown}
        onPointerMove={onHeaderPointerMove}
        onPointerUp={onHeaderPointerUp}
      >
        <span className={`dw-dot ${enabled ? 'on' : ''}`} />
        <span className="dw-title">{txBusy ? 'SSTV · TRANSMITTING' : 'SSTV'}</span>
        <span className="dw-spacer" />
        <button
          type="button"
          className="dw-close"
          data-no-drag
          onClick={() => closeWorkspace()}
          aria-label="Exit SSTV"
        >
          ✕
        </button>
      </div>

      <div className="sstv-toolbar">
        <div className="sstv-tabs" role="tablist">
          <button
            type="button"
            role="tab"
            aria-selected={view === 'rx'}
            className={`btn sm ${view === 'rx' ? 'active' : ''}`}
            onClick={() => setView('rx')}
          >
            RX
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={view === 'tx'}
            className={`btn sm ${view === 'tx' ? 'active' : ''} ${txBusy ? 'sstv-tx-live' : ''}`}
            onClick={() => setView('tx')}
          >
            SEND
          </button>
        </div>
        <button
          type="button"
          className={`btn sm ${enabled ? 'active' : ''}`}
          onClick={() => void setEnabled(!enabled)}
          title={enabled ? 'Stop listening for SSTV' : 'Listen for SSTV on RX1'}
        >
          {enabled ? 'RX ON' : 'RX OFF'}
        </button>
        <button
          type="button"
          className="btn sm"
          disabled={current == null}
          onClick={() => void stopCurrent()}
          title="End the picture being received"
        >
          STOP
        </button>
        <span className={`sstv-status ${live ? 'live' : ''}`} title={status}>
          {status}
        </span>
      </div>

      <div className="sstv-presets">
        {SSTV_PRESETS.map((p) => (
          <button
            type="button"
            key={p.hz}
            className={`sstv-chip ${Math.abs(vfoHz - p.hz) < 500 ? 'sel' : ''}`}
            onClick={() => void tuneTo(p.hz, p.mode)}
            title={`QSY to ${p.label} MHz ${p.mode}`}
          >
            {p.label}
          </button>
        ))}
      </div>

      {view === 'tx' ? (
        <SstvSendPanel />
      ) : (
        <>
          <div className="sstv-canvas-wrap">
            {shownPx ? (
              <canvas
                ref={canvasRef}
                className="sstv-canvas"
                style={{ aspectRatio: `${shownPx.width} / ${shownPx.height}` }}
              />
            ) : (
              <div className="sstv-empty">No picture yet</div>
            )}
          </div>

          {shownMeta && !live && (
            <>
              <AdjustBar meta={shownMeta} />
              <div className="sstv-adjust">
                <LogRow meta={shownMeta} />
              </div>
            </>
          )}

          {(current || images.length > 0) && (
            <div className="sstv-strip" title={galleryDir ? `Saved in ${galleryDir}` : undefined}>
              {current && (
                <button
                  type="button"
                  className={`sstv-chip live ${selectedId == null || selectedId === current.id ? 'sel' : ''}`}
                  onClick={() => select(null)}
                  title="Follow the picture being received"
                >
                  LIVE
                </button>
              )}
              {images.map((m) => (
                <button
                  type="button"
                  key={m.id}
                  className={`sstv-chip ${shownId === m.id && !live ? 'sel' : ''}`}
                  onClick={() => select(m.id)}
                  title={describe(m, false)}
                >
                  {m.callsign ?? m.mode} {utcHm(m.startedUnixMs)}
                </button>
              ))}
            </div>
          )}
        </>
      )}
    </div>
  );
}
