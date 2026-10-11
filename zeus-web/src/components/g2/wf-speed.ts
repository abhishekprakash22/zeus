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
// The G2 pane's SPD key: a per-receiver waterfall speed multiplier that
// cycles ×½ → ×1 → ×2 → ×4. Remembered in two vaults like the pane divider:
// localStorage for durable browser profiles (remote desktops, phones) and the
// radio's /api/ui/wf-speed for the kiosk, whose Chromium profile is thrown
// away every launch. The radio's copy wins when it exists.

export const WF_SPEED_FACTORS = [0.5, 1, 2, 4] as const;
export type WfSpeedFactor = (typeof WF_SPEED_FACTORS)[number];

const storageKey = (rxIndex: number) => `zeus.g2.wfSpeed.rx${rxIndex}`;

/** Snap any stored/served value onto the SPD steps; anything else is ×1. */
export function normalizeWfSpeed(raw: unknown): WfSpeedFactor {
  const v = typeof raw === 'string' ? Number.parseFloat(raw) : raw;
  return (WF_SPEED_FACTORS as readonly unknown[]).includes(v) ? (v as WfSpeedFactor) : 1;
}

/** The next step of the SPD cycle. */
export function nextWfSpeed(f: WfSpeedFactor): WfSpeedFactor {
  const i = WF_SPEED_FACTORS.indexOf(f);
  return WF_SPEED_FACTORS[(i + 1) % WF_SPEED_FACTORS.length]!;
}

/** Instant restore from this browser's own copy (×1 when none). */
export function readLocalWfSpeed(rxIndex: number): WfSpeedFactor {
  try {
    return normalizeWfSpeed(localStorage.getItem(storageKey(rxIndex)));
  } catch {
    return 1;
  }
}

/** Remember a choice in both vaults. Best-effort; never throws. */
export function saveWfSpeed(rxIndex: number, factor: WfSpeedFactor): void {
  try {
    localStorage.setItem(storageKey(rxIndex), String(factor));
  } catch {
    // private mode / quota — the radio's copy below still lands.
  }
  void fetch('/api/ui/wf-speed', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ rx: rxIndex, factor }),
  }).catch(() => {});
}

/** The radio's copy for this receiver, or null when it has none. */
export async function fetchServerWfSpeed(rxIndex: number): Promise<WfSpeedFactor | null> {
  try {
    const r = await fetch('/api/ui/wf-speed');
    if (!r.ok) return null;
    const map = (await r.json()) as Record<string, unknown> | null;
    if (!map || !(String(rxIndex) in map)) return null;
    return normalizeWfSpeed(map[String(rxIndex)]);
  } catch {
    return null;
  }
}
