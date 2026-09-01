// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Unit tests for the read-write /api/* fetch shim (api-tunnel.ts):
//   - GET /api/x tunnels over the data channel and resolves the radio's reply
//   - POST/PUT/… tunnel with their body + content-type over the channel
//   - non-/api requests delegate to the original fetch untouched
//   - requests issued BEFORE connect queue and flush once setApiChannel() lands

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { installApiTunnel, setApiChannel, __resetApiTunnelForTests } from './api-tunnel';

/** A minimal fake RTCDataChannel that captures sends and lets the test reply. */
class FakeChannel {
  readyState: 'connecting' | 'open' | 'closed' = 'open';
  onmessage: ((ev: MessageEvent) => void) | null = null;
  onopen: (() => void) | null = null;
  onclose: (() => void) | null = null;
  sent: string[] = [];

  send(data: string): void {
    this.sent.push(data);
  }

  /** Simulate the radio replying to a request with the given id. */
  reply(body: { id: number; status: number; headers?: Record<string, string>; body?: string }): void {
    this.onmessage?.({ data: JSON.stringify(body) } as MessageEvent);
  }

  open(): void {
    this.readyState = 'open';
    this.onopen?.();
  }
}

function lastRequest(ch: FakeChannel): {
  id: number;
  method: string;
  path: string;
  body?: string;
  contentType?: string;
} {
  return JSON.parse(ch.sent[ch.sent.length - 1]!);
}

