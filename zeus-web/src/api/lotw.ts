// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Licensed under the GNU General Public License v2.0-or-later. See the LICENSE
// file at the repository root for the full text.

export type LotwStationDefaults = {
  rig: string | null;
  antenna: string | null;
  txPowerW: number | null;
};

export type LotwStatus = {
  configured: boolean;
  stationLocation: string | null;
  lastSyncUtc: string | null;
  lastQslSince: string | null;
  lastResult: string | null;
  tqslPath: string | null;
  stationDefaults: LotwStationDefaults;
};

export type LotwCredentialsRequest = {
  username: string | null;
  password: string | null;
  stationLocation: string | null;
};

export type LotwSettingsRequest = {
  autoSync?: boolean | null;
  stationLocation?: string | null;
  stationDefaults?: LotwStationDefaults | null;
};

export type LotwConfirmationCounts = {
  fetched: number;
  matched: number;
  unmatched: number;
};

export type LotwSyncResponse = {
  lotw: LotwConfirmationCounts;
  qrz: LotwConfirmationCounts;
  lastQslSince: string | null;
  lastSyncUtc: string | null;
  message: string;
};

export type LotwUploadResponse = {
  success: boolean;
  message: string;
  uploadedCount: number;
  exportedPath: string | null;
  tqslPath: string | null;
  exitCode: number | null;
  errorTail: string | null;
  tqslMissing: boolean;
};

async function errorFromResponse(response: Response): Promise<Error> {
  const text = await response.text();
  let message = text;
  try {
    const parsed = JSON.parse(text) as { error?: unknown; message?: unknown };
    if (typeof parsed.error === 'string') message = parsed.error;
    else if (typeof parsed.message === 'string') message = parsed.message;
  } catch {
    /* keep raw text */
  }
  return new Error(message || `HTTP ${response.status}`);
}

export async function getLotwStatus(signal?: AbortSignal): Promise<LotwStatus> {
  const response = await fetch('/api/log/lotw/status', { signal });
  if (!response.ok) throw await errorFromResponse(response);
  return await response.json();
}

export async function saveLotwCredentials(
  request: LotwCredentialsRequest,
  signal?: AbortSignal,
): Promise<LotwStatus> {
  const response = await fetch('/api/log/lotw/credentials', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal,
  });
  if (!response.ok) throw await errorFromResponse(response);
  return await response.json();
}

export async function saveLotwSettings(
  request: LotwSettingsRequest,
  signal?: AbortSignal,
): Promise<LotwStatus> {
  const response = await fetch('/api/log/lotw/settings', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal,
  });
  if (!response.ok) throw await errorFromResponse(response);
  return await response.json();
}

export async function syncLotwConfirmations(signal?: AbortSignal): Promise<LotwSyncResponse> {
  const response = await fetch('/api/log/lotw/sync', { method: 'POST', signal });
  if (!response.ok) throw await errorFromResponse(response);
  return await response.json();
}

export async function uploadLotwEntries(
  logEntryIds?: string[] | null,
  signal?: AbortSignal,
): Promise<LotwUploadResponse> {
  const response = await fetch('/api/log/lotw/upload', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ logEntryIds: logEntryIds ?? null }),
    signal,
  });
  if (!response.ok && response.status !== 409) throw await errorFromResponse(response);
  return await response.json();
}
