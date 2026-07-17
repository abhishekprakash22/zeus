// SPDX-License-Identifier: GPL-2.0-or-later
//
// Plugin subscription sync — a plugin that was installed while free becomes a
// paid admin-managed plugin while the app is open. The Zeus app must stream the
// updated user/plugin policy into Settings -> Plugins, block live access, offer
// checkout, and allow the operator to remove it instead.

import { expect, test, type Page, type Route } from '@playwright/test';

const pluginId = 'com.openhpsdr.zeus.e2e.paidgate';
const pluginName = 'Paid Gate Demo';

async function fulfillJson(route: Route, body: unknown, status = 200) {
  await route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

function pluginDto() {
  return {
    id: pluginId,
    name: pluginName,
    version: '1.0.0',
    author: 'Zeus E2E',
    description: 'A plugin used to verify subscription gating.',
    homepage: null,
    license: 'GPL-2.0-or-later',
    capabilities: [],
    ui: null,
    audio: null,
  };
}

function userSession(subscriptionRequired: boolean) {
  return {
    qrzConnected: true,
    callsign: 'N9WAR',
    displayName: 'N9WAR',
    accessAllowed: true,
    isAdmin: false,
    hasQrzXmlSubscription: true,
    subscriptionStatus: 'qrz-xml',
    subscriptionExpiresUtc: null,
    pluginAccessMode: 'all',
    pluginEntitlements: [],
    managedPlugins: [
      {
        pluginId,
        displayName: pluginName,
        subscriptionRequired,
        monthlyPriceCents: subscriptionRequired ? 400 : 0,
        currency: 'USD',
        active: true,
        checkoutUrl: null,
        notes: null,
        createdUtc: '2026-07-04T00:00:00Z',
        updatedUtc: '2026-07-04T00:00:00Z',
      },
    ],
    denialReason: null,
    user: null,
  };
}

function registryCatalog() {
  return {
    schemaVersion: 1,
    generated: '2026-07-04T00:00:00Z',
    plugins: [
      {
        id: pluginId,
        name: pluginName,
        description: 'Registry still says this plugin is free.',
        author: 'Zeus E2E',
        license: 'GPL-2.0-or-later',
        homepage: null,
        categories: ['e2e'],
        verified: true,
        subscription: null,
        versions: [
          {
            version: '1.0.0',
            sdkAbi: 1,
            sdkMinVersion: '0.6.0',
            platforms: ['any'],
            downloadUrl: 'https://example.invalid/paid-gate-demo.zip',
            sha256: 'a'.repeat(64),
          },
        ],
      },
    ],
  };
}

type PluginSubscriptionWorld = {
  adminHasPricedPlugin: boolean;
  installed: boolean;
  checkoutPosts: Array<Record<string, unknown>>;
  installPosts: number;
};

async function stubZeusApi(page: Page): Promise<PluginSubscriptionWorld> {
  const world: PluginSubscriptionWorld = {
    adminHasPricedPlugin: false,
    installed: true,
    checkoutPosts: [],
    installPosts: 0,
  };

  await page.addInitScript(() => {
    const nativeSetInterval = window.setInterval.bind(window);
    window.setInterval = ((
      handler: TimerHandler,
      timeout?: number,
      ...args: unknown[]
    ) => nativeSetInterval(handler, timeout === 30_000 ? 250 : timeout, ...args)) as typeof window.setInterval;

    const NativeWebSocket = window.WebSocket;
    class MockZeusWebSocket extends EventTarget {
      readonly url: string;
      readonly protocol = '';
      readonly extensions = '';
      binaryType: BinaryType = 'blob';
      bufferedAmount = 0;
      readyState = NativeWebSocket.CONNECTING;
      onopen: ((this: WebSocket, ev: Event) => unknown) | null = null;
      onmessage: ((this: WebSocket, ev: MessageEvent) => unknown) | null = null;
      onerror: ((this: WebSocket, ev: Event) => unknown) | null = null;
      onclose: ((this: WebSocket, ev: CloseEvent) => unknown) | null = null;

      constructor(url: string | URL) {
        super();
        this.url = String(url);
        window.setTimeout(() => {
          if (this.readyState !== NativeWebSocket.CONNECTING) return;
          this.readyState = NativeWebSocket.OPEN;
          const event = new Event('open');
          this.onopen?.call(this as unknown as WebSocket, event);
          this.dispatchEvent(event);
        }, 0);
      }

      send() {
        /* No realtime socket frames are needed for this policy-sync e2e. */
      }

      close() {
        if (this.readyState === NativeWebSocket.CLOSED) return;
        this.readyState = NativeWebSocket.CLOSED;
        const event = new CloseEvent('close');
        this.onclose?.call(this as unknown as WebSocket, event);
        this.dispatchEvent(event);
      }
    }

    window.WebSocket = MockZeusWebSocket as unknown as typeof WebSocket;
  });

  await page.route(/^https?:\/\/[^/]+\/api(?:\/|\?|$)/, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();

    if (url.pathname === '/api/users/session') {
      await fulfillJson(route, userSession(world.adminHasPricedPlugin));
      return;
    }

    if (url.pathname === '/api/capabilities') {
      await fulfillJson(route, {
        host: 'desktop',
        platform: 'windows',
        architecture: 'x64',
        version: 'e2e',
        lanHttpsUrls: [],
        features: {},
      });
      return;
    }

    if (url.pathname === '/api/radio/selection') {
      await fulfillJson(route, {
        preferred: 'Auto',
        connected: 'Unknown',
        effective: 'Unknown',
        overrideDetection: false,
      });
      return;
    }

    if (url.pathname === '/api/plugins/registry') {
      await fulfillJson(route, {
        sourceUrl: 'https://example.invalid/registry.json',
        catalog: registryCatalog(),
      });
      return;
    }

    if (url.pathname === '/api/plugins/checkout') {
      world.checkoutPosts.push(JSON.parse(request.postData() ?? '{}') as Record<string, unknown>);
      await fulfillJson(route, {
        url: null,
        subscriptionUpdated: false,
        pluginIds: [pluginId],
      });
      return;
    }

    if (url.pathname === `/api/plugins/${pluginId}` && method === 'DELETE') {
      world.installed = false;
      await route.fulfill({ status: 204, body: '' });
      return;
    }

    if (url.pathname === '/api/plugins/install' && method === 'POST') {
      world.installPosts += 1;
      await fulfillJson(route, pluginDto());
      return;
    }

    if (url.pathname === '/api/plugins') {
      await fulfillJson(route, {
        sdkAbi: 1,
        sdkVersion: 'e2e',
        plugins: world.installed ? [pluginDto()] : [],
      });
      return;
    }

    if (url.pathname === '/api/ui/layouts' && method === 'GET') {
      await fulfillJson(route, {
        radioKey: url.searchParams.get('radio') ?? 'default',
        layouts: [],
        activeLayoutId: 'default',
      });
      return;
    }

    if (url.pathname === '/api/ui/layouts') {
      await route.fulfill({ status: 204, body: '' });
      return;
    }

    if (url.pathname === '/api/theme-settings') {
      await fulfillJson(route, { theme: 'dark', overrides: {} });
      return;
    }

    await fulfillJson(route, {});
  });

  return world;
}

