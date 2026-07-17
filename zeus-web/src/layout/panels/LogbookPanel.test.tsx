// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { act, render } from '../../components/meters/__tests__/harness';
import type { WorkspaceCtx } from '../WorkspaceContext';

const mocks = vi.hoisted(() => ({ liveRenders: 0 }));

vi.mock('../../components/design/LogbookLive', async () => {
  const React = await import('react');
  return {
    LogbookLive: () => {
      React.useEffect(() => {
        mocks.liveRenders += 1;
      }, []);
      return React.createElement('div', { 'data-testid': 'logbook-live' });
    },
  };
});

import { LogbookPanel } from './LogbookPanel';
import { WorkspaceContext } from '../WorkspaceContext';
import { useLogbookPluginStore } from '../../state/logbook-plugin-store';
import { useLoggerStore } from '../../state/logger-store';
import type { LotwStatus } from '../../api/lotw';

const lotwMocks = vi.hoisted(() => {
  const status = {
    configured: false,
    stationLocation: null,
    lastSyncUtc: null,
    lastQslSince: null,
    lastResult: null,
    tqslPath: null,
    stationDefaults: {
      rig: null,
      antenna: null,
      txPowerW: null,
    },
  } satisfies LotwStatus;
  return {
    getLotwStatus: vi.fn(async () => status),
    saveLotwCredentials: vi.fn(async () => status),
    saveLotwSettings: vi.fn(async () => status),
    syncLotwConfirmations: vi.fn(async () => ({
      lotw: { fetched: 0, matched: 0, unmatched: 0 },
      qrz: { fetched: 0, matched: 0, unmatched: 0 },
      lastQslSince: null,
      lastSyncUtc: null,
      message: 'Synced',
    })),
    uploadLotwEntries: vi.fn(async () => ({
      success: true,
      message: 'Uploaded',
      uploadedCount: 0,
      exportedPath: null,
      tqslPath: null,
      exitCode: null,
      errorTail: null,
      tqslMissing: false,
    })),
  };
});

vi.mock('../../api/lotw', () => lotwMocks);

function makeWorkspaceCtx(): WorkspaceCtx {
  return {
    connected: true,
    moxOn: false,
    tunOn: false,
    mode: 'USB',
    vfoHz: 14_074_000,
    callsign: '',
    setCallsign: vi.fn(),
    terminatorActive: false,
    imageMode: false,
    bgActive: false,
    panBackground: 'basic',
    backgroundImage: null,
    backgroundImageFit: 'fill',
    enriching: false,
    lookupKey: 0,
    contact: null,
    workedSummary: null,
    workedSummaryLoading: false,
    qrzLookupError: null,
    qrzActive: false,
    mapAvailable: false,
    setMapAvailable: vi.fn(),
    mapInteractive: false,
    effectiveHome: null,
    beamOverrideDeg: null,
    setBeamOverrideDeg: vi.fn(),
    beamInputStr: '',
    setBeamInputStr: vi.fn(),
    rotLiveAz: null,
    sp: 0,
    lp: 0,
    dist: 0,
    heroTitle: null,
    csInputRef: { current: null },
    runQrzLookup: vi.fn(),
    onCallsignSubmit: vi.fn(),
    submitBeam: vi.fn(),
    handleLogQso: vi.fn(),
    handleClearQrz: vi.fn(),
    dspActive: false,
    logbookTitle: 'Logbook',
    logbookActions: createElement('button', { type: 'button' }, 'Import'),
  };
}

function renderPanel() {
  return render(
    createElement(
      WorkspaceContext.Provider,
      { value: makeWorkspaceCtx() },
      createElement(LogbookPanel),
    ),
  );
}

describe('LogbookPanel plugin gate', () => {
  beforeEach(() => {
    mocks.liveRenders = 0;
    act(() => {
      useLogbookPluginStore.setState({ installed: false, live: false, probed: true });
      useLoggerStore.setState({ entries: [] });
    });
  });

  it('renders the unavailable panel chrome when the plugin is not live', () => {
    const { container, unmount } = renderPanel();

    expect(container.textContent).toContain('Logbook unavailable');
    expect(container.textContent).toContain('Install the Logbook plugin from Settings → Plugins');
    expect(container.querySelector('[data-testid="logbook-live"]')).toBeNull();
    expect((container.querySelector('input[type="search"]') as HTMLInputElement).disabled).toBe(true);
    expect(mocks.liveRenders).toBe(0);

    unmount();
  });

  it('renders LogbookLive and enables search when the plugin is live', () => {
    act(() => {
      useLogbookPluginStore.setState({ installed: true, live: true, probed: true });
    });

    const { container, unmount } = renderPanel();

    expect(container.querySelector('[data-testid="logbook-live"]')).not.toBeNull();
    expect(container.textContent).not.toContain('Logbook unavailable');
    expect((container.querySelector('input[type="search"]') as HTMLInputElement).disabled).toBe(false);
    expect(mocks.liveRenders).toBe(1);

    unmount();
  });

  it('opens the settings popover without throwing when the plugin is live', () => {
    act(() => {
      useLogbookPluginStore.setState({ installed: true, live: true, probed: true });
    });

    const { container, unmount } = renderPanel();
    const settings = container.querySelector<HTMLButtonElement>(
      'button[aria-label="Logbook settings"]',
    );
    expect(settings).not.toBeNull();

    act(() => {
      settings!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });

    const dialog = document.body.querySelector(
      '[role="dialog"][aria-label="Logbook Settings"]',
    );
    expect(dialog).not.toBeNull();

    unmount();
    dialog?.remove();
  });
});