describe('api-tunnel fetch shim', () => {
  let originalFetch: typeof window.fetch;

  beforeEach(() => {
    originalFetch = vi.fn(async () => new Response('passthrough', { status: 200 })) as typeof window.fetch;
    window.fetch = originalFetch;
    installApiTunnel();
  });

  afterEach(() => {
    __resetApiTunnelForTests();
  });

  it('tunnels a same-origin /api GET and resolves the radio reply', async () => {
    const ch = new FakeChannel();
    setApiChannel(ch as unknown as RTCDataChannel);

    const respPromise = window.fetch('/api/state');
    // The request was sent over the channel, not the network.
    expect(ch.sent.length).toBe(1);
    const req = lastRequest(ch);
    expect(req.method).toBe('GET');
    expect(req.path).toBe('/api/state');
    expect(originalFetch).not.toHaveBeenCalled();

    ch.reply({
      id: req.id,
      status: 200,
      headers: { 'content-type': 'application/json' },
      body: '{"vfoA":14200000}',
    });

    const resp = await respPromise;
    expect(resp.status).toBe(200);
    expect(await resp.json()).toEqual({ vfoA: 14200000 });
  });

  it('preserves the query string in the tunnelled path', async () => {
    const ch = new FakeChannel();
    setApiChannel(ch as unknown as RTCDataChannel);

    const p = window.fetch('/api/filter/presets?mode=USB');
    const req = lastRequest(ch);
    expect(req.path).toBe('/api/filter/presets?mode=USB');
    ch.reply({ id: req.id, status: 200, body: '[]' });
    await p;
  });

  it('tunnels a POST with its body + content-type and resolves the reply', async () => {
    const ch = new FakeChannel();
    setApiChannel(ch as unknown as RTCDataChannel);

    const respPromise = window.fetch('/api/tx/mox', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ on: true }),
    });
    // extractBody awaits before the send (async body read); wait for the send.
    await vi.waitFor(() => expect(ch.sent.length).toBe(1));
    const req = lastRequest(ch);
    expect(req.method).toBe('POST');
    expect(req.path).toBe('/api/tx/mox');
    expect(req.body).toBe('{"on":true}');
    expect(req.contentType).toBe('application/json');
    expect(originalFetch).not.toHaveBeenCalled();

    ch.reply({ id: req.id, status: 200, body: '{"moxOn":true}' });
    const resp = await respPromise;
    expect(resp.status).toBe(200);
    expect(await resp.json()).toEqual({ moxOn: true });
  });

  it('tunnels a body carried on a Request object', async () => {
    const ch = new FakeChannel();
    setApiChannel(ch as unknown as RTCDataChannel);

    // Absolute same-origin URL: the test env's Request can't parse a relative
    // path (no document base), but the shim still resolves it to /api/radio/lo.
    const request = new Request(`${window.location.origin}/api/radio/lo`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ hz: 14200000 }),
    });
    const respPromise = window.fetch(request);
    await vi.waitFor(() => expect(ch.sent.length).toBe(1));
    const req = lastRequest(ch);
    expect(req.method).toBe('POST');
    expect(req.path).toBe('/api/radio/lo');
    expect(req.body).toBe('{"hz":14200000}');

    ch.reply({ id: req.id, status: 200, body: 'null' });
    await respPromise;
  });

  it('delegates non-/api requests to the original fetch', async () => {
    const ch = new FakeChannel();
    setApiChannel(ch as unknown as RTCDataChannel);

    const resp = await window.fetch('https://example.com/data.json');
    expect(originalFetch).toHaveBeenCalledTimes(1);
    expect(await resp.text()).toBe('passthrough');
    expect(ch.sent.length).toBe(0);
  });

  it('queues GETs issued before connect and flushes them once the channel lands', async () => {
    // No channel yet — this fires during app mount, before unlock.
    const respPromise = window.fetch('/api/capabilities');
    expect(originalFetch).not.toHaveBeenCalled();

    // Channel connects (open) — queued request flushes now.
    const ch = new FakeChannel();
    setApiChannel(ch as unknown as RTCDataChannel);
    expect(ch.sent.length).toBe(1);
    const req = lastRequest(ch);
    expect(req.path).toBe('/api/capabilities');

    ch.reply({ id: req.id, status: 200, body: '{"ok":true}' });
    const resp = await respPromise;
    expect(await resp.json()).toEqual({ ok: true });
  });

  it('flushes a queue once a connecting channel transitions to open', async () => {
    const respPromise = window.fetch('/api/state');

    const ch = new FakeChannel();
    ch.readyState = 'connecting';
    setApiChannel(ch as unknown as RTCDataChannel);
    expect(ch.sent.length).toBe(0); // still buffered — channel not open yet

    ch.open(); // onopen flush
    expect(ch.sent.length).toBe(1);
    const req = lastRequest(ch);
    ch.reply({ id: req.id, status: 200, body: 'null' });
    await respPromise;
  });

  it('replays an in-flight request on the next channel after a disconnect', async () => {
    // A dropped session no longer mass-rejects: the in-flight request spends
    // its one silent retry as a replay on the reconnected channel.
    const ch = new FakeChannel();
    setApiChannel(ch as unknown as RTCDataChannel);

    const p = window.fetch('/api/state');
    expect(ch.sent.length).toBe(1);
    const { id } = lastRequest(ch);

    setApiChannel(null); // disconnect — request survives, queued for replay

    const ch2 = new FakeChannel();
    setApiChannel(ch2 as unknown as RTCDataChannel);
    expect(ch2.sent.length).toBe(1);
    expect(lastRequest(ch2).id).toBe(id);

    ch2.reply({ id, status: 200, body: '{"ok":true}' });
    const res = await p;
    expect(res.status).toBe(200);
  });

  it('fails an in-flight request whose retry is also lost to a disconnect', async () => {
    const ch = new FakeChannel();
    setApiChannel(ch as unknown as RTCDataChannel);
    const p = window.fetch('/api/state');
    setApiChannel(null);            // first drop → queued for replay
    const ch2 = new FakeChannel();
    setApiChannel(ch2 as unknown as RTCDataChannel);   // replay dispatched
    expect(ch2.sent.length).toBe(1);
    setApiChannel(null);            // second drop: the retry is spent
    await expect(p).rejects.toThrow(/closed/i);
  });

  it('does not expire a queued request on the dispatch deadline (boot race)', async () => {
    // The fresh-page-load race: RPCs fired before the session is up used to
    // arm their 15 s deadline at enqueue and die in the queue. The dispatch
    // deadline now runs from SEND; queued time counts only against the hard cap.
    vi.useFakeTimers();
    try {
      const p = window.fetch('/api/state'); // no channel yet → queued
      vi.advanceTimersByTime(20_000);       // past REQUEST_TIMEOUT_MS

      const ch = new FakeChannel();
      setApiChannel(ch as unknown as RTCDataChannel); // session up → flush
      expect(ch.sent.length).toBe(1);
      const { id } = lastRequest(ch);
      ch.reply({ id, status: 200, body: '{"late":"boot"}' });
      const res = await p;
      expect(res.status).toBe(200);
    } finally {
      vi.useRealTimers();
    }
  });

  it('silently retries once on a dispatch timeout, then fails on the second', async () => {
    vi.useFakeTimers();
    try {
      const ch = new FakeChannel();
      setApiChannel(ch as unknown as RTCDataChannel);
      const p = window.fetch('/api/state');
      expect(ch.sent.length).toBe(1);

      vi.advanceTimersByTime(15_001);       // first deadline → silent retry
      expect(ch.sent.length).toBe(2);
      expect(lastRequest(ch).path).toBe('/api/state');

      vi.advanceTimersByTime(15_001);       // second deadline → final
      await expect(p).rejects.toThrow(/timed out/i);
    } finally {
      vi.useRealTimers();
    }
  });

  it('enforces the hard cap when the session never arrives', async () => {
    vi.useFakeTimers();
    try {
      const p = window.fetch('/api/state'); // queued forever — no channel
      vi.advanceTimersByTime(45_001);
      await expect(p).rejects.toThrow(/waiting for the session/i);
    } finally {
      vi.useRealTimers();
    }
  });
});
