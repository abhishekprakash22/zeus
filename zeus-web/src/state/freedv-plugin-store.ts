// SPDX-License-Identifier: GPL-2.0-or-later
//
// FreeDV mode gate. The FreeDV backend now ships IN CORE (same in-core story
// as the FT8 suite — see Zeus.Server.Hosting/FreeDv/), serving the plugin
// route prefix /api/plugins/org.openhpsdr.freedv. The gate therefore mirrors
// digital-plugin.ts: LIVE (GET /status answers 2xx) is what unlocks the
// FREEDV mode entry. `installed` is kept as informational state — a real
// org.openhpsdr.freedv plugin installed alongside still registers here and
// its activation re-triggers the probe — but it no longer gates readiness,
// because the in-core backend is never in the installed-plugins list.

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
  return useFreeDvPluginStore.getState().live;
}

export function freeDvPluginUnavailableReason(): string | null {
  const s = useFreeDvPluginStore.getState();
  if (s.live) return null;
  return 'FreeDV backend is not reachable — reconnect to Zeus';
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
