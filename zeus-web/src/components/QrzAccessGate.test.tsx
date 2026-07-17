// SPDX-License-Identifier: GPL-2.0-or-later

import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { QrzAccessGate } from './QrzAccessGate';
import { useQrzStore } from '../state/qrz-store';
import { useUserAccessStore } from '../state/user-access-store';
import type { QrzStation } from '../api/qrz';
import type { ZeusUserSession } from '../api/users';

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
}

const qrzHome: QrzStation = {
  callsign: 'N9WAR',
  name: 'Operator',
  firstName: 'Zeus',
  country: 'United States',
  state: 'IL',
  city: 'Chicago',
  grid: 'EN61',
  lat: null,
  lon: null,
  dxcc: 291,
  cqZone: 4,
  ituZone: 8,
  imageUrl: null,
  licenseClass: null,
  licenseCodes: null,
  licenseEffectiveDate: null,
  licenseExpiresDate: null,
  email: null,
  acceptsLotw: null,
  acceptsEqsl: null,
  acceptsMailQsl: null,
  qslManager: null,
  gmtOffset: null,
  timeZone: null,
  observesDst: null,
  born: null,
};

const signedInSession: ZeusUserSession = {
  qrzConnected: true,
  callsign: 'N9WAR',
  displayName: 'N9WAR',
  accessAllowed: false,
  isAdmin: false,
  hasQrzXmlSubscription: true,
  subscriptionStatus: 'manual',
  subscriptionExpiresUtc: null,
  pluginAccessMode: 'all',
  pluginEntitlements: [],
  managedPlugins: [],
  denialReason: 'Access disabled by Zeus admin',
  user: null,
};

const loggedOutSession: ZeusUserSession = {
  qrzConnected: false,
  callsign: null,
  displayName: null,
  accessAllowed: false,
  isAdmin: false,
  hasQrzXmlSubscription: false,
  subscriptionStatus: 'none',
  subscriptionExpiresUtc: null,
  pluginAccessMode: 'all',
  pluginEntitlements: [],
  managedPlugins: [],
  denialReason: 'QRZ login required',
  user: null,
};

function resetStores(session: ZeusUserSession) {
  useQrzStore.setState({
    connected: session.qrzConnected,
    hasXmlSubscription: session.hasQrzXmlSubscription,
    hasApiKey: false,
    home: session.qrzConnected ? qrzHome : null,
    rememberedUsername: 'N9WAR',
    lastLookup: null,
    nameCache: {},
    loginInFlight: false,
    lookupInFlight: false,
    loginError: null,
    lookupError: null,
  });
  useUserAccessStore.setState({
    checked: true,
    loading: false,
    adminLoading: false,
    saving: false,
    error: null,
    adminError: null,
    session,
    users: [],
    managedPlugins: [],
  });
}

describe('QrzAccessGate', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    vi.unstubAllGlobals();
  });

  function stubSession(session: ZeusUserSession) {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockImplementation((input) => {
        if (input === '/api/users/session') {
          return Promise.resolve(jsonResponse(session));
        }
        return Promise.resolve(jsonResponse({}));
      }),
    );
  }

  it('uses an existing QRZ session instead of showing another password prompt', async () => {
    resetStores(signedInSession);
    stubSession(signedInSession);

    await act(async () => {
      root.render(<QrzAccessGate />);
    });

    expect(container.textContent).toContain('QRZ Account Active');
    expect(container.textContent).toContain('N9WAR is the Zeus username.');
    expect(container.textContent).toContain('REFRESH ACCESS');
    expect(container.textContent).toContain('SIGN OUT');
    expect(container.querySelector('input[type="password"]')).toBeNull();
    expect(
      Array.from(container.querySelectorAll('button')).some(
        (button) => button.textContent?.trim() === 'SIGN IN',
      ),
    ).toBe(false);
  });

  it('shows the integrated QRZ login form when no QRZ session exists', async () => {
    resetStores(loggedOutSession);
    stubSession(loggedOutSession);

    await act(async () => {
      root.render(<QrzAccessGate />);
    });

    expect(container.textContent).toContain('QRZ Login Required');
    expect(container.textContent).toContain('QRZ callsign is the Zeus username.');
    expect(container.querySelector('input[autocomplete="username"]')).not.toBeNull();
    expect(container.querySelector('input[type="password"]')).not.toBeNull();
    expect(
      Array.from(container.querySelectorAll('button')).some(
        (button) => button.textContent?.trim() === 'SIGN IN',
      ),
    ).toBe(true);
  });
});