test('admin plugin price streams into the Zeus plugin store and gates installed access', async ({ page }) => {
  const pageErrors: string[] = [];
  page.on('pageerror', (err) => pageErrors.push(err.message));
  const world = await stubZeusApi(page);

  await page.goto('/#pa');
  await expect(page.getByRole('region', { name: 'Settings' })).toBeVisible();
  await page.getByRole('tab', { name: 'PLUGINS' }).click();

  const installedPanel = page.getByTestId('plugins-installed');
  await expect(installedPanel).toContainText(pluginName);
  await expect(installedPanel).not.toContainText('Plugin subscriptions required');

  world.adminHasPricedPlugin = true;

  await expect(installedPanel).toContainText('Plugin subscriptions required', { timeout: 5_000 });
  await expect(installedPanel).toContainText('These installed plugins are now managed as paid subscriptions');
  await expect(installedPanel).toContainText('Plugin subscription required - USD 4.00/mo');

  await page.getByRole('button', { name: 'KEEP ACCESS' }).click();
  await expect.poll(() => world.checkoutPosts.length).toBe(1);
  expect(world.checkoutPosts[0]).toMatchObject({ pluginIds: [pluginId] });

  await page.getByRole('tab', { name: 'BROWSE' }).click();
  const browserPanel = page.getByTestId('plugins-browser');
  await expect(browserPanel).toContainText(pluginName);
  await expect(browserPanel).toContainText('Plugin subscription required - USD 4.00/mo');

  await page
    .getByRole('button', { name: `${pluginName} requires a plugin subscription` })
    .click();
  await expect.poll(() => world.checkoutPosts.length).toBe(2);
  expect(world.checkoutPosts[1]).toMatchObject({ pluginIds: [pluginId] });
  expect(world.installPosts).toBe(0);

  await page.getByRole('tab', { name: 'INSTALLED' }).click();
  await page.getByRole('button', { name: 'REMOVE' }).click();
  await expect(page.getByRole('alertdialog')).toContainText(`Remove ${pluginName}`);
  await page.getByRole('button', { name: 'Remove', exact: true }).click();

  await expect.poll(() => world.installed).toBe(false);
  await expect(installedPanel).toContainText('No plugins installed yet');
  await expect(installedPanel).not.toContainText(pluginName);
  expect(pageErrors).toEqual([]);
});
