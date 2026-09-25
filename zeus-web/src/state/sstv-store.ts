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
// Pixels live here as RGBA buffers keyed by picture id (mutated in place;
// `pixelsRev` bumps so the canvas redraws). SSE replays nothing, so refresh()
// runs on every stream (re)open and re-fetches the picture in progress.

import { create } from 'zustand';
import {
  getSstvImage,
  getSstvStatus,
  postSstvEnabled,
  postSstvStop,
  setMode,
  type RxMode,
  type SstvImageMeta,
} from '../api/client';
import { useConnectionStore } from './connection-store';
import { useTxStore } from './tx-store';
import { snapshotRadio, type RadioModeSnapshot } from './digital-mode';

export type SstvEvent =
  | { kind: 'start'; image: SstvImageMeta }
  | { kind: 'rows'; id: number; row: number; count: number; width: number; rgb: string }
  | { kind: 'end'; image: SstvImageMeta }
  | { kind: 'discard'; id: number };

export interface SstvPixels {
  width: number;
  height: number;
  rgba: Uint8ClampedArray<ArrayBuffer>;
}

export interface SstvState {
  /** SSTV is engaged as a mode (button depressed, window up, decoder on). */
  panelOpen: boolean;
  /** Pre-entry radio config, handed on when switching to FT8/FT4/WSPR. */
  priorRadio: RadioModeSnapshot | null;
  /** The sideband we switched to on entry, or null if the mode was kept. */
  forcedMode: RxMode | null;
  enabled: boolean;
  current: SstvImageMeta | null;
  /** Finished pictures this session, newest first. */
  images: SstvImageMeta[];
  /** Picture on screen; null follows the live one (or the newest). */
  selectedId: number | null;
  pixels: Record<number, SstvPixels>;
  pixelsRev: number;
  openWorkspace: () => void;
  closeWorkspace: (opts?: { restore?: boolean }) => void;
  setEnabled: (on: boolean) => Promise<void>;
  stopCurrent: () => Promise<void>;
  select: (id: number | null) => void;
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

function decodeBase64(b64: string): Uint8Array {
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

function prune(pixels: Record<number, SstvPixels>, keep: number[]): Record<number, SstvPixels> {
  const ids = new Set(keep.slice(0, KEEP_PIXELS));
  const next: Record<number, SstvPixels> = {};
  for (const [k, v] of Object.entries(pixels)) if (ids.has(Number(k))) next[Number(k)] = v;
  return next;
}

export const useSstvStore = create<SstvState>((set, get) => {
  /** Fetch a whole picture (after the end-of-picture re-render, or on rehydrate). */
  const loadImage = async (id: number): Promise<void> => {
    try {
      const dto = await getSstvImage(id);
      const px = blank(dto.meta.width, dto.meta.height);
      blit(px, 0, decodeBase64(dto.rgb));
      set((s) => ({ pixels: { ...s.pixels, [id]: px }, pixelsRev: s.pixelsRev + 1 }));
    } catch {
      /* picture aged out of the backend ring — nothing to show */
    }
  };

  return {
    panelOpen: false,
    priorRadio: null,
    forcedMode: null,
    enabled: false,
    current: null,
    images: [],
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
    },

    closeWorkspace: (opts) => {
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
      if (id != null && !get().pixels[id]) void loadImage(id);
    },

    refresh: async () => {
      try {
        const st = await getSstvStatus();
        set((s) => ({
          enabled: st.enabled,
          current: st.current,
          images: st.images,
          pixels: prune(s.pixels, [
            ...(st.current ? [st.current.id] : []),
            ...st.images.map((i) => i.id),
          ]),
        }));
        const show = st.current?.id ?? st.images[0]?.id;
        if (show != null) void loadImage(show);
      } catch {
        /* backend without SSTV (older build) — stay idle */
      }
    },

    ingest: (ev) => {
      switch (ev.kind) {
        case 'start':
          set((s) => ({
            current: ev.image,
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
          set((s) => {
            const images = [ev.image, ...s.images.filter((i) => i.id !== ev.image.id)].slice(0, 12);
            return {
              current: s.current?.id === ev.image.id ? null : s.current,
              images,
              pixels: prune(s.pixels, [ev.image.id, ...images.map((i) => i.id)]),
            };
          });
          void loadImage(ev.image.id);
          break;
        case 'discard':
          set((s) => {
            const pixels = { ...s.pixels };
            delete pixels[ev.id];
            return {
              current: s.current?.id === ev.id ? null : s.current,
              pixels,
              pixelsRev: s.pixelsRev + 1,
            };
          });
          break;
      }
    },
  };
});
