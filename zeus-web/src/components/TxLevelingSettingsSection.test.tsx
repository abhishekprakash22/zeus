// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { TX_LEVELING_CONFIG_DEFAULT } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';
import { TxLevelingSettingsSection } from './TxLevelingSettingsSection';

describe('TxLevelingSettingsSection', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    useConnectionStore.setState({
      status: 'Disconnected',
      txLeveling: { ...TX_LEVELING_CONFIG_DEFAULT },
    });
    useTxStore.setState({ levelerMaxGainDb: 8 });
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    useConnectionStore.setState({
      status: 'Disconnected',
      txLeveling: { ...TX_LEVELING_CONFIG_DEFAULT },
    });
    useTxStore.setState({ levelerMaxGainDb: 8 });
  });

  it('keeps TX chain controls editable while the radio is disconnected', () => {
    act(() => {
      root.render(<TxLevelingSettingsSection />);
    });

    const controls = [
      ...Array.from(container.querySelectorAll<HTMLInputElement>('input')),
      ...Array.from(container.querySelectorAll<HTMLButtonElement>('button')),
    ];

    expect(controls.length).toBeGreaterThan(0);
    expect(controls.every((control) => !control.disabled)).toBe(true);
  });

  it('shows CESSB waiting for the compressor, and active once COMP is on', () => {
    act(() => {
      root.render(<TxLevelingSettingsSection />);
    });
    const note = () => container.querySelector('[data-testid="cessb-note"]')?.textContent;
    expect(note()).toBe('waits for the compressor — turn COMP on');
    act(() => {
      useConnectionStore.setState({
        txLeveling: { ...TX_LEVELING_CONFIG_DEFAULT, compressorEnabled: true },
      });
    });
    expect(note()).toBe('active with the compressor');
    expect(container.querySelector('[data-testid="cessb-toggle"]')?.className).toContain('active');
  });

  it('defaults CESSB on, and older payloads without the field normalise to on', async () => {
    const { normalizeTxLeveling } = await import('../api/client');
    expect(TX_LEVELING_CONFIG_DEFAULT.cessbEnabled).toBe(true);
    expect(normalizeTxLeveling({ compressorEnabled: true }).cessbEnabled).toBe(true);
    expect(normalizeTxLeveling({ compressorEnabled: true, cessbEnabled: false }).cessbEnabled).toBe(false);
  });
});
