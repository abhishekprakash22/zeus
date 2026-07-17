// SPDX-License-Identifier: GPL-2.0-or-later
//
// Signal Intelligence status must explain the confidence-aware classifier so
// operators can tell coherent weak signals from raw impulsive peaks.

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { SignalIntelligenceSettingsSection } from './SignalIntelligenceSettingsSection';
import { useSignalEnhanceStore } from '../dsp/signal-estimator';
import { useConnectionStore } from '../state/connection-store';

describe('SignalIntelligenceSettingsSection', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    useConnectionStore.setState({ mode: 'USB' });
    useSignalEnhanceStore.getState().setSignalEnhanceSceneStatus(null);
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    useSignalEnhanceStore.getState().setSignalEnhanceSceneStatus(null);
  });

  it('shows coherent and impulsive scene metrics in the status row', () => {
    useSignalEnhanceStore.getState().setSignalEnhanceSceneStatus({
      atUtc: '2026-06-15T00:00:00.000Z',
      profileId: 'voice',
      baseProfileId: 'voice',
      reason: 'impulsive artifacts',
      peakCount: 4,
      coherentPeakCount: 1,
      peaksPer10Khz: 2.4,
      occupiedPct: 12.5,
      coherentOccupiedPct: 2.5,
      impulsivePct: 4.3,
      maxSnrDb: 18.2,
      coherentMaxSnrDb: 11.1,
    });

    act(() => {
      root.render(<SignalIntelligenceSettingsSection />);
    });

    expect(container.textContent).toContain('impulsive artifacts');
    expect(container.textContent).toContain('COH 2.5%');
    expect(container.textContent).toContain('CPK 1');
    expect(container.textContent).toContain('IMP 4.3%');
  });
});
