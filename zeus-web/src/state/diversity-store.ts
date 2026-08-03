// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Diversity combiner state — the frontend face of POST /api/rx/diversity
// (backend: DspPipelineService managed complex combine, Thetis-DiversityForm
// parity). The weight is one complex number w = gain·e^{jθ}; the panel edits
// it as a point in the complex plane, so this store keeps (gain, phaseDeg)
// plus four persisted memory slots — saved nulls are per-QRM-source, so each
// remembers the weight, the depth achieved, and the dial it was won on.
//
// POST discipline: drag gestures stream weight changes, so setWeight applies
// locally at once (the pad must track the finger) and coalesces the network
// send through a 120 ms trailing throttle — same debounce philosophy as the
// tile-scroll capture. Discrete actions (enable, source, recall) post
// immediately. Every field of the endpoint is optional; we always send the
// full weight for determinism.

import { create } from 'zustand';

export interface DiversityMemory {
  label: string;
  gain: number;
  phaseDeg: number;
  depthDb: number; // null depth measured at save time (display only)
  dialMhz: string; // dial at save time (display only)
}

export interface DiversityState {
  enabled: boolean;
  gain: number; // 0..2, matches Thetis range
  phaseDeg: number; // 0..360
  sourceRx: number; // source receiver index (default 1 = RX2/ADC1)
  mems: (DiversityMemory | null)[];
  setEnabled: (on: boolean) => void;
  setSourceRx: (rx: number) => void;
  setWeight: (gain: number, phaseDeg: number) => void;
  saveMem: (slot: number, depthDb: number, dialMhz: string) => void;
  clearMem: (slot: number) => void;
  recallMem: (slot: number) => void;
}

const MEM_KEY = 'zeus.diversity.mems';

function loadMems(): (DiversityMemory | null)[] {
  try {
    const raw = localStorage.getItem(MEM_KEY);
    if (!raw) return [null, null, null, null];
    const parsed = JSON.parse(raw) as (DiversityMemory | null)[];
    const out: (DiversityMemory | null)[] = [null, null, null, null];
    for (let i = 0; i < 4; i++) {
      const m = parsed[i];
      if (
        m &&
        typeof m.gain === 'number' &&
        Number.isFinite(m.gain) &&
        typeof m.phaseDeg === 'number' &&
        Number.isFinite(m.phaseDeg)
      )
        out[i] = m;
    }
    return out;
  } catch {
    return [null, null, null, null];
  }
}

function persistMems(mems: (DiversityMemory | null)[]): void {
  try {
    localStorage.setItem(MEM_KEY, JSON.stringify(mems));
  } catch {
    /* storage unavailable — memories become session-only */
  }
}

export interface DiversityPayload {
  enabled?: boolean;
  gain?: number;
  phaseDeg?: number;
  sourceRx?: number;
}

/** Exported for tests: the exact body a state snapshot posts. */
export function buildDiversityPayload(s: {
  enabled: boolean;
  gain: number;
  phaseDeg: number;
  sourceRx: number;
}): DiversityPayload {
  return {
    enabled: s.enabled,
    gain: Math.max(0, Math.min(2, s.gain)),
    phaseDeg: ((s.phaseDeg % 360) + 360) % 360,
    sourceRx: s.sourceRx,
  };
}

function post(payload: DiversityPayload): void {
  void fetch('/api/rx/diversity', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  }).catch(() => {
    /* radio away — the panel keeps local state; next action retries */
  });
}

// 120 ms trailing throttle for the drag stream: leading send for instant
// engage, then at most one send per window carrying the latest weight.
let throttleTimer: ReturnType<typeof setTimeout> | null = null;
let pendingSnapshot: DiversityPayload | null = null;

function postThrottled(payload: DiversityPayload): void {
  if (throttleTimer) {
    pendingSnapshot = payload;
    return;
  }
  post(payload);
  throttleTimer = setTimeout(() => {
    throttleTimer = null;
    if (pendingSnapshot) {
      const p = pendingSnapshot;
      pendingSnapshot = null;
      postThrottled(p);
    }
  }, 120);
}

/** Test hook: drop any pending throttle state between cases. */
export function _resetDiversityThrottleForTests(): void {
  if (throttleTimer) clearTimeout(throttleTimer);
  throttleTimer = null;
  pendingSnapshot = null;
}

export const useDiversityStore = create<DiversityState>((set, get) => ({
  enabled: false,
  gain: 1.0,
  phaseDeg: 0,
  sourceRx: 1,
  mems: loadMems(),

  setEnabled: (on) => {
    set({ enabled: on });
    post(buildDiversityPayload(get()));
  },
  setSourceRx: (rx) => {
    set({ sourceRx: rx });
    post(buildDiversityPayload(get()));
  },
  setWeight: (gain, phaseDeg) => {
    set({
      gain: Math.max(0, Math.min(2, gain)),
      phaseDeg: ((phaseDeg % 360) + 360) % 360,
    });
    postThrottled(buildDiversityPayload(get()));
  },
  saveMem: (slot, depthDb, dialMhz) => {
    const s = get();
    const mems = s.mems.slice();
    mems[slot] = {
      label: `M${slot + 1}`,
      gain: s.gain,
      phaseDeg: s.phaseDeg,
      depthDb,
      dialMhz,
    };
    set({ mems });
    persistMems(mems);
  },
  clearMem: (slot) => {
    const mems = get().mems.slice();
    mems[slot] = null;
    set({ mems });
    persistMems(mems);
  },
  recallMem: (slot) => {
    const m = get().mems[slot];
    if (!m) return;
    set({ gain: m.gain, phaseDeg: m.phaseDeg, enabled: true });
    post(buildDiversityPayload(get()));
  },
}));
