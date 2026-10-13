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
// The ASK drawer: a half-height sheet from the bottom of the screen. Each
// answer shows the read-only lookups the assistant made (its tool trace) and
// the manual sections it used; the header says, always, that it can only
// read. Mounted once at the app root; opened by the ASK deck key or Settings.

import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { ASSISTANT_SUGGESTIONS, useAssistantStore, type AssistantMessage } from './assistant-store';

export function AssistantDrawer() {
  const open = useAssistantStore((s) => s.open);
  const busy = useAssistantStore((s) => s.busy);
  const messages = useAssistantStore((s) => s.messages);
  const settings = useAssistantStore((s) => s.settings);
  const setOpen = useAssistantStore((s) => s.setOpen);
  const ask = useAssistantStore((s) => s.ask);
  const clear = useAssistantStore((s) => s.clear);
  const [draft, setDraft] = useState('');
  const [showPrivacy, setShowPrivacy] = useState(false);
  const listRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const el = listRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages.length, busy]);

  if (!open) return null;

  const ready = settings?.enabled === true && (settings.provider === 'openai' || settings.hasApiKey);
  const send = (q: string) => {
    if (!q.trim() || busy) return;
    void ask(q);
    setDraft('');
  };

  return (
    <div role="dialog" aria-label="Ask ANAN Core" data-testid="assistant-drawer" style={sheet}>
      <div style={header}>
        <span style={{ color: '#cfe8f8', fontWeight: 600, letterSpacing: '0.06em' }}>ASK ANAN CORE</span>
        <span style={chip}>Read-only</span>
        <span style={{ flex: 1 }} />
        <button type="button" style={linkBtn} onClick={() => setShowPrivacy((v) => !v)}>
          What gets sent?
        </button>
        {messages.length > 0 && (
          <button type="button" style={linkBtn} onClick={clear}>
            Clear
          </button>
        )}
        <button type="button" aria-label="Close" style={closeBtn} onClick={() => setOpen(false)}>
          ×
        </button>
      </div>
      {showPrivacy && (
        <div style={privacy} data-testid="assistant-privacy">
          Sent to the model you configured
          {settings ? ` (${settings.provider === 'anthropic' ? 'Anthropic' : 'OpenAI-compatible'} · ${settings.model})` : ''}:
          your question, a snapshot of the radio&apos;s state, and redacted log lines when diagnosing. Never
          audio, IQ, passwords or your remote key. The assistant can only read — it cannot change a setting or
          transmit.
        </div>
      )}
      <div ref={listRef} style={list}>
        {!ready && (
          <div style={bubbleA} data-testid="assistant-setup">
            The assistant is off. An operator can turn it on at the radio in <b>Settings → Assistant</b> by
            choosing a model: your own Anthropic API key, or a model server on your network.
          </div>
        )}
        {ready && messages.length === 0 && (
          <div style={bubbleA}>
            I can explain any part of the radio from the manual, and look at its current state to work out
            what&apos;s wrong. I <b>can&apos;t change settings or transmit</b> — I&apos;ll tell you exactly what to tap.
          </div>
        )}
        {messages.map((m, i) => (
          <Message key={i} m={m} />
        ))}
        {busy && (
          <div style={{ ...bubbleA, color: '#9fb4c8' }} data-testid="assistant-busy">
            Looking…
          </div>
        )}
      </div>
      {ready && messages.length === 0 && (
        <div style={suggRow}>
          {ASSISTANT_SUGGESTIONS.map((q) => (
            <button key={q} type="button" style={sugg} onClick={() => send(q)}>
              {q}
            </button>
          ))}
        </div>
      )}
      <div style={inputRow}>
        <input
          value={draft}
          disabled={!ready}
          placeholder={ready ? 'Ask about the radio…' : 'Assistant is off'}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') send(draft);
          }}
          style={input}
          aria-label="Question"
        />
        <button type="button" disabled={!ready || busy || !draft.trim()} style={askBtn} onClick={() => send(draft)}>
          {busy ? '…' : 'Ask'}
        </button>
      </div>
    </div>
  );
}

