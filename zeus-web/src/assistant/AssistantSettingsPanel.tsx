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
// Settings → ASSISTANT. The key is write-only: this panel can set or clear
// it and shows a 4-character hint, but never reads it back.

import { useEffect, useState, type CSSProperties } from 'react';
import { useAssistantStore, type AssistantSettingsDto } from './assistant-store';

export function AssistantSettingsPanel() {
  const settings = useAssistantStore((s) => s.settings);
  const manualSections = useAssistantStore((s) => s.manualSections);
  const load = useAssistantStore((s) => s.loadSettings);
  const save = useAssistantStore((s) => s.saveSettings);
  const setOpen = useAssistantStore((s) => s.setOpen);
  const [provider, setProvider] = useState<AssistantSettingsDto['provider']>('anthropic');
  const [baseUrl, setBaseUrl] = useState('');
  const [model, setModel] = useState('');
  const [key, setKey] = useState('');
  const [msg, setMsg] = useState<string | null>(null);

  useEffect(() => {
    void load();
  }, [load]);
  useEffect(() => {
    if (!settings) return;
    setProvider(settings.provider);
    setBaseUrl(settings.baseUrl);
    setModel(settings.model);
  }, [settings]);

  const apply = async (update: Parameters<typeof save>[0], ok: string) => {
    const err = await save(update);
    setMsg(err ?? ok);
    if (!err) setKey('');
  };

  if (!settings) return <div style={hint}>Loading assistant settings…</div>;

  return (
    <div style={wrap} data-testid="assistant-settings">
      <div style={label}>ASSISTANT (READ-ONLY)</div>
      <p style={hint}>
        Ask questions about the radio and get help diagnosing problems. The assistant answers from the operator
        manual{manualSections > 0 ? ` (${manualSections} sections on this radio)` : ''} and the radio&apos;s
        current state. It can only read: it cannot change a setting or transmit. Off until you turn it on.
      </p>

      <label style={row}>
        <input
          type="checkbox"
          checked={settings.enabled}
          onChange={(e) => void apply({ enabled: e.target.checked }, e.target.checked ? 'Assistant on.' : 'Assistant off.')}
        />
        <span>Enable the assistant</span>
      </label>

      <label style={field}>
        <span style={label}>MODEL PROVIDER</span>
        <select
          value={provider}
          onChange={(e) => {
            const p = e.target.value as AssistantSettingsDto['provider'];
            setProvider(p);
            void apply({ provider: p }, 'Provider changed — address and model reset to its defaults.');
          }}
          style={inputS}
        >
          <option value="anthropic">Anthropic (your API key)</option>
          <option value="openai">OpenAI-compatible server (e.g. a model server on your network)</option>
        </select>
      </label>

      <label style={field}>
        <span style={label}>ADDRESS</span>
        <input value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} style={inputS} spellCheck={false} />
      </label>
      <label style={field}>
        <span style={label}>MODEL</span>
        <input value={model} onChange={(e) => setModel(e.target.value)} style={inputS} spellCheck={false} />
      </label>
      <label style={field}>
        <span style={label}>API KEY {provider === 'openai' ? '(optional)' : ''}</span>
        <input
          type="password"
          value={key}
          autoComplete="off"
          placeholder={settings.hasApiKey ? `Saved (${settings.apiKeyHint ?? 'set'}) — type to replace` : 'Not set'}
          onChange={(e) => setKey(e.target.value)}
          style={inputS}
        />
      </label>

      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <button
          type="button"
          className="btn sm active"
          style={btn}
          onClick={() => void apply({ baseUrl, model, ...(key.trim() ? { apiKey: key.trim() } : {}) }, 'Saved.')}
        >
          SAVE
        </button>
        {settings.hasApiKey && (
          <button type="button" className="btn sm" style={btn} onClick={() => void apply({ clearApiKey: true }, 'Key removed.')}>
            REMOVE KEY
          </button>
        )}
        <button type="button" className="btn sm" style={btn} onClick={() => setOpen(true)}>
          OPEN ASSISTANT
        </button>
      </div>
      {msg && (
        <div style={hint} role="status">
          {msg}
        </div>
      )}
      <p style={hint}>
        What is sent to the model: your question, a snapshot of the radio&apos;s state, and redacted log lines when
        diagnosing. Never audio, IQ, passwords or your remote key. The key is stored on the radio only and is never
        sent to a browser. Assistant settings can be changed only at the radio, not over a remote session.
      </p>
    </div>
  );
}

const wrap: CSSProperties = { display: 'flex', flexDirection: 'column', gap: 12, maxWidth: 620, padding: '6px 0' };
const label: CSSProperties = { fontSize: 10, letterSpacing: '0.08em', color: 'var(--fg-3, #8a94a3)' };
const hint: CSSProperties = { fontSize: 12, lineHeight: 1.45, color: 'var(--fg-3, #8a94a3)', margin: 0 };
const row: CSSProperties = { display: 'flex', alignItems: 'center', gap: 8, minHeight: 36 };
const field: CSSProperties = { display: 'flex', flexDirection: 'column', gap: 4 };
const inputS: CSSProperties = { minHeight: 36, fontSize: 14 };
const btn: CSSProperties = { minHeight: 36 };
