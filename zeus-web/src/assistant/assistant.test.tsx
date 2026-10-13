// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, type RenderResult } from '../components/meters/__tests__/harness';
import { AssistantDrawer } from './AssistantDrawer';
import { historyFor, useAssistantStore, type AssistantSettingsDto } from './assistant-store';

const ON: AssistantSettingsDto = {
  enabled: true, provider: 'anthropic', baseUrl: 'https://api.anthropic.com', model: 'claude-sonnet-5-5',
  hasApiKey: true, apiKeyHint: '…3456',
};

let mounted: RenderResult | null = null;
const text = () => mounted!.container.textContent ?? '';

beforeEach(() => {
  useAssistantStore.setState({ open: true, busy: false, messages: [], settings: ON, manualSections: 70 });
});
afterEach(() => {
  mounted?.unmount();
  mounted = null;
  vi.unstubAllGlobals();
});

describe('assistant store', () => {
  it('replays only real text turns, last six', () => {
    const msgs = Array.from({ length: 10 }, (_, i) => ({ role: (i % 2 ? 'assistant' : 'user') as 'user' | 'assistant', text: `t${i}` }));
    msgs.push({ role: 'assistant', text: '', error: 'boom' } as never);
    const h = historyFor(msgs);
    expect(h).toHaveLength(6);
    expect(h[5]).toEqual({ role: 'assistant', text: 't9' });
  });

  it('asks the radio and records the answer with its trace and citations', async () => {
    const fetchMock = vi.fn(async () =>
      new Response(JSON.stringify({
        text: 'RX1 is muted. Tap MUTE.',
        trace: [{ tool: 'get_radio_state', summary: 'get_radio_state()' }],
        citations: ['Receiving › Mute'],
        error: null,
      })));
    vi.stubGlobal('fetch', fetchMock);
    await useAssistantStore.getState().ask("I can't hear anything");
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/assistant/ask');
    expect(JSON.parse(init.body as string)).toEqual({ question: "I can't hear anything", history: [] });
    const last = useAssistantStore.getState().messages.at(-1)!;
    expect(last.text).toContain('muted');
    expect(last.citations).toEqual(['Receiving › Mute']);
    expect(useAssistantStore.getState().busy).toBe(false);
  });

  it('turns a refused settings write into the at-the-radio explanation', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 403 })));
    const err = await useAssistantStore.getState().saveSettings({ enabled: true });
    expect(err).toContain('only be changed at the radio');
  });
});

describe('assistant drawer', () => {
  it('says it is read-only and explains what is sent', async () => {
    await act(async () => {
      mounted = render(createElement(AssistantDrawer));
    });
    expect(text()).toContain('Read-only');
    expect(text()).toContain("can't change settings or transmit");
    const btn = Array.from(mounted!.container.querySelectorAll('button')).find((b) => b.textContent === 'What gets sent?')!;
    await act(async () => {
      btn.click();
    });
    expect(text()).toContain('Never audio, IQ, passwords or your remote key');
  });

  it('shows the setup hint and disables input when the assistant is off', async () => {
    useAssistantStore.setState({ settings: { ...ON, enabled: false } });
    await act(async () => {
      mounted = render(createElement(AssistantDrawer));
    });
    expect(mounted!.container.querySelector('[data-testid="assistant-setup"]')).not.toBeNull();
    expect((mounted!.container.querySelector('input') as HTMLInputElement).disabled).toBe(true);
  });

  it('renders an answer with the lookups it made and the manual sections it used', async () => {
    useAssistantStore.setState({
      messages: [
        { role: 'user', text: 'PS?' },
        {
          role: 'assistant',
          text: 'Feedback is too hot (214).',
          trace: [{ tool: 'read', summary: 'run_diagnostic(ps-not-working)' }],
          citations: ['PureSignal › Feedback level'],
        },
      ],
    });
    await act(async () => {
      mounted = render(createElement(AssistantDrawer));
    });
    expect(text()).toContain('run_diagnostic(ps-not-working)');
    expect(text()).toContain('PureSignal › Feedback level');
    expect(text()).toContain('Feedback is too hot');
  });

  it('never renders anything that looks like the key', async () => {
    await act(async () => {
      mounted = render(createElement(AssistantDrawer));
    });
    expect(text()).not.toMatch(/sk-|x-api-key/);
  });
});
