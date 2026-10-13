// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Assistant (phase 1, read-only) — client state. The radio does the work
// (/api/assistant/ask runs the model and the read-only tools server-side, so
// the API key never reaches a browser); this store holds the open drawer,
// the conversation and the settings DTO (which never carries the key).

import { create } from 'zustand';

export type AssistantTrace = { tool: string; summary: string };

export type AssistantMessage = {
  role: 'user' | 'assistant';
  text: string;
  trace?: AssistantTrace[];
  citations?: string[];
  error?: string;
};

export type AssistantSettingsDto = {
  enabled: boolean;
  provider: 'anthropic' | 'openai';
  baseUrl: string;
  model: string;
  hasApiKey: boolean;
  apiKeyHint: string | null;
};

export type AssistantSettingsUpdate = Partial<{
  enabled: boolean;
  provider: 'anthropic' | 'openai';
  baseUrl: string;
  model: string;
  apiKey: string;
  clearApiKey: boolean;
}>;

type AnswerDto = {
  text: string;
  trace: AssistantTrace[];
  citations: string[];
  error: string | null;
};

type State = {
  open: boolean;
  busy: boolean;
  messages: AssistantMessage[];
  settings: AssistantSettingsDto | null;
  manualSections: number;
  setOpen: (open: boolean) => void;
  toggle: () => void;
  clear: () => void;
  ask: (question: string) => Promise<void>;
  loadSettings: () => Promise<void>;
  saveSettings: (update: AssistantSettingsUpdate) => Promise<string | null>;
};

/** Prior turns the radio replays to the model: text only, last six. */
export function historyFor(messages: AssistantMessage[]): { role: string; text: string }[] {
  return messages
    .filter((m) => !m.error && m.text.trim().length > 0)
    .slice(-6)
    .map((m) => ({ role: m.role, text: m.text }));
}

export const useAssistantStore = create<State>((set, get) => ({
  open: false,
  busy: false,
  messages: [],
  settings: null,
  manualSections: 0,
  setOpen: (open) => {
    set({ open });
    if (open && !get().settings) void get().loadSettings();
  },
  toggle: () => get().setOpen(!get().open),
  clear: () => set({ messages: [] }),
  ask: async (question) => {
    const q = question.trim();
    if (!q || get().busy) return;
    const history = historyFor(get().messages);
    set((s) => ({ busy: true, messages: [...s.messages, { role: 'user', text: q }] }));
    let reply: AssistantMessage;
    try {
      const r = await fetch('/api/assistant/ask', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ question: q, history }),
      });
      if (!r.ok) {
        reply = { role: 'assistant', text: '', error: `The radio answered HTTP ${r.status}.` };
      } else {
        const a = (await r.json()) as AnswerDto;
        reply = {
          role: 'assistant',
          text: a.text ?? '',
          trace: a.trace ?? [],
          citations: a.citations ?? [],
          error: a.error ?? undefined,
        };
      }
    } catch {
      reply = { role: 'assistant', text: '', error: "Couldn't reach the radio's assistant." };
    }
    set((s) => ({ busy: false, messages: [...s.messages, reply] }));
  },
  loadSettings: async () => {
    try {
      const r = await fetch('/api/assistant/settings');
      if (!r.ok) return;
      const body = (await r.json()) as { settings: AssistantSettingsDto; manualSections: number };
      set({ settings: body.settings, manualSections: body.manualSections ?? 0 });
    } catch {
      /* stays null: the drawer shows the setup hint */
    }
  },
  saveSettings: async (update) => {
    try {
      const r = await fetch('/api/assistant/settings', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(update),
      });
      if (r.status === 403) return 'Assistant settings can only be changed at the radio, not over a remote session.';
      if (!r.ok) return `Saving failed (HTTP ${r.status}).`;
      set({ settings: (await r.json()) as AssistantSettingsDto });
      return null;
    } catch {
      return "Couldn't reach the radio.";
    }
  },
}));

/** Starter questions shown in an empty conversation. */
export const ASSISTANT_SUGGESTIONS: readonly string[] = [
  "I can't hear anything",
  'PureSignal won\u2019t converge',
  'What does AGC-T do?',
  'What do the display effects levels do?',
  'Draft a problem report for what\u2019s wrong',
];
