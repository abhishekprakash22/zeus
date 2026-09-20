// SPDX-License-Identifier: GPL-2.0-or-later
//
// Logbook availability gate. The Logbook panel and the "log this QSO" action
// are enabled whenever the BACKEND has a logbook behind /api/log/* — which is
// the built-in store on a stock install, or org.openhpsdr.logbook when that
// plugin is installed. The plugin probe is kept as a secondary signal so a
// plugin that is installed but not yet live still reads as unavailable.

import { create } from 'zustand';
import { LOGBOOK_PLUGIN_ID, probeLogbookPlugin } from '../api/logbook-plugin';
import { getLogCapabilities } from '../api/log';
import { usePluginsStore } from '../plugins/state/plugins-store';
import { useDisplayStore } from './display-store';

interface LogbookPluginState {
  installed: boolean;
  live: boolean;
  probed: boolean;
  probe: () => Promise<void>;
  refresh: () => Promise<void>;
}

async function backendHasLogbook(): Promise<boolean> {
  try {
    return (await getLogCapabilities()).pluginInstalled;
  } catch {
    return false;
  }
}

export const useLogbookPluginStore = create<LogbookPluginState>((set) => ({
  installed: false,
  live: false,
  probed: false,

  probe: async () => {
    const pluginInstalled = usePluginsStore.getState().installed.some((p) => p.id === LOGBOOK_PLUGIN_ID);
    if (pluginInstalled) {
      const live = await probeLogbookPlugin();
      set({ installed: true, live, probed: true });
      return;
    }

    // No plugin — ask the backend, which answers for the built-in store.
    const ready = await backendHasLogbook();
    set({ installed: ready, live: ready, probed: true });
  },

  refresh: async () => {
    await usePluginsStore.getState().refreshInstalled();
    await useLogbookPluginStore.getState().probe();
  },
}));

// Shown when the gate is shut. With the built-in store that means the
// backend itself has no logbook — not that anything needs installing.
export const LOGBOOK_UNAVAILABLE_REASON = 'Logbook unavailable — the backend has no logbook store';

export function isLogbookPluginReady(): boolean {
  const s = useLogbookPluginStore.getState();
  return s.installed && s.live;
}

export function logbookPluginUnavailableReason(): string | null {
  return isLogbookPluginReady() ? null : LOGBOOK_UNAVAILABLE_REASON;
}

if (typeof window !== 'undefined') {
  // Track the PLUGIN's presence separately from readiness: with the built-in
  // store, readiness is already true before the plugin arrives, so comparing
  // against it would miss the hand-over.
  let hadPlugin = usePluginsStore.getState().installed.some((p) => p.id === LOGBOOK_PLUGIN_ID);
  usePluginsStore.subscribe((s) => {
    const hasPlugin = s.installed.some((p) => p.id === LOGBOOK_PLUGIN_ID);
    if (hasPlugin !== hadPlugin) {
      hadPlugin = hasPlugin;
      void useLogbookPluginStore.getState().probe();
    }
  });

  let wasConnected = useDisplayStore.getState().connected;
  useDisplayStore.subscribe((s) => {
    if (s.connected && !wasConnected) {
      void useLogbookPluginStore.getState().refresh();
    }
    wasConnected = s.connected;
  });
}
