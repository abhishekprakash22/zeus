// SPDX-License-Identifier: GPL-2.0-or-later
//
// Plugins store. Holds two server-shaped snapshots:
//
//   1. The installed plugin list (from GET /api/plugins).
//   2. The registry catalog (from GET /api/plugins/registry), which is
//      fetched lazily — operators may not open the browser tab.
//
// Plus per-side load flags (loaded / inflight / lastError) so the panels
// can render a sensible "loading" / "couldn't reach the registry" state.
// Mirrors the shape of capabilities-store.ts.

import { create } from 'zustand';

import {
  fetchInstalledPlugins,
  fetchRegistry,
  installPlugin,
  registryEntryToManagedPlugin,
  uninstallPlugin,
  type InstallRequest,
  type PluginDto,
  type PluginListResponse,
  type RegistryCatalog,
  type UninstallResult,
} from '../api/plugins';
import { reloadInstalledPluginUis } from '../runtime/pluginRuntime';
import { pluginAccessFor } from '../../state/user-access-store';

type LoadState = {
  loaded: boolean;
  inflight: boolean;
  loadError: string | null;
};

const INITIAL_LOAD: LoadState = {
  loaded: false,
  inflight: false,
  loadError: null,
};

export type PluginsStoreState = {
  // Installed Zeus plugin-repo plugins. Operator-scanned VST3 / AU wrappers
  // are split into installedVsts so the plugin store can show them separately.
  installed: PluginDto[];
  installedVsts: PluginDto[];
  blocked: PluginDto[];
  sdkAbi: number;
  sdkVersion: string;
  installedLoad: LoadState;

  // Registry catalog
  registry: RegistryCatalog | null;
  registrySourceUrl: string;
  registryLoad: LoadState;

  // Install workflow flags + last error/success message (used by the
  // InstallFromUrl form and InstallButton on each registry card).
  installInflight: boolean;
  lastInstallError: string | null;
  lastInstallOk: string | null;

  // Uninstall workflow flags
  uninstallInflight: boolean;
  lastUninstallError: string | null;
  lastUninstallNotice: string | null;

  refreshInstalled: () => Promise<void>;
  refreshRegistry: () => Promise<void>;
  install: (req: InstallRequest) => Promise<PluginDto | null>;
  uninstall: (id: string) => Promise<UninstallResult | null>;
  clearInstallFeedback: () => void;
  clearUninstallFeedback: () => void;
};

function errMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

/** How long the "Installed …" confirmation stays visible before the reload. */
const POST_INSTALL_RELOAD_DELAY_MS = 1200;

/**
 * Full app reload after a successful plugin install — the automatic
 * equivalent of the operator hard-refreshing (Ctrl/Cmd+Shift+R). A freshly
 * installed plugin's backend routes go live immediately, but frontend state
 * that was decided at boot (per-plugin gate probes, mode buttons, panel
 * wake-up) can be stale until the app re-runs its boot path; reloading is the
 * one mechanism that refreshes ALL of it, for every plugin, on every
 * platform (the desktop webview and the browser reload identically).
 * The delay lets the "Installed X — reloading" confirmation render first.
 *
 * `reload` is injectable for tests; jsdom's location.reload throws.
 */
export function schedulePostInstallReload(
  reload: () => void = () => window.location.reload(),
  delayMs: number = POST_INSTALL_RELOAD_DELAY_MS,
): void {
  if (import.meta.env.MODE === 'test') return;
  setTimeout(() => {
    try {
      reload();
    } catch {
      // Non-browser host (tests, SSR) — nothing to reload.
    }
  }, delayMs);
}

async function refreshRuntimePanels() {
  try {
    await reloadInstalledPluginUis();
  } catch (err) {
    // jsdom's native fetch cannot resolve app-relative URLs when a test does
    // not stub this second refresh path. The installed-list refresh above is
    // still covered there; production browsers resolve /api/plugins normally.
    if (
      err instanceof TypeError &&
      err.message.includes('Failed to parse URL')
    ) {
      return;
    }

    console.warn('plugin runtime refresh threw', err);
  }
}

