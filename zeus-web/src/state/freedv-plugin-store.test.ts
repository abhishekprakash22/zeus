// SPDX-License-Identifier: GPL-2.0-or-later
//
// freedv-plugin-store tests — FreeDV mode gate. installed = plugin id appears
// in the installed list; live = GET /status answers 2xx.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  freeDvPluginUnavailableReason,
  isFreeDvPluginReady,
  useFreeDvPluginStore,
} from './freedv-plugin-store';
import { usePluginsStore } from '../plugins/state/plugins-store';
import { useDisplayStore } from './display-store';
import { FREEDV_PLUGIN_BASE, FREEDV_PLUGIN_ID } from '../api/freedv-plugin';
import { parsePluginDto } from '../plugins/api/plugins';

const flush = () => new Promise((r) => setTimeout(r, 0));

function stubFetch(statusCode: number, pluginIds: string[] = []) {
  const fn = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url === `${FREEDV_PLUGIN_BASE}/status`) {
      return {
        ok: statusCode >= 200 && statusCode < 300,
        status: statusCode,
        headers: new Headers(),
        json: async () => ({}),
      };
    }
    if (url === '/api/plugins') {
      return {
        ok: true,
        status: 200,
        headers: new Headers(),
        json: async () => ({
          sdkAbi: 1,
          sdkVersion: '1.2.0',
          plugins: pluginIds.map((id) => ({ id, scanned: false, name: id, version: '1.0.0' })),
        }),
      };
    }
    return { ok: true, status: 200, headers: new Headers(), json: async () => ({}) };
  });
  vi.stubGlobal('fetch', fn as never);
  return fn;
}

describe('freedv-plugin-store', () => {
  beforeEach(() => {
    stubFetch(200);
    useFreeDvPluginStore.setState({ installed: false, live: false, probed: false });
    usePluginsStore.setState({ installed: [] });
    useDisplayStore.setState({ connected: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('probe marks live on a 2xx /status', async () => {
    await useFreeDvPluginStore.getState().probe();
    expect(useFreeDvPluginStore.getState().live).toBe(true);
    expect(useFreeDvPluginStore.getState().probed).toBe(true);
  });

  it('probe marks NOT live on 404 and 503', async () => {
    stubFetch(404);
    await useFreeDvPluginStore.getState().probe();
    expect(useFreeDvPluginStore.getState().live).toBe(false);

    stubFetch(503);
    await useFreeDvPluginStore.getState().probe();
    expect(useFreeDvPluginStore.getState().live).toBe(false);
  });

  it('probe marks NOT live on a network error', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new Error('offline'))) as never);
    await useFreeDvPluginStore.getState().probe();
    expect(useFreeDvPluginStore.getState().live).toBe(false);
    expect(useFreeDvPluginStore.getState().probed).toBe(true);
  });

  it('installed follows the plugins-store list and re-probes on change', async () => {
    const fn = stubFetch(200);
    usePluginsStore.setState({
      installed: [parsePluginDto({ id: FREEDV_PLUGIN_ID, name: 'Zeus FreeDV' })],
    });
    await flush();
    expect(useFreeDvPluginStore.getState().installed).toBe(true);
    expect(fn.mock.calls.some((c) => String(c[0]) === `${FREEDV_PLUGIN_BASE}/status`)).toBe(true);

    usePluginsStore.setState({ installed: [] });
    await flush();
    expect(useFreeDvPluginStore.getState().installed).toBe(false);
  });

  it('another plugin id does not open the gate', async () => {
    usePluginsStore.setState({
      installed: [parsePluginDto({ id: 'com.kb2uka.digital', name: 'Zeus Digital' })],
    });
    await flush();
    expect(useFreeDvPluginStore.getState().installed).toBe(false);
  });

  it('readiness and reasons require both installed and live', () => {
    useFreeDvPluginStore.setState({ installed: false, live: false });
    expect(isFreeDvPluginReady()).toBe(false);
    expect(freeDvPluginUnavailableReason()).toBe('Install the FreeDV plugin from Settings / Plugins');

    useFreeDvPluginStore.setState({ installed: true, live: false });
    expect(isFreeDvPluginReady()).toBe(false);
    expect(freeDvPluginUnavailableReason()).toBe('Restart Zeus to activate the FreeDV plugin');

    useFreeDvPluginStore.setState({ installed: true, live: true });
    expect(isFreeDvPluginReady()).toBe(true);
    expect(freeDvPluginUnavailableReason()).toBeNull();
  });

  it('re-probes on an app-WS reconnect', async () => {
    const fn = stubFetch(200, [FREEDV_PLUGIN_ID]);
    useDisplayStore.setState({ connected: true });
    await flush();
    const urls = fn.mock.calls.map((c) => String(c[0]));
    expect(urls).toContain('/api/plugins');
    expect(urls).toContain(`${FREEDV_PLUGIN_BASE}/status`);
    expect(useFreeDvPluginStore.getState().live).toBe(true);
  });
});
