/** @vitest-environment jsdom */

// Covers the keyboard-pin path added for issue #1303: with a popover chip
// focused, pressing 1/2/3 pins that option into the matching favorite slot
// through the same `dropOnFav` logic as the drag gesture (including the
// displacement swap), and a disabled chip is guarded against pinning.

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { act, render } from '../meters/__tests__/harness';
import { useToolbarFavoritesStore } from '../../state/toolbar-favorites-store';
import { ToolbarFavorites, type ToolbarOption } from './ToolbarFavorites';

// The store debounces a POST to /api/toolbar-settings on every setFavorites
// and fetches once on module load. Stub both so tests never touch the network.
vi.mock('../../api/toolbar-settings', () => ({
  fetchToolbarSettings: vi.fn(() =>
    Promise.resolve({ mode: null, band: null, step: null, stepHz: null }),
  ),
  updateToolbarSettings: vi.fn(() => Promise.resolve()),
}));

const OPTIONS: readonly ToolbarOption[] = [
  { key: 'A', label: 'A' },
  { key: 'B', label: 'B' },
  { key: 'C', label: 'C' },
  { key: 'D', label: 'D', disabled: true },
  { key: 'E', label: 'E' },
];

function renderFavorites() {
  const onSelect = vi.fn();
  const view = render(
    createElement(ToolbarFavorites, {
      kind: 'mode',
      label: 'MODE',
      options: OPTIONS,
      currentKey: null,
      onSelect,
    }),
  );
  return { ...view, onSelect };
}

function openPopover(container: HTMLElement) {
  const toggle = Array.from(
    container.querySelectorAll<HTMLButtonElement>('button'),
  ).find((b) => b.textContent === '⋯');
  expect(toggle).toBeTruthy();
  act(() => {
    toggle!.click();
  });
}

// Popover chips are portaled into document.body, so query the whole document.
function chipByLabel(label: string): HTMLButtonElement {
  const chip = Array.from(
    document.querySelectorAll<HTMLButtonElement>('.toolbar-fav__popover .toolbar-fav__chip'),
  ).find((b) => b.textContent === label);
  expect(chip).toBeTruthy();
  return chip!;
}

function pressKey(el: HTMLElement, key: string) {
  el.focus();
  act(() => {
    el.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
  });
}

function slots(): string[] {
  return useToolbarFavoritesStore.getState().mode;
}

describe('ToolbarFavorites keyboard-pin', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // Seed three valid slots so the stale-slot repair does not kick in.
    useToolbarFavoritesStore.setState({ mode: ['A', 'B', 'C'] });
  });

  afterEach(() => {
    // Flush the store's debounced save so its timer never fires mid-next-test.
    useToolbarFavoritesStore.setState({ mode: ['A', 'B', 'C'] });
  });

  it('pins a focused non-favorite chip into the pressed slot', () => {
    const { container, unmount } = renderFavorites();
    openPopover(container);

    // Focus the "E" chip (not currently a favorite) and press "2".
    pressKey(chipByLabel('E'), '2');

    // Slot index 1 now holds E; the other slots are untouched.
    expect(slots()).toEqual(['A', 'E', 'C']);

    unmount();
  });

  it('swaps displaced favorite to match drag (dropOnFav) semantics', () => {
    const { container, unmount } = renderFavorites();
    openPopover(container);

    // "A" is already pinned at slot 0. Pressing "3" pins it at slot 2; the
    // option displaced from slot 2 ("C") moves back into A's old slot 0 —
    // exactly what dragging A onto slot 2 would do.
    pressKey(chipByLabel('A'), '3');

    expect(slots()).toEqual(['C', 'B', 'A']);

    unmount();
  });

  it('ignores keys other than 1/2/3', () => {
    const { container, unmount } = renderFavorites();
    openPopover(container);

    pressKey(chipByLabel('E'), '4');

    expect(slots()).toEqual(['A', 'B', 'C']);

    unmount();
  });

  it('guards a disabled chip against keyboard-pin', () => {
    const { container, unmount } = renderFavorites();
    openPopover(container);

    const disabled = chipByLabel('D');
    expect(disabled.disabled).toBe(true);

    // keydown still reaches React on a disabled button, so the in-handler
    // disabled guard is what must prevent the pin.
    pressKey(disabled, '2');

    expect(slots()).toEqual(['A', 'B', 'C']);

    unmount();
  });
});
