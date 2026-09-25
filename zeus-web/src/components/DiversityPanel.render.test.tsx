// SPDX-License-Identifier: GPL-2.0-or-later
//
// The diversity panel must render without looping. A selector that returned
// a fresh function each render sent it into React's update-depth limit and
// the error boundary the moment the operator opened it. This mounts it
// against a two-receiver state and asserts it settles.

import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { useConnectionStore } from '../state/connection-store';
import { DiversityPanel } from './DiversityPanel';

const rx = (index: number, adcSource: number) => ({
  index, enabled: true, adcSource, vfoHz: 7_180_000, mode: 'LSB', filterLowHz: -2900,
  filterHighHz: -100, filterPresetName: null, afGainDb: 0, sampleRateHz: 48_000, muted: false,
});

describe('DiversityPanel', () => {
  let container: HTMLDivElement;
  let root: Root;
  beforeEach(() => {
    (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });
  afterEach(() => {
    act(() => root.unmount());
    container.remove();
  });

  it('renders and settles with two receivers on different ADCs', async () => {
    useConnectionStore.setState({ receivers: [rx(0, 0), rx(1, 1)] } as never);
    await act(async () => {
      root.render(<DiversityPanel />);
    });
    // The partner is the ADC1 jack, not a receiver — the label says so.
    expect(container.textContent).toContain('ADC0');
    expect(container.textContent).toContain('RX2/EXT JACK');
    expect(container.textContent).toContain('ADC1');
  });

  it('does not offer a receiver selector — RX2 stays independent', async () => {
    useConnectionStore.setState({ receivers: [rx(0, 0), rx(1, 0)] } as never);
    await act(async () => {
      root.render(<DiversityPanel />);
    });
    expect(container.textContent).not.toContain('COMBINE RX1 WITH');
    expect(container.querySelector('.ps-pill.dim')).toBeNull();
  });
});
