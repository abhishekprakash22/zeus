// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { ApiError } from './client';

export type ZeusUserRecord = {
  callsign: string;
  displayName: string;
  accessAllowed: boolean;
  isAdmin: boolean;
  subscriptionStatus: string;
  subscriptionExpiresUtc: string | null;
  pluginAccessMode: 'all' | 'selected';
  pluginEntitlements: ZeusPluginEntitlement[];
  hasQrzXmlSubscription: boolean;
  grid: string | null;
  notes: string | null;
  createdUtc: string;
  updatedUtc: string;
  lastLoginUtc: string | null;
};

export type ZeusUserSession = {
  qrzConnected: boolean;
  callsign: string | null;
  displayName: string | null;
  accessAllowed: boolean;
  isAdmin: boolean;
  hasQrzXmlSubscription: boolean;
  subscriptionStatus: string;
  subscriptionExpiresUtc: string | null;
  pluginAccessMode: 'all' | 'selected';
  pluginEntitlements: ZeusPluginEntitlement[];
  managedPlugins: ZeusManagedPluginRecord[];
  denialReason: string | null;
  user: ZeusUserRecord | null;
};

export type ZeusPluginEntitlement = {
  pluginId: string;
  accessAllowed: boolean;
  subscriptionStatus: string;
  subscriptionExpiresUtc: string | null;
  denialReason: string | null;
};

export type ZeusManagedPluginRecord = {
  pluginId: string;
  displayName: string;
  subscriptionRequired: boolean;
  monthlyPriceCents: number;
  currency: string;
  active: boolean;
  checkoutUrl: string | null;
  notes: string | null;
  createdUtc: string;
  updatedUtc: string;
};

export type ZeusUsersAdminResponse = {
  session: ZeusUserSession;
  users: ZeusUserRecord[];
  managedPlugins: ZeusManagedPluginRecord[];
};

export type ZeusUserUpdateRequest = {
  accessAllowed?: boolean;
  isAdmin?: boolean;
  subscriptionStatus?: string;
  subscriptionExpiresUtc?: string | null;
  pluginAccessMode?: 'all' | 'selected';
  pluginEntitlements?: ZeusPluginEntitlement[];
  notes?: string | null;
};

export type ZeusUserUpsertRequest = ZeusUserUpdateRequest & {
  callsign: string;
};

export type ZeusManagedPluginUpdateRequest = {
  displayName?: string | null;
  subscriptionRequired?: boolean;
  monthlyPriceCents?: number;
  currency?: string | null;
  active?: boolean;
  checkoutUrl?: string | null;
  notes?: string | null;
};

function toStr(v: unknown): string | null {
  return typeof v === 'string' && v.length > 0 ? v : null;
}

function normalizePluginAccessMode(raw: unknown): 'all' | 'selected' {
  return raw === 'selected' ? 'selected' : 'all';
}

function normalizePluginEntitlement(raw: unknown): ZeusPluginEntitlement {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    pluginId: typeof r.pluginId === 'string' ? r.pluginId : '',
    accessAllowed: r.accessAllowed === true,
    subscriptionStatus: typeof r.subscriptionStatus === 'string' ? r.subscriptionStatus : 'manual',
    subscriptionExpiresUtc: toStr(r.subscriptionExpiresUtc),
    denialReason: toStr(r.denialReason),
  };
}

function normalizePluginEntitlements(raw: unknown): ZeusPluginEntitlement[] {
  if (!Array.isArray(raw)) return [];
  return raw
    .map(normalizePluginEntitlement)
    .filter((e) => e.pluginId.length > 0);
}

function normalizeManagedPlugin(raw: unknown): ZeusManagedPluginRecord {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    pluginId: typeof r.pluginId === 'string' ? r.pluginId : '',
    displayName: typeof r.displayName === 'string' ? r.displayName : '',
    subscriptionRequired: r.subscriptionRequired === true,
    monthlyPriceCents: typeof r.monthlyPriceCents === 'number' && Number.isFinite(r.monthlyPriceCents)
      ? r.monthlyPriceCents
      : 0,
    currency: typeof r.currency === 'string' ? r.currency : 'USD',
    active: r.active !== false,
    checkoutUrl: toStr(r.checkoutUrl),
    notes: toStr(r.notes),
    createdUtc: typeof r.createdUtc === 'string' ? r.createdUtc : '',
    updatedUtc: typeof r.updatedUtc === 'string' ? r.updatedUtc : '',
  };
}

