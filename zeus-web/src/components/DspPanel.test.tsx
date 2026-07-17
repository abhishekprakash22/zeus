// SPDX-License-Identifier: GPL-2.0-or-later
//
// The visible DSP SMART button must be a real arming control, not a silent
// one-shot. Operators expect it to visibly select and expose the automation
// status row when clicked.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

// The RX Suite button pops out an independent OS window; stub the opener.
vi.mock('../layout/workspace-windows', () => ({
  openAudioSuiteWindow: vi.fn(),
}));

import { NR_CONFIG_DEFAULT } from '../api/client';
import { useAudioSuiteStore } from '../state/audio-suite-store';
import { openAudioSuiteWindow } from '../layout/workspace-windows';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';
import { useSmartNrStore } from '../state/smart-nr-store';
import { DspPanel } from './DspPanel';

const openAudioSuiteWindowMock = vi.mocked(openAudioSuiteWindow);

describe('DspPanel SMART control', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    openAudioSuiteWindowMock.mockClear();
    useSmartNrStore.getState().resetSettings();
    useConnectionStore.setState({
      status: 'Connected',
      mode: 'USB',
      nr: { ...NR_CONFIG_DEFAULT },
    });
    useAudioSuiteStore.setState({
      rxOpen: false,
      txOpen: false,
      isOpen: false,
      suiteRoute: 'tx',
    });
    useDisplayStore.setState({
      panDb: null,
      panValid: false,
      lastSeq: 0,
    });
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    useSmartNrStore.getState().resetSettings();
    useConnectionStore.setState({
      status: 'Disconnected',
      nr: { ...NR_CONFIG_DEFAULT },
    });
    useAudioSuiteStore.setState({
      rxOpen: false,
      txOpen: false,
      isOpen: false,
      suiteRoute: 'tx',
    });
  });

  it('arms and disarms Smart NR from the panel button', () => {
    act(() => {
      root.render(<DspPanel />);
    });

    const smart = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((b) => b.textContent?.trim() === 'SMART');

    expect(smart).toBeDefined();
    expect(smart!.getAttribute('aria-pressed')).toBe('false');
    expect(smart!.className).not.toContain('active');

    act(() => {
      smart!.click();
    });

    expect(useSmartNrStore.getState().automationMode).toBe('auto');
    expect(smart!.getAttribute('aria-pressed')).toBe('true');
    expect(smart!.className).toContain('active');
    expect(container.textContent).toContain('AUTO');
    expect(container.textContent).toContain('WAIT');

    act(() => {
      smart!.click();
    });

    expect(useSmartNrStore.getState().automationMode).toBe('manual');
    expect(smart!.getAttribute('aria-pressed')).toBe('false');
    expect(smart!.className).not.toContain('active');
  });

  it('applies a suggested Smart NR profile from the status row', () => {
    const suggested = { ...NR_CONFIG_DEFAULT, nrMode: 'Emnr' as const, emnrPost2Factor: 18 };
    useSmartNrStore.getState().setAutomationMode('suggest');
    useSmartNrStore.getState().setStatus({
      atUtc: '2026-06-14T00:00:00.000Z',
      profile: 'NR2',
      reason: 'SSB noise profile',
      maxSnrDb: 16,
      occupancyPct: 24,
      coherentOccupancyPct: 18,
      impulsivePct: 0,
      peakCount: 7,
      coherentPeakCount: 5,
      pending: false,
      applied: false,
      nr: suggested,
    });

    act(() => {
      root.render(<DspPanel />);
    });

    const apply = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((b) => b.textContent?.trim() === 'APPLY');

    expect(apply).toBeDefined();

    act(() => {
      apply!.click();
    });

    expect(useConnectionStore.getState().nr.nrMode).toBe('Emnr');
    expect(useSmartNrStore.getState().status?.applied).toBe(true);
    expect(container.textContent).toContain('APPLIED');
  });

  it('cycles NR3 before NR4 when an RNNoise model is active', () => {
    useConnectionStore.setState({
      status: 'Connected',
      nr: { ...NR_CONFIG_DEFAULT, nrMode: 'Off' },
      wdspNr3RnnrAvailable: true,
      nr3ModelName: 'rnnoise_nr3_default.rnn',
      nr3UsingBundledDefault: false,
    });

    act(() => {
      root.render(<DspPanel />);
    });

    const nrButton = () => Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((b) => {
        const title = b.getAttribute('title') ?? '';
        return title.includes('Noise reduction off')
          || title.includes('NR1')
          || title.includes('NR2')
          || title.includes('NR3')
          || title.includes('NR4');
      });

    expect(useConnectionStore.getState().nr.nrMode).toBe('Off');

    act(() => { nrButton()!.click(); });
    expect(useConnectionStore.getState().nr.nrMode).toBe('Anr');

    act(() => { nrButton()!.click(); });
    expect(useConnectionStore.getState().nr.nrMode).toBe('Emnr');

    act(() => { nrButton()!.click(); });
    expect(useConnectionStore.getState().nr.nrMode).toBe('Rnnr');
    expect(nrButton()!.textContent?.trim()).toBe('NR3');

    act(() => { nrButton()!.click(); });
    expect(useConnectionStore.getState().nr.nrMode).toBe('Sbnr');
    expect(nrButton()!.textContent?.trim()).toBe('NR4');
  });

  it('pops the RX Audio Suite out into its own window from the DSP panel', () => {
    act(() => {
      root.render(<DspPanel />);
    });

    const rxSuite = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((b) => b.textContent?.trim() === 'RX Suite');

    expect(rxSuite).toBeDefined();

    act(() => {
      rxSuite!.click();
    });

    // The suite pops out into an independent OS window (openRx →
    // openAudioSuiteWindow), so no in-app open flag is toggled.
    expect(openAudioSuiteWindowMock).toHaveBeenCalledWith('rx');
    expect(useAudioSuiteStore.getState().rxOpen).toBe(false);
  });
});
