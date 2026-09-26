// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// SSTV state. The backend SstvService decodes pictures and streams `sstv` SSE
// events on the Digital stream:
//   kind:'start'   — a VIS header was decoded; picture metadata
//   kind:'rows'    — freshly decoded rows (base64 RGB)
//   kind:'end'     — picture done; its rows were re-rendered with the final
//                    slant fit, so the full picture is re-fetched
//   kind:'discard' — a false start; drop it
//   kind:'update'  — gallery edit (re-render, late FSK-ID callsign)
//   kind:'removed' — deleted from the gallery
//   kind:'tx'      — transmitter status (keyed, progress, why it stopped)
// Pixels live here as RGBA buffers keyed by picture id (mutated in place;
// `pixelsRev` bumps so the canvas redraws). Only the pictures actually shown
// are fetched — the gallery can list hundreds. SSE replays nothing, so
// refresh() runs on every stream (re)open.

import { create } from 'zustand';
import {
  deleteSstvImage,
  getSstvImage,
  getSstvStatus,
  getSstvTx,
  postSstvAdjust,
  postSstvTx,
  postSstvTxHalt,
  postSstvEnabled,
  postSstvStop,
  setMode,
  setVfo,
  type RxMode,
  type SstvAdjust,
  type SstvImageMeta,
  type SstvModeInfo,
  type SstvTxStatus,
} from '../api/client';
import { useConnectionStore } from './connection-store';
import { useTxStore } from './tx-store';
import { snapshotRadio, type RadioModeSnapshot } from './digital-mode';

export type SstvEvent =
  | { kind: 'start'; image: SstvImageMeta }
  | { kind: 'rows'; id: number; row: number; count: number; width: number; rgb: string }
  | { kind: 'end'; image: SstvImageMeta }
  | { kind: 'update'; image: SstvImageMeta }
  | { kind: 'discard'; id: number }
  | { kind: 'removed'; id: number }
  | { kind: 'tx'; tx: SstvTxStatus };

export interface SstvPixels {
  width: number;
  height: number;
  rgba: Uint8ClampedArray<ArrayBuffer>;
}

/** The analog SSTV calling frequencies, with the sideband each is worked on. */
export const SSTV_PRESETS: readonly { label: string; hz: number; mode: RxMode }[] = [
  { label: '3.845', hz: 3_845_000, mode: 'LSB' },
  { label: '7.171', hz: 7_171_000, mode: 'LSB' },
  { label: '14.230', hz: 14_230_000, mode: 'USB' },
  { label: '14.233', hz: 14_233_000, mode: 'USB' },
  { label: '21.340', hz: 21_340_000, mode: 'USB' },
  { label: '28.680', hz: 28_680_000, mode: 'USB' },
];

export interface SstvState {
  /** SSTV is engaged as a mode (button depressed, window up, decoder on). */
  panelOpen: boolean;
  /** Pre-entry radio config, handed on when switching to FT8/FT4/WSPR. */
  priorRadio: RadioModeSnapshot | null;
  /** The sideband we switched to on entry, or null if the mode was kept. */
  forcedMode: RxMode | null;
  enabled: boolean;
  current: SstvImageMeta | null;
  /** Finished pictures (session + gallery on disk), newest first. */
  images: SstvImageMeta[];
  modes: string[];
  modeInfos: SstvModeInfo[];
  galleryDir: string | null;
  /** Which half of the window is showing. */
  view: 'rx' | 'tx';
  /** Transmitter status from the backend (null until first known). */
  tx: SstvTxStatus | null;
  /** Why the last SEND was refused, shown until the next attempt. */
  txError: string | null;
  /** Composer: the picture to send (before scaling/overlay), mode, texts. */
  txSource: ImageBitmap | null;
  txMode: string;
  txTop: string;
  txBottom: string;
  txFsk: boolean;
  /** Picture on screen; null follows the live one (or the newest). */
  selectedId: number | null;
  pixels: Record<number, SstvPixels>;
  pixelsRev: number;
  openWorkspace: () => void;
  closeWorkspace: (opts?: { restore?: boolean }) => void;
  setEnabled: (on: boolean) => Promise<void>;
  stopCurrent: () => Promise<void>;
  select: (id: number | null) => void;
  /** Fetch a picture's pixels if not held, without changing the selection. */
  ensurePixels: (id: number) => void;
  adjust: (id: number, adj: SstvAdjust) => Promise<void>;
  remove: (id: number) => Promise<void>;
  tuneTo: (hz: number, mode: RxMode) => Promise<void>;
  setView: (view: 'rx' | 'tx') => void;
  setTxSource: (src: ImageBitmap | null) => void;
  setTxOptions: (opts: Partial<Pick<SstvState, 'txMode' | 'txTop' | 'txBottom' | 'txFsk'>>) => void;
  /** Quick reply: a received picture becomes the one to send. */
  replyTo: (id: number) => Promise<void>;
  send: (rgbBase64: string, fskId: string | null) => Promise<void>;
  halt: () => Promise<void>;
  refresh: () => Promise<void>;
  ingest: (ev: SstvEvent) => void;
}

