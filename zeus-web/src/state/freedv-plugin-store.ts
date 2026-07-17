// SPDX-License-Identifier: GPL-2.0-or-later
//
// FreeDV plugin gate. The FreeDV mode entry is available only when the
// org.openhpsdr.freedv backend plugin is installed and its status route is live.

import { create } from 'zustand';
import { FREEDV_PLUGIN_ID, probeFreeDvPlugin } from '../api/freedv-plugin';
import { usePluginsStore } from '../plugins/state/plugins-store';
import { useDisplayStore } from './display-store';

interface FreeDvPluginState {
  installed: boolean;
  live: boolean;
  probed: boolean;
  probe: () => Promise<void>;
  refresh: () => Promise<void>;
}

export const useFreeDvPluginStore = create<FreeDvPluginState>((set) => ({
  installed: false,
  live: false,
  probed: false,

  probe: async () => {
    const live = await probeFreeDvPlugin();
    set({ live, probed: true });
  },

  refresh: async () => {
    await usePluginsStore.getState().refreshInstalled();
    const live = await probeFreeDvPlugin();
    set({ live, probed: true });
  },
}));

export function isFreeDvPluginReady(): boolean {
  const s = useFreeDvPluginStore.getState();
  return s.installed && s.live;
}

export function freeDvPluginUnavailableReason(): string | null {
  const s = useFreeDvPluginStore.getState();
  if (s.installed && s.live) return null;
  if (!s.installed) return 'Install the FreeDV plugin from Settings / Plugins';
  return 'Restart Zeus to activate the FreeDV plugin';
}

if (typeof window !== 'undefined') {
  usePluginsStore.subscribe((s) => {
    const installed = s.installed.some((p) => p.id === FREEDV_PLUGIN_ID);
    if (installed !== useFreeDvPluginStore.getState().installed) {
      useFreeDvPluginStore.setState({ installed });
      void useFreeDvPluginStore.getState().probe();
    }
  });

  let wasConnected = useDisplayStore.getState().connected;
  useDisplayStore.subscribe((s) => {
    if (s.connected && !wasConnected) {
      void useFreeDvPluginStore.getState().refresh();
    }
    wasConnected = s.connected;
  });
}
