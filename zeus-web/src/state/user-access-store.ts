// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { create } from 'zustand';
import { ApiError } from '../api/client';
import {
  createAdminUser,
  fetchAdminUsers,
  fetchUserSession,
  updateAdminUser,
  updateManagedPlugin as updateManagedPluginApi,
  type ZeusManagedPluginRecord,
  type ZeusManagedPluginUpdateRequest,
  type ZeusPluginEntitlement,
  type ZeusUserRecord,
  type ZeusUserSession,
  type ZeusUserUpdateRequest,
  type ZeusUserUpsertRequest,
} from '../api/users';
import { useQrzStore } from './qrz-store';

type UserAccessState = {
  checked: boolean;
  loading: boolean;
  adminLoading: boolean;
  saving: boolean;
  error: string | null;
  adminError: string | null;
  session: ZeusUserSession | null;
  users: ZeusUserRecord[];
  managedPlugins: ZeusManagedPluginRecord[];

  refreshSession: () => Promise<ZeusUserSession | null>;
  loginWithQrz: (username: string, password: string) => Promise<boolean>;
  logout: () => Promise<void>;
  loadAdminUsers: () => Promise<void>;
  createUser: (request: ZeusUserUpsertRequest) => Promise<ZeusUserRecord | null>;
  updateUser: (callsign: string, request: ZeusUserUpdateRequest) => Promise<ZeusUserRecord | null>;
  updateManagedPlugin: (
    pluginId: string,
    request: ZeusManagedPluginUpdateRequest,
  ) => Promise<ZeusManagedPluginRecord | null>;
};

function errorMessage(err: unknown): string {
  return err instanceof ApiError ? err.message : String(err);
}

function replaceUser(users: ZeusUserRecord[], next: ZeusUserRecord): ZeusUserRecord[] {
  const found = users.some((u) => u.callsign === next.callsign);
  const merged = found ? users.map((u) => (u.callsign === next.callsign ? next : u)) : [next, ...users];
  return [...merged].sort((a, b) => {
    if (a.isAdmin !== b.isAdmin) return a.isAdmin ? -1 : 1;
    return a.callsign.localeCompare(b.callsign);
  });
}

export type PluginAccessDecision = {
  allowed: boolean;
  reason: string | null;
  entitlement: ZeusPluginEntitlement | null;
  managedPlugin: ZeusManagedPluginRecord | null;
};

function expiredAt(value: string | null | undefined): boolean {
  if (!value) return false;
  const time = Date.parse(value);
  return Number.isFinite(time) && time <= Date.now();
}

function findPluginEntitlement(
  session: ZeusUserSession | null,
  pluginId: string,
): ZeusPluginEntitlement | null {
  const wanted = pluginId.trim().toLowerCase();
  if (!wanted) return null;
  return session?.pluginEntitlements.find((e) => e.pluginId.toLowerCase() === wanted) ?? null;
}

function findManagedPlugin(
  session: ZeusUserSession | null,
  pluginId: string,
): ZeusManagedPluginRecord | null {
  const wanted = pluginId.trim().toLowerCase();
  if (!wanted) return null;
  return session?.managedPlugins.find((p) => p.pluginId.toLowerCase() === wanted) ?? null;
}

