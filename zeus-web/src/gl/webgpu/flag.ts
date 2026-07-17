// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Opt-in flag for the experimental WebGPU 3D heightfield waterfall.
//
// OFF by default: the production surface stays on the proven WebGL2 renderer
// (gl/waterfall.ts) until the heightfield path is benchmarked and signed off.
// Two ways to enable, both read live so no rebuild is needed:
//   • URL:          ?webgpuWaterfall=1   (sticky — persists to localStorage)
//   • localStorage: zeus.waterfall.webgpu = "1"
// Append ?webgpuWaterfall=0 to force back off.
//
// This only expresses INTENT. Actual use still requires the capability probe
// (caps.ts) to confirm the device supports WebGPU — see resolveWaterfallRenderer.

const STORAGE_KEY = 'zeus.waterfall.webgpu';
const URL_PARAM = 'webgpuWaterfall';
const PAN_STORAGE_KEY = 'zeus.panadapter.webgpu3d';
const PAN_URL_PARAM = 'webgpuPanadapter';

function readUrlOverride(param = URL_PARAM): boolean | null {
  try {
    if (typeof window === 'undefined') return null;
    const raw = new URLSearchParams(window.location.search).get(param);
    if (raw === null) return null;
    return raw === '1' || raw === 'true';
  } catch {
    return null;
  }
}

function readStored(key = STORAGE_KEY): '1' | '0' | null {
  try {
    if (typeof localStorage === 'undefined') return null;
    const v = localStorage.getItem(key);
    return v === '1' || v === '0' ? v : null;
  } catch {
    return null;
  }
}

function writeStored(enabled: boolean, key = STORAGE_KEY): void {
  try {
    if (typeof localStorage === 'undefined') return;
    // Store the explicit choice, including the emergency-off override.
    localStorage.setItem(key, enabled ? '1' : '0');
  } catch {
    // private mode / quota — URL override still works for this session.
  }
}

/** Whether the WebGPU heightfield is the active waterfall. Default ON for the
 *  normal profile; low-power hosts pass defaultEnabled=false so the flat WebGL
 *  surface is preferred unless the operator explicitly opts back in. Actual use
 *  still requires the capability probe (caps.ts) and a clean renderer init; on
 *  any failure WaterfallSurface falls back to WebGL. */
export function isWebGpuWaterfallEnabled(defaultEnabled = true): boolean {
  const override = readUrlOverride();
  if (override !== null) {
    writeStored(override);
    return override;
  }
  const stored = readStored();
  if (stored !== null) return stored === '1';
  return defaultEnabled;
}

/** Programmatic toggle (e.g. a Settings switch). */
export function setWebGpuWaterfallEnabled(enabled: boolean): void {
  writeStored(enabled);
}

/** Whether the WebGPU 3D panadapter is the active pan surface. Default ON, with
 *  `?webgpuPanadapter=0` as the sticky emergency fallback to the WebGL2 trace. */
export function isWebGpuPanadapterEnabled(): boolean {
  const override = readUrlOverride(PAN_URL_PARAM);
  if (override !== null) {
    writeStored(override, PAN_STORAGE_KEY);
    return override;
  }
  return readStored(PAN_STORAGE_KEY) !== '0';
}

export function setWebGpuPanadapterEnabled(enabled: boolean): void {
  writeStored(enabled, PAN_STORAGE_KEY);
}