const KEEP_PIXELS = 13;

/** SSTV rides an SSB receiver. The analog SSTV convention: LSB below 10 MHz
 *  (3.845, 7.171), USB above (14.230, 21.340, 28.680). */
const SSB_MODES: readonly RxMode[] = ['USB', 'LSB', 'DIGU', 'DIGL'];
export function sstvSidebandFor(vfoHz: number): RxMode {
  return vfoHz < 10_000_000 ? 'LSB' : 'USB';
}

function txActive(): boolean {
  const tx = useTxStore.getState();
  return tx.moxOn || tx.tunOn;
}

function decodeBase64(b64: string): Uint8Array<ArrayBuffer> {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

function blank(width: number, height: number): SstvPixels {
  const rgba = new Uint8ClampedArray(width * height * 4);
  for (let i = 3; i < rgba.length; i += 4) rgba[i] = 255;
  return { width, height, rgba };
}

/** Copy `rgb` (3 bytes/px) into `px` starting at pixel row `row`. */
function blit(px: SstvPixels, row: number, rgb: Uint8Array): void {
  let o = row * px.width * 4;
  for (let i = 0; i + 2 < rgb.length && o < px.rgba.length; i += 3, o += 4) {
    px.rgba[o] = rgb[i]!;
    px.rgba[o + 1] = rgb[i + 1]!;
    px.rgba[o + 2] = rgb[i + 2]!;
  }
}

/** Decode a gallery PNG to RGBA through the browser's own decoder. */
async function pngPixels(b64: string): Promise<SstvPixels> {
  const bmp = await createImageBitmap(new Blob([decodeBase64(b64)], { type: 'image/png' }));
  const c = document.createElement('canvas');
  c.width = bmp.width;
  c.height = bmp.height;
  const ctx = c.getContext('2d');
  if (!ctx) throw new Error('no 2d context');
  ctx.drawImage(bmp, 0, 0);
  bmp.close();
  return { width: c.width, height: c.height, rgba: ctx.getImageData(0, 0, c.width, c.height).data };
}

function withMeta(list: SstvImageMeta[], m: SstvImageMeta): SstvImageMeta[] {
  return list.some((i) => i.id === m.id) ? list.map((i) => (i.id === m.id ? m : i)) : list;
}

export const useSstvStore = create<SstvState>((set, get) => {
  /** Keep pixels only for the pictures most likely to be looked at again. */
  const prune = (pixels: Record<number, SstvPixels>): Record<number, SstvPixels> => {
    const s = get();
    const keep = new Set<number>(
      [s.current?.id, s.selectedId, ...s.images.slice(0, KEEP_PIXELS).map((i) => i.id)].filter(
        (x): x is number => x != null,
      ),
    );
    const next: Record<number, SstvPixels> = {};
    for (const [k, v] of Object.entries(pixels)) if (keep.has(Number(k))) next[Number(k)] = v;
    return next;
  };

  /** Fetch a whole picture (after a re-render, or to show a gallery one). */
  const loadImage = async (id: number): Promise<void> => {
    try {
      const dto = await getSstvImage(id);
      let px: SstvPixels;
      if (dto.rgb != null) {
        px = blank(dto.meta.width, dto.meta.height);
        blit(px, 0, decodeBase64(dto.rgb));
      } else if (dto.png != null) {
        px = await pngPixels(dto.png);
      } else {
        return;
      }
      set((s) => ({ pixels: prune({ ...s.pixels, [id]: px }), pixelsRev: s.pixelsRev + 1 }));
    } catch {
      /* picture gone (deleted, or its file removed by hand) — nothing to show */
    }
  };

  return {
    panelOpen: false,
    priorRadio: null,
    forcedMode: null,
    enabled: false,
    current: null,
    images: [],
    modes: [],
    modeInfos: [],
    galleryDir: null,
    view: 'rx',
    tx: null,
    txError: null,
    txSource: null,
    txMode: 'Martin 1',
    txTop: '',
    txBottom: '',
    txFsk: true,
    selectedId: null,
    pixels: {},
    pixelsRev: 0,

    // Entering SSTV never QSYs (unlike FT8/WSPR there is no single dial to go
    // to — operators tune across 14.230–14.233 etc.). It only makes sure the
    // receiver is on a sideband, and exit undoes exactly that, nothing more:
    // restoring the entry VFO would throw away the operator's tuning.
    openWorkspace: () => {
      if (get().panelOpen) return;
      const prior = snapshotRadio();
      let forcedMode: RxMode | null = null;
      if (!SSB_MODES.includes(prior.mode) && !txActive()) {
        forcedMode = sstvSidebandFor(prior.vfoHz);
        void setMode(forcedMode).catch(() => undefined);
      }
      set({ panelOpen: true, priorRadio: prior, forcedMode });
      if (!get().enabled) void get().setEnabled(true);
      void get().refresh();
    },

    closeWorkspace: (opts) => {
      // Leaving SSTV never leaves a picture going out behind a closed window.
      if (get().tx?.transmitting) void get().halt();
      const { priorRadio, forcedMode } = get();
      set({ panelOpen: false, priorRadio: null, forcedMode: null });
      void get().setEnabled(false);
      const restore = opts?.restore !== false;
      if (
        restore &&
        priorRadio &&
        forcedMode &&
        !txActive() &&
        useConnectionStore.getState().mode === forcedMode
      ) {
        void setMode(priorRadio.mode).catch(() => undefined);
      }
    },

    setEnabled: async (on) => {
      set({ enabled: on });
      try {
        const st = await postSstvEnabled(on);
        set({ enabled: st.enabled });
      } catch {
        set({ enabled: !on });
      }
    },

    stopCurrent: async () => {
      try {
        await postSstvStop();
      } catch {
        /* next status refresh reconciles */
      }
    },

    select: (id) => {
      set({ selectedId: id });
      if (id != null) get().ensurePixels(id);
    },

    ensurePixels: (id) => {
      if (!get().pixels[id]) void loadImage(id);
    },

    adjust: async (id, adj) => {
      try {
        const meta = await postSstvAdjust(id, adj);
        set((s) => ({ images: withMeta(s.images, meta) }));
        await loadImage(id);
      } catch {
        /* no longer adjustable (aged out) — the 'update' SSE keeps meta honest */
      }
    },

    remove: async (id) => {
      try {
        await deleteSstvImage(id);
      } catch {
        return;
      }
      get().ingest({ kind: 'removed', id });
    },

    // Band presets: QSY + the preset's sideband. The VFO first, the mode last,
    // so the server's per-band mode recall (a cross-band SetVfo) can't win.
    tuneTo: async (hz, mode) => {
      if (txActive()) return;
      try {
        await setVfo(hz);
        await setMode(mode);
      } catch {
        /* best effort — the operator can tune by hand */
      }
    },

    setView: (view) => set({ view }),

    setTxSource: (src) => set({ txSource: src }),

    setTxOptions: (opts) => set(opts),

    replyTo: async (id) => {
      const px = get().pixels[id];
      const meta = get().images.find((i) => i.id === id);
      if (!px) return;
      try {
        const bmp = await createImageBitmap(new ImageData(px.rgba, px.width, px.height));
        const rsv = meta?.callsign ? `${meta.callsign} UR 595` : 'UR 595';
        set({ txSource: bmp, txBottom: rsv, view: 'tx', txError: null });
      } catch {
        /* no bitmap support — the operator can still load a file */
      }
    },

    send: async (rgbBase64, fskId) => {
      set({ txError: null });
      try {
        const tx = await postSstvTx({ mode: get().txMode, rgb: rgbBase64, fskId });
        set({ tx });
      } catch (err) {
        set({ txError: err instanceof Error ? err.message : 'refused' });
      }
    },

    halt: async () => {
      try {
        set({ tx: await postSstvTxHalt() });
      } catch {
        /* the 'tx' SSE frame reconciles */
      }
    },

    refresh: async () => {
      try {
        const [st, tx] = await Promise.all([getSstvStatus(), getSstvTx().catch(() => null)]);
        set((s) => ({
          enabled: st.enabled,
          current: st.current,
          images: st.images,
          modes: st.modes,
          modeInfos: st.modeInfos ?? [],
          tx: tx ?? s.tx,
          galleryDir: st.galleryDir,
          selectedId:
            s.selectedId != null && st.images.some((i) => i.id === s.selectedId)
              ? s.selectedId
              : null,
        }));
        set((s) => ({ pixels: prune(s.pixels) }));
        const show = get().selectedId ?? st.current?.id ?? st.images[0]?.id;
        if (show != null && (show === st.current?.id || !get().pixels[show])) void loadImage(show);
      } catch {
        /* backend without SSTV (older build) — stay idle */
      }
    },

    ingest: (ev) => {
      switch (ev.kind) {
        case 'start':
          // Also a picture resumed after a fade: it leaves the finished list.
          set((s) => ({
            current: ev.image,
            images: s.images.filter((i) => i.id !== ev.image.id),
            pixels: { ...s.pixels, [ev.image.id]: blank(ev.image.width, ev.image.height) },
            pixelsRev: s.pixelsRev + 1,
          }));
          break;
        case 'rows': {
          const s = get();
          const px = s.pixels[ev.id];
          if (!px) return;
          blit(px, ev.row, decodeBase64(ev.rgb));
          const cur = s.current;
          set({
            pixelsRev: s.pixelsRev + 1,
            current:
              cur && cur.id === ev.id
                ? { ...cur, rowsDone: Math.max(cur.rowsDone, ev.row + ev.count) }
                : cur,
          });
          break;
        }
        case 'end':
          set((s) => ({
            current: s.current?.id === ev.image.id ? null : s.current,
            images: [ev.image, ...s.images.filter((i) => i.id !== ev.image.id)],
          }));
          set((s) => ({ pixels: prune(s.pixels) }));
          void loadImage(ev.image.id);
          break;
        case 'update': {
          const prev = get().images.find((i) => i.id === ev.image.id);
          set((s) => ({ images: withMeta(s.images, ev.image) }));
          // A re-render changes pixels; a late FSK-ID callsign doesn't.
          const rerendered =
            prev != null &&
            (prev.slantPpm !== ev.image.slantPpm ||
              prev.shiftPx !== ev.image.shiftPx ||
              prev.mode !== ev.image.mode);
          if (rerendered && get().pixels[ev.image.id]) void loadImage(ev.image.id);
          break;
        }
        case 'tx':
          set({ tx: ev.tx });
          break;
        case 'discard':
        case 'removed':
          set((s) => {
            const pixels = { ...s.pixels };
            delete pixels[ev.id];
            return {
              current: s.current?.id === ev.id ? null : s.current,
              images: s.images.filter((i) => i.id !== ev.id),
              selectedId: s.selectedId === ev.id ? null : s.selectedId,
              pixels,
              pixelsRev: s.pixelsRev + 1,
            };
          });
          break;
      }
    },
  };
});
