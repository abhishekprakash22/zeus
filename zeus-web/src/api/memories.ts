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

// Memory channels (radio-side list, GET/POST/DELETE /api/memories) and the
// operator spot post to the connected DX cluster node.

import type { RxMode } from './client';

export type MemoryChannel = {
  id: string;
  name: string;
  hz: number;
  mode: RxMode;
  filterLowHz: number | null;
  filterHighHz: number | null;
  createdUnixMs: number;
};

const MODES: readonly RxMode[] = ['LSB', 'USB', 'CWL', 'CWU', 'AM', 'FM', 'SAM', 'DSB', 'DIGL', 'DIGU', 'FREEDV'];

/** Accept the server's list defensively: unknown shapes are dropped, never thrown on. */
export function parseMemoryList(raw: unknown): MemoryChannel[] {
  if (!Array.isArray(raw)) return [];
  const out: MemoryChannel[] = [];
  for (const r of raw) {
    if (!r || typeof r !== 'object') continue;
    const o = r as Record<string, unknown>;
    const hz = Number(o.hz);
    const mode = String(o.mode ?? '') as RxMode;
    if (typeof o.id !== 'string' || !Number.isFinite(hz) || hz <= 0 || !MODES.includes(mode)) continue;
    const lo = typeof o.filterLowHz === 'number' ? o.filterLowHz : null;
    const hi = typeof o.filterHighHz === 'number' ? o.filterHighHz : null;
    out.push({
      id: o.id,
      name: typeof o.name === 'string' ? o.name : '',
      hz,
      mode,
      filterLowHz: lo,
      filterHighHz: hi,
      createdUnixMs: Number(o.createdUnixMs) || 0,
    });
  }
  return out.sort((a, b) => a.hz - b.hz);
}

async function send(url: string, init?: RequestInit): Promise<MemoryChannel[]> {
  const res = await fetch(url, init);
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return parseMemoryList(await res.json());
}

export function fetchMemories(): Promise<MemoryChannel[]> {
  return send('/api/memories');
}

export function postMemory(m: {
  name?: string;
  hz: number;
  mode: RxMode;
  filterLowHz?: number | null;
  filterHighHz?: number | null;
}): Promise<MemoryChannel[]> {
  return send('/api/memories', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ ...m, hz: Math.round(m.hz), createdUnixMs: 0 }),
  });
}

export function deleteMemory(id: string): Promise<MemoryChannel[]> {
  return send(`/api/memories/${encodeURIComponent(id)}`, { method: 'DELETE' });
}

/** Same rule the server applies: 3–15 of A–Z 0–9 /, at least one digit. */
export function isPlausibleCallsign(call: string): boolean {
  const c = call.trim().toUpperCase();
  return /^[A-Z0-9/]{3,15}$/.test(c) && /[0-9]/.test(c);
}

export type SpotResult = { ok: true } | { ok: false; error: string };

export async function postClusterSpot(freqHz: number, call: string, comment: string): Promise<SpotResult> {
  try {
    const res = await fetch('/api/dxcluster/spot', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ freqHz: Math.round(freqHz), call: call.trim().toUpperCase(), comment }),
    });
    if (res.ok) return { ok: true };
    let error = `${res.status} ${res.statusText}`;
    try {
      const body = (await res.json()) as { error?: unknown };
      if (typeof body.error === 'string') error = body.error;
    } catch {
      /* non-JSON error body */
    }
    return { ok: false, error };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : 'network error' };
  }
}
