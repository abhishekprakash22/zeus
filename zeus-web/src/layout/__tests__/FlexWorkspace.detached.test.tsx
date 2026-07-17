// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { act, render } from '../../components/meters/__tests__/harness';
import { useLayoutStore } from '../../state/layout-store';
import { FlexWorkspace } from '../FlexWorkspace';
import { WorkspaceContext, type WorkspaceCtx } from '../WorkspaceContext';
import type { WorkspaceTile } from '../workspace';

const panelMocks = vi.hoisted(() => {
  const Panel = () => null;
  const defs = {
    logbook: {
      id: 'logbook',
      name: 'Logbook',
      category: 'log',
      tags: [],
      component: Panel,
    },
    qrz: {
      id: 'qrz',
      name: 'QRZ Lookup',
      category: 'tools',
      tags: [],
      component: Panel,
    },
  };
  return { defs };
});

vi.mock('../panels', () => ({
  getPanelDef: (id: string) =>
    panelMocks.defs[id as keyof typeof panelMocks.defs],
  PANELS: panelMocks.defs,
  PANEL_CATEGORIES: [],
  PANEL_CATEGORY_LABELS: {},
}));

vi.mock('../../plugins/runtime/usePluginPanels', () => ({
  usePluginPanels: () => [],
}));

vi.mock('react-grid-layout', async () => {
  const React = await import('react');
  return {
    ResponsiveGridLayout: ({
      children,
      className,
    }: {
      children: React.ReactNode;
      className?: string;
    }) =>
      React.createElement(
        'div',
        { className: `${className ?? ''} react-grid-layout` },
        children,
      ),
    useContainerWidth: () => ({
      width: 960,
      mounted: true,
      containerRef: { current: null },
    }),
  };
});

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
    logbookActions: null,
  };
}

function tile(patch: Partial<WorkspaceTile> & { uid: string }): WorkspaceTile {
  return {
    panelId: 'logbook',
    x: 0,
    y: 0,
    w: 6,
    h: 12,
    ...patch,
  };
}

function setDetachedTiles(tiles: WorkspaceTile[]) {
  act(() => {
    useLayoutStore.setState({
      layouts: [
        {
          id: 'detached',
          name: 'Detached',
          layoutJson: JSON.stringify({ schemaVersion: 8, tiles }),
        },
      ],
      activeLayoutId: 'default',
      workspace: { schemaVersion: 8, tiles: [] },
      isLoaded: true,
      addPanelOpen: false,
      settingsViewOpen: false,
      settingsInitialTab: undefined,
    });
  });
}

function renderDetachedWorkspace() {
  return render(
    createElement(
      WorkspaceContext.Provider,
      { value: makeWorkspaceCtx() },
      createElement(FlexWorkspace, {
        layoutId: 'detached',
        showAddPanelModal: false,
      }),
    ),
  );
}

describe('FlexWorkspace detached layout rendering', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  beforeEach(() => {
    vi.stubGlobal(
      'ResizeObserver',
      class ResizeObserver {
        observe() {}
        unobserve() {}
        disconnect() {}
      },
    );
    setDetachedTiles([]);
  });

  it('renders a single detached tile in fill mode without the RGL grid', () => {
    setDetachedTiles([tile({ uid: 'tile-logbook' })]);

    const { container, unmount } = renderDetachedWorkspace();

    expect(container.querySelector('.detached-single-tile-fill')).not.toBeNull();
    expect(container.querySelector('.all-panels-grid')).toBeNull();
    expect(container.querySelector('[data-panel-id="logbook"]')).not.toBeNull();

    unmount();
  });

  it('keeps multi-tile detached layouts on the RGL grid', () => {
    setDetachedTiles([
      tile({ uid: 'tile-logbook' }),
      tile({ uid: 'tile-qrz', panelId: 'qrz', x: 6 }),
    ]);

    const { container, unmount } = renderDetachedWorkspace();

    expect(container.querySelector('.detached-single-tile-fill')).toBeNull();
    expect(container.querySelector('.all-panels-grid')).not.toBeNull();

    unmount();
  });
});