export const usePluginsStore = create<PluginsStoreState>((set, get) => ({
  installed: [],
  installedVsts: [],
  blocked: [],
  sdkAbi: 0,
  sdkVersion: '',
  installedLoad: { ...INITIAL_LOAD },

  registry: null,
  registrySourceUrl: '',
  registryLoad: { ...INITIAL_LOAD },

  installInflight: false,
  lastInstallError: null,
  lastInstallOk: null,

  uninstallInflight: false,
  lastUninstallError: null,
  lastUninstallNotice: null,

  // Idempotent — multiple call sites (panel mount, post-install, post-uninstall)
  // can request a refresh; the in-flight guard prevents duplicate GETs.
  refreshInstalled: async () => {
    if (get().installedLoad.inflight) return;
    set({ installedLoad: { ...INITIAL_LOAD, inflight: true } });
    try {
      const resp: PluginListResponse = await fetchInstalledPlugins();
      const entitled = resp.plugins.filter((p) => pluginAccessFor(p.id, p.scanned).allowed);
      const blocked = resp.plugins.filter((p) => !p.scanned && !pluginAccessFor(p.id, p.scanned).allowed);
      set({
        // Operator-scanned VST3 / AU plugins (resp.plugins[].scanned) live in
        // their own Settings ▸ Plugins ▸ VSTs tab — they are not Zeus
        // plugin-repo plugins, so the main Installed list excludes them. The
        // Audio Suite has its own consumer (pluginRuntime.fetchInstalledPlugins)
        // that still sees the full server list.
        installed: entitled.filter((p) => !p.scanned),
        installedVsts: entitled.filter((p) => p.scanned),
        blocked,
        sdkAbi: resp.sdkAbi,
        sdkVersion: resp.sdkVersion,
        installedLoad: { loaded: true, inflight: false, loadError: null },
      });
    } catch (err) {
      set({
        installedLoad: {
          loaded: get().installedLoad.loaded,
          inflight: false,
          loadError: errMessage(err),
        },
      });
    }
  },

  refreshRegistry: async () => {
    if (get().registryLoad.inflight) return;
    set({ registryLoad: { ...INITIAL_LOAD, inflight: true } });
    try {
      const resp = await fetchRegistry();
      set({
        registry: resp.catalog,
        registrySourceUrl: resp.sourceUrl,
        registryLoad: { loaded: true, inflight: false, loadError: null },
      });
    } catch (err) {
      set({
        registryLoad: {
          loaded: get().registryLoad.loaded,
          inflight: false,
          loadError: errMessage(err),
        },
      });
    }
  },

  install: async (req) => {
    if (get().installInflight) return null;
    set({
      installInflight: true,
      lastInstallError: null,
      lastInstallOk: null,
    });
    try {
      if (req.source === 'registry' && req.id) {
        const entry = get().registry?.plugins.find((plugin) => plugin.id === req.id) ?? null;
        const access = pluginAccessFor(req.id, false, entry ? registryEntryToManagedPlugin(entry) : null);
        if (!access.allowed) {
          throw new Error(access.reason ?? 'Plugin subscription required');
        }
      }
      const dto = await installPlugin(req);
      set({
        installInflight: false,
        lastInstallError: null,
        lastInstallOk: `Installed ${dto.name} ${dto.version} — reloading…`,
      });
      // Refresh the installed list so the new plugin appears immediately.
      await get().refreshInstalled();
      await refreshRuntimePanels();
      // Then reload the whole app so every boot-time gate (plugin probes,
      // mode buttons, dormant panels) picks the new plugin up — the automatic
      // hard-refresh operators otherwise had to do by hand.
      schedulePostInstallReload();
      return dto;
    } catch (err) {
      set({
        installInflight: false,
        lastInstallError: errMessage(err),
        lastInstallOk: null,
      });
      return null;
    }
  },

  uninstall: async (id) => {
    if (get().uninstallInflight) return null;
    set({
      uninstallInflight: true,
      lastUninstallError: null,
      lastUninstallNotice: null,
    });
    try {
      const result = await uninstallPlugin(id);
      set({
        uninstallInflight: false,
        lastUninstallError: null,
        lastUninstallNotice:
          result.status === 202
            ? result.message ??
              'Plugin removal deferred — restart Zeus to complete.'
            : null,
      });
      await get().refreshInstalled();
      await refreshRuntimePanels();
      return result;
    } catch (err) {
      set({
        uninstallInflight: false,
        lastUninstallError: errMessage(err),
        lastUninstallNotice: null,
      });
      return null;
    }
  },

  clearInstallFeedback: () =>
    set({ lastInstallError: null, lastInstallOk: null }),

  clearUninstallFeedback: () =>
    set({ lastUninstallError: null, lastUninstallNotice: null }),
}));
