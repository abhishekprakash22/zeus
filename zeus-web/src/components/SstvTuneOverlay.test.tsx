// SPDX-License-Identifier: GPL-2.0-or-later
// SSTV tuning marks: tones land at dial + f on USB, dial − f on LSB, and
// nothing is drawn unless SSTV is engaged on a sideband.

import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';
import { useSstvStore } from '../state/sstv-store';
import { SstvTuneOverlay } from './SstvTuneOverlay';

// 10 kHz span centred on 14.230 MHz: 1 Hz = 0.01 % of the width.
const CENTER_HZ = 14_230_000;

describe('SstvTuneOverlay', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    useDisplayStore.setState({
      centerHz: BigInt(CENTER_HZ),
      hzPerPixel: 10,
      panDb: new Float32Array(1000),
    });
    useConnectionStore.setState({ vfoHz: CENTER_HZ, mode: 'USB' });
    useSstvStore.setState({ panelOpen: true });
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    useSstvStore.setState({ panelOpen: false });
    useDisplayStore.setState({ panDb: null, hzPerPixel: 0 });
  });

  const marks = () =>
    Array.from(container.querySelectorAll<HTMLElement>('.sstv-tune-mark')).map((el) => ({
      label: el.textContent,
      left: parseFloat(el.style.left),
    }));

  it('puts the tones above the dial on USB', () => {
    act(() => root.render(<SstvTuneOverlay />));
    expect(marks()).toEqual([
      { label: 'SYNC', left: 62 },
      { label: 'BLK', left: 65 },
      { label: 'WHT', left: 73 },
    ]);
  });

  it('mirrors them below the dial on LSB', () => {
    useConnectionStore.setState({ mode: 'LSB' });
    act(() => root.render(<SstvTuneOverlay />));
    expect(marks().map((m) => m.left)).toEqual([38, 35, 27]);
  });

  it('draws nothing when SSTV is not engaged, or off a sideband', () => {
    useSstvStore.setState({ panelOpen: false });
    act(() => root.render(<SstvTuneOverlay />));
    expect(marks()).toEqual([]);
    useSstvStore.setState({ panelOpen: true });
    useConnectionStore.setState({ mode: 'AM' });
    act(() => root.render(<SstvTuneOverlay />));
    expect(marks()).toEqual([]);
  });
});
