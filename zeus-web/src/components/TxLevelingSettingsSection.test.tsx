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
});
