// SPDX-License-Identifier: GPL-2.0-or-later
//
// logbook-plugin-store tests — Logbook panel gate. installed = plugin id
// appears in the installed list; live = GET /status answers 2xx.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  isLogbookPluginReady,
  logbookPluginUnavailableReason,
  useLogbookPluginStore,
} from './logbook-plugin-store';
import { usePluginsStore } from '../plugins/state/plugins-store';
import { useDisplayStore } from './display-store';
import { LOGBOOK_PLUGIN_BASE, LOGBOOK_PLUGIN_ID } from '../api/logbook-plugin';
import { parsePluginDto } from '../plugins/api/plugins';

const flush = () => new Promise((r) => setTimeout(r, 0));

function stubFetch(statusCode: number, pluginIds: string[] = []) {
  const fn = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url === `${LOGBOOK_PLUGIN_BASE}/status`) {
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
          sdkVersion: '1.3.0',
          plugins: pluginIds.map((id) => ({ id, scanned: false, name: id, version: '1.0.0' })),
        }),
      };
    }
    return { ok: true, status: 200, headers: new Headers(), json: async () => ({}) };
  });
  vi.stubGlobal('fetch', fn as never);
  return fn;
}

function installLogbookPlugin() {
  usePluginsStore.setState({
    installed: [parsePluginDto({ id: LOGBOOK_PLUGIN_ID, name: 'Zeus Logbook' })],
  });
}

describe('logbook-plugin-store', () => {
  beforeEach(() => {
    stubFetch(200);
    useLogbookPluginStore.setState({ installed: false, live: false, probed: false });
    usePluginsStore.setState({ installed: [] });
    useDisplayStore.setState({ connected: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('probe marks live on a 2xx /status', async () => {
    installLogbookPlugin();
    await useLogbookPluginStore.getState().probe();
    expect(useLogbookPluginStore.getState().live).toBe(true);
    expect(useLogbookPluginStore.getState().probed).toBe(true);
  });

  it('probe marks NOT live on 404 and 503', async () => {
    installLogbookPlugin();

    stubFetch(404);
    await useLogbookPluginStore.getState().probe();
    expect(useLogbookPluginStore.getState().live).toBe(false);

    stubFetch(503);
    await useLogbookPluginStore.getState().probe();
    expect(useLogbookPluginStore.getState().live).toBe(false);
  });

  it('probe marks NOT live on a network error', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new Error('offline'))) as never);
    installLogbookPlugin();
    await useLogbookPluginStore.getState().probe();
    expect(useLogbookPluginStore.getState().live).toBe(false);
    expect(useLogbookPluginStore.getState().probed).toBe(true);
  });

  it('does not fetch /status when the plugin is not installed', async () => {
    const fn = stubFetch(404);
    await useLogbookPluginStore.getState().probe();
    expect(useLogbookPluginStore.getState().installed).toBe(false);
    expect(useLogbookPluginStore.getState().live).toBe(false);
    expect(useLogbookPluginStore.getState().probed).toBe(true);
    expect(fn.mock.calls.some((c) => String(c[0]) === `${LOGBOOK_PLUGIN_BASE}/status`)).toBe(false);
  });

  it('installed follows the plugins-store list and re-probes on change', async () => {
    const fn = stubFetch(200);
    usePluginsStore.setState({
      installed: [parsePluginDto({ id: LOGBOOK_PLUGIN_ID, name: 'Zeus Logbook' })],
    });
    await flush();
    expect(useLogbookPluginStore.getState().installed).toBe(true);
    expect(fn.mock.calls.some((c) => String(c[0]) === `${LOGBOOK_PLUGIN_BASE}/status`)).toBe(true);

    usePluginsStore.setState({ installed: [] });
    await flush();
    expect(useLogbookPluginStore.getState().installed).toBe(false);
  });

  it('another plugin id does not open the gate', async () => {
    usePluginsStore.setState({
      installed: [parsePluginDto({ id: 'org.openhpsdr.freedv', name: 'Zeus FreeDV' })],
    });
    await flush();
    expect(useLogbookPluginStore.getState().installed).toBe(false);
  });

  it('readiness and reason require both installed and live', () => {
    useLogbookPluginStore.setState({ installed: false, live: false });
    expect(isLogbookPluginReady()).toBe(false);
    expect(logbookPluginUnavailableReason()).toBe('Install the Logbook plugin from Settings → Plugins');

    useLogbookPluginStore.setState({ installed: true, live: false });
    expect(isLogbookPluginReady()).toBe(false);
    expect(logbookPluginUnavailableReason()).toBe('Install the Logbook plugin from Settings → Plugins');

    useLogbookPluginStore.setState({ installed: true, live: true });
    expect(isLogbookPluginReady()).toBe(true);
    expect(logbookPluginUnavailableReason()).toBeNull();
  });

  it('re-probes on an app-WS reconnect', async () => {
    const fn = stubFetch(200, [LOGBOOK_PLUGIN_ID]);
    useDisplayStore.setState({ connected: true });
    await flush();
    const urls = fn.mock.calls.map((c) => String(c[0]));
    expect(urls).toContain('/api/plugins');
    expect(urls).toContain(`${LOGBOOK_PLUGIN_BASE}/status`);
    expect(useLogbookPluginStore.getState().live).toBe(true);
  });
});
