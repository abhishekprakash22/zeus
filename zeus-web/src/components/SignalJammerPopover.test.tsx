/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { useEasterEggStore } from '../state/easter-egg-store';
import { useSignalJammerStore } from '../state/signal-jammer-store';
import { act, render } from './meters/__tests__/harness';
import { SignalJammerPopover } from './SignalJammerPopover';

function setInputValue(input: HTMLInputElement, value: string) {
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
  setter?.call(input, value);
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

describe('SignalJammerPopover', () => {
  beforeEach(() => {
    useEasterEggStore.setState({
      hardwareUnlocked: true,
      signalJammerPopoverOpen: true,
      boltClicks: 0,
    });
    useSignalJammerStore.getState().__resetForTests();
  });
  afterEach(() => vi.restoreAllMocks());

  it('enables TX Testing Tools from the popout', () => {
    const { container, unmount } = render(createElement(SignalJammerPopover));
    const dialog = container.querySelector<HTMLElement>('[role="dialog"]');
    const checkbox = container.querySelector<HTMLInputElement>('input[type="checkbox"]');

    act(() => {
      checkbox?.click();
    });

    expect(dialog?.getAttribute('aria-label')).toBe('TX Testing Tools');
    expect(container.querySelector('.sj-title')?.textContent).toBe('TX Testing Tools');
    expect(checkbox).toBeTruthy();
    expect(useSignalJammerStore.getState().enabled).toBe(true);

    unmount();
  });

  it('updates the TX test level from the slider', () => {
    const { container, unmount } = render(createElement(SignalJammerPopover));
    const slider = Array.from(container.querySelectorAll<HTMLInputElement>('input[type="range"]'))
      .find((input) => input.min === '0' && input.max === '100');

    act(() => {
      if (!slider) return;
      setInputValue(slider, '64');
    });

    expect(slider).toBeTruthy();
    expect(useSignalJammerStore.getState().level).toBe(64);

    unmount();
  });

  it('updates the typed spectrogram text', () => {
    const { container, unmount } = render(createElement(SignalJammerPopover));
    const input = container.querySelector<HTMLInputElement>('input[type="text"]');

    act(() => {
      if (!input) return;
      setInputValue(input, 'N9WAR');
    });

    expect(input).toBeTruthy();
    expect(useSignalJammerStore.getState().textSoundText).toBe('N9WAR');

    unmount();
  });

  it('renders typed spectrogram preview in a scrollable fixed-size window', () => {
    const { container, unmount } = render(createElement(SignalJammerPopover));
    const frame = container.querySelector<HTMLDivElement>('.sj-text-preview-window');
    const preview = container.querySelector<HTMLDivElement>('.sj-text-preview');

    expect(frame).toBeTruthy();
    expect(preview).toBeTruthy();
    expect(preview?.style.gridTemplateRows).toContain('42');
    expect(preview?.style.gridTemplateColumns).toContain('175');
    expect(preview?.style.width).toBe('525px');
    expect(preview?.style.height).toBe('126px');

    unmount();
  });

  it('queues typed spectrogram audio for TX when WRITE is clicked', () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(
        JSON.stringify({
          enabled: false,
          preset: 'hash',
          level: 28,
          toneHz: 980,
          driftHz: 80,
          pulseRateHz: 3.5,
          textQueued: true,
          textPendingSamples: 960,
        }),
        { headers: { 'content-type': 'application/json' } },
      ),
    );
    const { container, unmount } = render(createElement(SignalJammerPopover));
    const writeButton = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((button) => button.textContent?.includes('WRITE'));

    act(() => {
      writeButton?.click();
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/tx/qrm/text',
      expect.objectContaining({
        method: 'POST',
        headers: { 'content-type': 'application/json' },
      }),
    );
    const body = JSON.parse(fetchMock.mock.calls[0]?.[1]?.body as string) as {
      autoTransmit?: boolean;
      samplesBase64?: string;
      sampleRate?: number;
    };
    expect(body.autoTransmit).toBe(true);
    expect(body.sampleRate).toBe(48000);
    expect(body.samplesBase64).toEqual(expect.any(String));

    unmount();
  });

  it('does not post oversized typed spectrogram audio', () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response('{}', { headers: { 'content-type': 'application/json' } }),
    );
    useSignalJammerStore.setState({
      textSoundText: 'A'.repeat(32),
      textPixelsPerSecond: 2,
    });
    const { container, unmount } = render(createElement(SignalJammerPopover));
    const writeButton = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((button) => button.textContent?.includes('WRITE'));

    expect(container.querySelector('.sj-text-meta')?.textContent).toBe('Too long');
    expect(writeButton?.disabled).toBe(true);

    act(() => {
      writeButton?.click();
    });

    expect(fetchMock).not.toHaveBeenCalled();

    unmount();
  });
});
