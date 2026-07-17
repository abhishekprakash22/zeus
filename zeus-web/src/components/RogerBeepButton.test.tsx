/** @vitest-environment jsdom */

import { createElement } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { setRogerBeep } from '../api/client';
import { useTxStore } from '../state/tx-store';
import { act, render } from './meters/__tests__/harness';
import { RogerBeepButton } from './RogerBeepButton';

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return {
    ...actual,
    setRogerBeep: vi.fn(),
  };
});

describe('RogerBeepButton', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useTxStore.setState({ rogerBeepEnabled: false });
    vi.mocked(setRogerBeep).mockResolvedValue({ rogerBeepEnabled: true });
  });

  it('optimistically toggles and persists the roger beep setting', async () => {
    const { container, unmount } = render(createElement(RogerBeepButton));
    const button = container.querySelector<HTMLButtonElement>('button');

    await act(async () => {
      button?.click();
      await Promise.resolve();
    });

    expect(button).toBeTruthy();
    expect(setRogerBeep).toHaveBeenCalledWith(true);
    expect(useTxStore.getState().rogerBeepEnabled).toBe(true);

    unmount();
  });
});
