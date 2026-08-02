#!/usr/bin/env node
// SPDX-License-Identifier: GPL-2.0-or-later
//
// zeus-remote-relay — self-hostable signaling rendezvous for Zeus remote
// operation. Speaks exactly the wire protocol of the public broker's /signal
// path, so both existing ends work unmodified:
//
//   radio  : ZEUS_REMOTE_BROKER_URL=wss://relay:8787/signal?role=host
//            ZEUS_REMOTE_CALLSIGN=VU2XYZ            (QRZ-free identity)
//   browser: open the client with ?broker=https://relay:8787
//
// Security model (ADR-0007/0008): the relay is UNTRUSTED plumbing. It only
// ferries SDP blobs; the radio's SPAKE2+ password gate is the sole
// authenticator and every session is deny-by-default until the operator's
// password proves out end-to-end. Consequently the relay needs no accounts —
// an optional shared token (RELAY_TOKEN env → &k=<token> on both roles)
// merely keeps random internet scanners from occupying callsign slots.
//
// Protocol (all JSON text frames over WebSocket):
//   host   → connects /signal?role=host   (callsign from X-QRZ-Callsign
//            header or &callsign=). Receives {t:"offer",sdp}; replies
//            {t:"answer",sdp}. One live socket per callsign (new replaces old).
//   client → connects /signal?role=client&callsign=CALL, sends
//            {t:"offer",sdp}, awaits {t:"answer",sdp} or {t:"offline"}.
//   POST /turn → {iceServers:[...]} from TURN_JSON env, else 404 (client
//            falls back to public STUN; fine for most home NATs).
//   --ui <dir> → also serve a static directory (a zeus-web build), so the
//            relay origin doubles as the remote client page.
//
// Node >= 18, single dependency: `npm i ws`.

import http from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { join, normalize, extname } from 'node:path';
import { WebSocketServer } from 'ws';

const PORT = Number(process.env.PORT || 8787);
const TOKEN = process.env.RELAY_TOKEN || '';
const TURN_JSON = process.env.TURN_JSON || '';
const uiIdx = process.argv.indexOf('--ui');
const UI_DIR = uiIdx > -1 ? process.argv[uiIdx + 1] : '';

/** callsign -> { ws, queue: [{client, timer}] , busy: client|null } */
const hosts = new Map();

const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css',
  '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml',
  '.wasm': 'application/wasm', '.webmanifest': 'application/manifest+json' };

const server = http.createServer(async (req, res) => {
  if (req.method === 'POST' && req.url === '/turn') {
    if (TURN_JSON) { res.writeHead(200, { 'content-type': 'application/json' }); return res.end(TURN_JSON); }
    res.writeHead(404); return res.end();
  }
  if (UI_DIR && req.method === 'GET') {
    try {
      let p = normalize(decodeURIComponent((req.url || '/').split('?')[0])).replace(/^([.\\/])+/, '');
      if (p === '' || p === '/') p = 'index.html';
      let file = join(UI_DIR, p);
      try { await stat(file); } catch { file = join(UI_DIR, 'index.html'); } // SPA fallback
      const body = await readFile(file);
      res.writeHead(200, { 'content-type': MIME[extname(file)] || 'application/octet-stream' });
      return res.end(body);
    } catch { res.writeHead(404); return res.end(); }
  }
  res.writeHead(200, { 'content-type': 'text/plain' });
  res.end('zeus-remote-relay: ok\n');
});

const wss = new WebSocketServer({ noServer: true });

server.on('upgrade', (req, socket, head) => {
  const url = new URL(req.url, 'http://x');
  if (url.pathname !== '/signal') { socket.destroy(); return; }
  if (TOKEN && url.searchParams.get('k') !== TOKEN) { socket.destroy(); return; }
  wss.handleUpgrade(req, socket, head, (ws) => onSignal(ws, req, url));
});

function onSignal(ws, req, url) {
  const role = url.searchParams.get('role');
  const call = (url.searchParams.get('callsign')
    || req.headers['x-qrz-callsign'] || '').toString().toUpperCase().trim();

  if (role === 'host') {
    if (!call) return ws.close(4000, 'callsign required');
    const prev = hosts.get(call);
    if (prev) { try { prev.ws.close(4001, 'replaced'); } catch { /* */ } }
    const entry = { ws, queue: [], busy: null };
    hosts.set(call, entry);
    log(`host online: ${call}`);
    ws.on('message', (data) => {
      // host speaks only answers (and support replies) — route to the waiting client
      const c = entry.busy;
      entry.busy = null;
      if (c && c.readyState === 1) { try { c.send(data.toString()); } catch { /* */ } try { c.close(); } catch { /* */ } }
      pump(call, entry);
    });
    ws.on('close', () => {
      if (hosts.get(call) === entry) hosts.delete(call);
      for (const q of entry.queue) { try { q.client.send('{"t":"offline"}'); q.client.close(); } catch { /* */ } clearTimeout(q.timer); }
      log(`host offline: ${call}`);
    });
    return;
  }

  if (role === 'client') {
    const entry = hosts.get(call);
    if (!entry || entry.ws.readyState !== 1) {
      try { ws.send('{"t":"offline"}'); } catch { /* */ }
      return ws.close();
    }
    ws.once('message', (data) => {
      let msg; try { msg = JSON.parse(data.toString()); } catch { return ws.close(); }
      if (msg?.t !== 'offer' || typeof msg.sdp !== 'string' || msg.sdp.length > 256 * 1024) return ws.close();
      const timer = setTimeout(() => {
        // host never answered — release the slot, tell the client
        if (entry.busy === ws) entry.busy = null;
        entry.queue = entry.queue.filter((q) => q.client !== ws);
        try { ws.send('{"t":"offline"}'); ws.close(); } catch { /* */ }
        pump(call, entry);
      }, 25_000);
      entry.queue.push({ client: ws, offer: JSON.stringify({ t: 'offer', sdp: msg.sdp }), timer });
      pump(call, entry);
    });
    ws.on('close', () => { entry.queue = entry.queue.filter((q) => q.client !== ws); });
    return;
  }
  ws.close(4002, 'role required');
}

/** Serialize offers per host: one in flight; the host's next message is the
 *  answer for the in-flight client (mirrors the radio's sequential
 *  HandleSignalAsync — no correlation ids needed). */
function pump(call, entry) {
  if (entry.busy || entry.queue.length === 0 || entry.ws.readyState !== 1) return;
  const next = entry.queue.shift();
  clearTimeout(next.timer);
  next.timer = setTimeout(() => {
    if (entry.busy === next.client) entry.busy = null;
    try { next.client.send('{"t":"offline"}'); next.client.close(); } catch { /* */ }
    pump(call, entry);
  }, 25_000);
  entry.busy = next.client;
  try { entry.ws.send(next.offer); } catch { entry.busy = null; }
}

function log(m) { console.log(`[relay ${new Date().toISOString()}] ${m}`); }

server.listen(PORT, () => log(`listening on :${PORT}${UI_DIR ? `, serving UI from ${UI_DIR}` : ''}${TOKEN ? ', token required' : ''}${TURN_JSON ? ', TURN configured' : ''}`));