function normalizeManagedPlugins(raw: unknown): ZeusManagedPluginRecord[] {
  if (!Array.isArray(raw)) return [];
  return raw
    .map(normalizeManagedPlugin)
    .filter((p) => p.pluginId.length > 0);
}

function normalizeUser(raw: unknown): ZeusUserRecord {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    callsign: typeof r.callsign === 'string' ? r.callsign : '',
    displayName: typeof r.displayName === 'string' ? r.displayName : '',
    accessAllowed: r.accessAllowed === true,
    isAdmin: r.isAdmin === true,
    subscriptionStatus: typeof r.subscriptionStatus === 'string' ? r.subscriptionStatus : 'manual',
    subscriptionExpiresUtc: toStr(r.subscriptionExpiresUtc),
    pluginAccessMode: normalizePluginAccessMode(r.pluginAccessMode),
    pluginEntitlements: normalizePluginEntitlements(r.pluginEntitlements),
    hasQrzXmlSubscription: r.hasQrzXmlSubscription === true,
    grid: toStr(r.grid),
    notes: toStr(r.notes),
    createdUtc: typeof r.createdUtc === 'string' ? r.createdUtc : '',
    updatedUtc: typeof r.updatedUtc === 'string' ? r.updatedUtc : '',
    lastLoginUtc: toStr(r.lastLoginUtc),
  };
}

function normalizeSession(raw: unknown): ZeusUserSession {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    qrzConnected: r.qrzConnected === true,
    callsign: toStr(r.callsign),
    displayName: toStr(r.displayName),
    accessAllowed: r.accessAllowed === true,
    isAdmin: r.isAdmin === true,
    hasQrzXmlSubscription: r.hasQrzXmlSubscription === true,
    subscriptionStatus: typeof r.subscriptionStatus === 'string' ? r.subscriptionStatus : 'none',
    subscriptionExpiresUtc: toStr(r.subscriptionExpiresUtc),
    pluginAccessMode: normalizePluginAccessMode(r.pluginAccessMode),
    pluginEntitlements: normalizePluginEntitlements(r.pluginEntitlements),
    managedPlugins: normalizeManagedPlugins(r.managedPlugins),
    denialReason: toStr(r.denialReason),
    user: r.user ? normalizeUser(r.user) : null,
  };
}

function normalizeAdminResponse(raw: unknown): ZeusUsersAdminResponse {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    session: normalizeSession(r.session),
    users: Array.isArray(r.users) ? r.users.map(normalizeUser) : [],
    managedPlugins: normalizeManagedPlugins(r.managedPlugins),
  };
}

async function jsonFetch<T>(input: RequestInfo, init: RequestInit | undefined, parse: (raw: unknown) => T): Promise<T> {
  const res = await fetch(input, init);
  const rawText = await res.text();
  let raw: unknown = null;
  if (rawText.length > 0) {
    try {
      raw = JSON.parse(rawText) as unknown;
    } catch {
      raw = rawText;
    }
  }

  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    if (raw && typeof raw === 'object' && 'error' in raw && typeof (raw as { error: unknown }).error === 'string') {
      message = (raw as { error: string }).error;
    }
    throw new ApiError(res.status, message);
  }

  return parse(raw);
}

export function fetchUserSession(signal?: AbortSignal): Promise<ZeusUserSession> {
  return jsonFetch('/api/users/session', { signal }, normalizeSession);
}

export function fetchAdminUsers(signal?: AbortSignal): Promise<ZeusUsersAdminResponse> {
  return jsonFetch('/api/admin/users', { signal }, normalizeAdminResponse);
}

export function createAdminUser(req: ZeusUserUpsertRequest, signal?: AbortSignal): Promise<ZeusUserRecord> {
  return jsonFetch(
    '/api/admin/users',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(req),
      signal,
    },
    normalizeUser,
  );
}

export function updateAdminUser(
  callsign: string,
  req: ZeusUserUpdateRequest,
  signal?: AbortSignal,
): Promise<ZeusUserRecord> {
  return jsonFetch(
    `/api/admin/users/${encodeURIComponent(callsign)}`,
    {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(req),
      signal,
    },
    normalizeUser,
  );
}

export function updateManagedPlugin(
  pluginId: string,
  req: ZeusManagedPluginUpdateRequest,
  signal?: AbortSignal,
): Promise<ZeusManagedPluginRecord> {
  return jsonFetch(
    `/api/admin/plugins/${encodeURIComponent(pluginId)}`,
    {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(req),
      signal,
    },
    normalizeManagedPlugin,
  );
}