function Message({ m }: { m: AssistantMessage }) {
  if (m.role === 'user') return <div style={bubbleU}>{m.text}</div>;
  return (
    <div style={bubbleA} data-testid="assistant-answer">
      {m.trace && m.trace.length > 0 && (
        <div style={{ marginBottom: 6 }}>
          {m.trace.map((t, i) => (
            <div key={i} style={traceRow}>
              <span style={{ color: '#7fd6a6' }}>✓</span> {t.summary}
            </div>
          ))}
        </div>
      )}
      {m.error ? (
        <div style={{ color: '#f0c08a' }}>{m.error}</div>
      ) : (
        <div style={{ whiteSpace: 'pre-wrap' }}>{m.text}</div>
      )}
      {m.citations && m.citations.length > 0 && (
        <div style={{ marginTop: 6, display: 'flex', flexWrap: 'wrap', gap: 6 }}>
          {m.citations.map((c) => (
            <span key={c} style={cite}>
              📖 {c}
            </span>
          ))}
        </div>
      )}
      {!m.error && m.text && (
        <button
          type="button"
          style={{ ...linkBtn, marginTop: 6, paddingLeft: 0 }}
          onClick={() => void navigator.clipboard?.writeText(m.text).catch(() => {})}
        >
          Copy
        </button>
      )}
    </div>
  );
}

const sheet: CSSProperties = {
  position: 'fixed',
  left: 0,
  right: 0,
  bottom: 0,
  height: 'min(62vh, 520px)',
  zIndex: 1100,
  background: 'rgba(10,20,31,0.98)',
  borderTop: '1px solid #2a4a66',
  display: 'flex',
  flexDirection: 'column',
  color: '#d8e4f0',
  paddingBottom: 'env(safe-area-inset-bottom, 0px)',
  boxShadow: '0 -12px 32px rgba(0,0,0,0.45)',
};
const header: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 10,
  padding: '8px 12px',
  borderBottom: '1px solid #1b2c3e',
  fontSize: 12,
};
const chip: CSSProperties = { fontSize: 11, padding: '2px 8px', borderRadius: 6, background: '#12324a', color: '#9fd0f0' };
const linkBtn: CSSProperties = {
  background: 'transparent',
  border: 'none',
  color: '#9fb4c8',
  fontSize: 12,
  textDecoration: 'underline dotted',
  cursor: 'pointer',
  padding: '4px 6px',
};
const closeBtn: CSSProperties = {
  background: 'transparent',
  border: 'none',
  color: '#cfe8f8',
  fontSize: 22,
  minWidth: 44,
  minHeight: 44,
  cursor: 'pointer',
};
const privacy: CSSProperties = {
  fontSize: 12,
  lineHeight: 1.45,
  color: '#9fb4c8',
  padding: '8px 12px',
  background: '#0d1926',
  borderBottom: '1px solid #1b2c3e',
};
const list: CSSProperties = {
  flex: 1,
  overflowY: 'auto',
  padding: '10px 12px',
  display: 'flex',
  flexDirection: 'column',
  gap: 10,
};
const bubbleU: CSSProperties = {
  alignSelf: 'flex-end',
  background: '#1d3b55',
  color: '#eaf3fb',
  borderRadius: '12px 12px 2px 12px',
  padding: '8px 12px',
  maxWidth: '75%',
  fontSize: 15,
};
const bubbleA: CSSProperties = {
  alignSelf: 'flex-start',
  background: '#122233',
  border: '1px solid #22384d',
  borderRadius: '12px 12px 12px 2px',
  padding: '10px 12px',
  maxWidth: '88%',
  fontSize: 15,
  lineHeight: 1.5,
};
const traceRow: CSSProperties = { fontFamily: 'var(--font-mono, ui-monospace, monospace)', fontSize: 12, color: '#7fb2d6' };
const cite: CSSProperties = {
  fontSize: 12,
  color: '#9fd0f0',
  border: '1px solid #2b5878',
  borderRadius: 6,
  padding: '2px 8px',
};
const suggRow: CSSProperties = { display: 'flex', gap: 8, flexWrap: 'wrap', padding: '0 12px 8px' };
const sugg: CSSProperties = {
  fontSize: 13,
  border: '1px solid #2b5878',
  color: '#cfe8f8',
  background: 'transparent',
  borderRadius: 999,
  padding: '8px 12px',
  minHeight: 40,
  cursor: 'pointer',
};
const inputRow: CSSProperties = { display: 'flex', gap: 8, padding: '8px 12px', borderTop: '1px solid #1b2c3e' };
const input: CSSProperties = {
  flex: 1,
  minHeight: 44,
  background: '#0b1622',
  border: '1px solid #2a4258',
  color: '#eaf3fb',
  borderRadius: 8,
  padding: '0 12px',
  fontSize: 15,
};
const askBtn: CSSProperties = {
  minWidth: 72,
  minHeight: 44,
  background: '#4aa3df',
  color: '#06121d',
  border: 'none',
  borderRadius: 8,
  fontWeight: 600,
  cursor: 'pointer',
};