export function pluginAccessFor(
  pluginId: string,
  scanned = false,
  registryPlugin: ZeusManagedPluginRecord | null = null,
): PluginAccessDecision {
  if (scanned) {
    return { allowed: true, reason: null, entitlement: null, managedPlugin: null };
  }

  const session = useUserAccessStore.getState().session;
  if (!session) {
    return { allowed: true, reason: null, entitlement: null, managedPlugin: registryPlugin };
  }

  const managedPlugin = findManagedPlugin(session, pluginId) ?? registryPlugin;
  if (!session.accessAllowed) {
    return {
      allowed: false,
      reason: session?.denialReason ?? 'QRZ app access required',
      entitlement: null,
      managedPlugin,
    };
  }

  const entitlement = findPluginEntitlement(session, pluginId);
  if (entitlement) {
    if (!entitlement.accessAllowed) {
      return {
        allowed: false,
        reason: entitlement.denialReason ?? 'Plugin subscription required',
        entitlement,
        managedPlugin,
      };
    }
    if (expiredAt(entitlement.subscriptionExpiresUtc)) {
      return {
        allowed: false,
        reason: 'Plugin subscription expired',
        entitlement,
        managedPlugin,
      };
    }
    return { allowed: true, reason: null, entitlement, managedPlugin };
  }

  if (managedPlugin?.active === false) {
    return {
      allowed: false,
      reason: 'Plugin disabled by Zeus admin',
      entitlement: null,
      managedPlugin,
    };
  }

  if (managedPlugin?.subscriptionRequired || session.pluginAccessMode === 'selected') {
    return {
      allowed: false,
      reason: 'Plugin subscription required',
      entitlement: null,
      managedPlugin,
    };
  }

  return { allowed: true, reason: null, entitlement: null, managedPlugin };
}

export const useUserAccessStore = create<UserAccessState>((set, get) => ({
  checked: false,
  loading: false,
  adminLoading: false,
  saving: false,
  error: null,
  adminError: null,
  session: null,
  users: [],
  managedPlugins: [],

  refreshSession: async () => {
    set({ loading: true, error: null });
    try {
      const session = await fetchUserSession();
      set({ checked: true, loading: false, session });
      return session;
    } catch (err) {
      set({ checked: true, loading: false, error: errorMessage(err) });
      return null;
    }
  },

  loginWithQrz: async (username, password) => {
    const qrzOk = await useQrzStore.getState().login(username, password);
    const session = await get().refreshSession();
    return Boolean(qrzOk && session?.accessAllowed);
  },

  logout: async () => {
    await useQrzStore.getState().logout();
    set({
      checked: true,
      session: null,
      users: [],
      managedPlugins: [],
      error: null,
      adminError: null,
    });
    await get().refreshSession();
  },

  loadAdminUsers: async () => {
    set({ adminLoading: true, adminError: null });
    try {
      const response = await fetchAdminUsers();
      set({
        adminLoading: false,
        session: response.session,
        users: response.users,
        managedPlugins: response.managedPlugins,
      });
    } catch (err) {
      set({ adminLoading: false, adminError: errorMessage(err) });
    }
  },

  createUser: async (request) => {
    set({ saving: true, adminError: null });
    try {
      const user = await createAdminUser(request);
      set((state) => ({ saving: false, users: replaceUser(state.users, user) }));
      return user;
    } catch (err) {
      set({ saving: false, adminError: errorMessage(err) });
      return null;
    }
  },

  updateUser: async (callsign, request) => {
    set({ saving: true, adminError: null });
    try {
      const user = await updateAdminUser(callsign, request);
      set((state) => ({ saving: false, users: replaceUser(state.users, user) }));
      await get().refreshSession();
      return user;
    } catch (err) {
      set({ saving: false, adminError: errorMessage(err) });
      return null;
    }
  },

  updateManagedPlugin: async (pluginId, request) => {
    set({ saving: true, adminError: null });
    try {
      const managedPlugin = await updateManagedPluginApi(pluginId, request);
      set((state) => {
        const found = state.managedPlugins.some((p) => p.pluginId === managedPlugin.pluginId);
        const managedPlugins = found
          ? state.managedPlugins.map((p) => (p.pluginId === managedPlugin.pluginId ? managedPlugin : p))
          : [...state.managedPlugins, managedPlugin];
        const sorted = [...managedPlugins].sort((a, b) => a.pluginId.localeCompare(b.pluginId));
        const session = state.session
          ? {
              ...state.session,
              managedPlugins: sorted,
            }
          : state.session;
        return { saving: false, managedPlugins: sorted, session };
      });
      return managedPlugin;
    } catch (err) {
      set({ saving: false, adminError: errorMessage(err) });
      return null;
    }
  },
}));
