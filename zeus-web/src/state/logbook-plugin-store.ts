// SPDX-License-Identifier: GPL-2.0-or-later
//
// Logbook plugin gate. The built-in Logbook panel stays registered, but its
// data/actions are available only when org.openhpsdr.logbook is installed and
// its backend status route is live.

import { create } from 'zustand';
import { LOGBOOK_PLUGIN_ID, probeLogbookPlugin } from '../api/logbook-plugin';
import { usePluginsStore } from '../plugins/state/plugins-store';
import { useDisplayStore } from './display-store';

interface LogbookPluginState {
  installed: boolean;
  live: boolean;
  probed: boolean;
  probe: () => Promise<void>;
  refresh: () => Promise<void>;
}

export const useLogbookPluginStore = create<LogbookPluginState>((set) => ({
  installed: false,
  live: false,
  probed: false,

  probe: async () => {
    const installed = usePluginsStore.getState().installed.some((p) => p.id === LOGBOOK_PLUGIN_ID);
    if (!installed) {
      set({ installed: false, live: false, probed: true });
      return;
    }

    const live = await probeLogbookPlugin();
    set({ installed: true, live, probed: true });
  },

  refresh: async () => {
    await usePluginsStore.getState().refreshInstalled();
    await useLogbookPluginStore.getState().probe();
  },
}));

export function isLogbookPluginReady(): boolean {
  const s = useLogbookPluginStore.getState();
  return s.installed && s.live;
}

export function logbookPluginUnavailableReason(): string | null {
  return isLogbookPluginReady() ? null : 'Install the Logbook plugin from Settings → Plugins';
}

if (typeof window !== 'undefined') {
  usePluginsStore.subscribe((s) => {
    const installed = s.installed.some((p) => p.id === LOGBOOK_PLUGIN_ID);
    const current = useLogbookPluginStore.getState();
    if (installed !== current.installed) {
      useLogbookPluginStore.setState({ installed, live: installed ? current.live : false });
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
