// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.
//
// REST + SSE client for the Zeus Digital plugin — the
// backend plugin that hosts the FT8/FT4/WSPR decoders, TX keyer and spotting
// uploaders after their extraction from core. Everything digital-mode the
// frontend used to reach at /api/ft8|/api/wspr|/api/spotting now lives under
// /api/plugins/{resolved digital plugin id} (EXCEPT /api/ft8/settings — the
// UI-shell per-mode prefs stay core). GET /status doubles as the liveness probe
// the mode-gate uses: 2xx means the plugin's routes are live (mapped at boot or
// on a mid-session install — the host publishes plugin routes dynamically); 404
// (not installed / activation failed) and 503 (shut down) both read as not-live.

import { isRemoteMode } from '../remote/remote-client';
import { getRemoteControlSender } from '../realtime/ws-client';
import { getServerBaseUrl } from '../serverUrl';
import { usePluginsStore } from '../plugins/state/plugins-store';
import type { PluginDto } from '../plugins/api/plugins';

export const DIGITAL_PLUGIN_ID = 'org.openhpsdr.digital';
export const LEGACY_DIGITAL_PLUGIN_ID = 'com.kb2uka.digital';
export const DIGITAL_PLUGIN_BASE = `/api/plugins/${DIGITAL_PLUGIN_ID}`;

export function resolveDigitalPluginId(
  installed: readonly Pick<PluginDto, 'id'>[] = usePluginsStore.getState().installed,
): string | null {
  if (installed.some((p) => p.id === DIGITAL_PLUGIN_ID)) return DIGITAL_PLUGIN_ID;
  if (installed.some((p) => p.id === LEGACY_DIGITAL_PLUGIN_ID)) return LEGACY_DIGITAL_PLUGIN_ID;
  return null;
}

export function digitalPluginId(): string {
  return resolveDigitalPluginId() ?? DIGITAL_PLUGIN_ID;
}

export function digitalPluginBase(): string {
  return `/api/plugins/${digitalPluginId()}`;
}

/**
 * Liveness probe for the mode gate: true ONLY on a 2xx from GET /status.
 * Not-installed / failed-activation (404) and shut-down-instance (503) are
 * both "not live". Network errors read as not-live too — the next probe
 * trigger (ws reconnect / install refresh) recovers.
 */
export async function probeDigitalPlugin(signal?: AbortSignal): Promise<boolean> {
  const base = digitalPluginBase();
  try {
    const res = await fetch(`${base}/status`, { signal });
    return res.ok;
  } catch {
    return false;
  }
}

/** Operator identity pushed to the plugin (spotting + TX message generation).
 *  The CORE owns identity (/api/operator); the plugin only ever receives it. */
export interface DigitalIdentity {
  call: string;
  grid: string;
}

export async function postDigitalIdentity(identity: DigitalIdentity, signal?: AbortSignal): Promise<void> {
  const base = digitalPluginBase();
  const res = await fetch(`${base}/config/identity`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(identity),
    signal,
  });
  if (!res.ok) throw new Error(`POST ${base}/config/identity → ${res.status}`);
}

/**
 * WSJT-X live-decode egress subset pushed to the plugin's WsjtxLiveEmitter.
 * Derived from the CORE WsjtxConfig (which stays the operator-facing source of
 * truth in Settings): `enabled` = enabled && sendLiveDecodes; `host` is the
 * unicast host OR the multicast group depending on `multicast`.
 */
export interface DigitalWsjtxLiveConfig {
  enabled: boolean;
  host: string;
  port: number;
  multicast: boolean;
  /** Operator-configured WSJT-X instance id — third-party loggers correlate the
   *  live stream with core's QSOLogged/LoggedADIF datagrams by this id. */
  instanceId: string;
  /** Multicast hop limit (1..255); ignored for unicast. */
  multicastTtl: number;
}

export async function postDigitalWsjtxLive(cfg: DigitalWsjtxLiveConfig, signal?: AbortSignal): Promise<void> {
  const base = digitalPluginBase();
  const res = await fetch(`${base}/config/wsjtx-live`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(cfg),
    signal,
  });
  if (!res.ok) throw new Error(`POST ${base}/config/wsjtx-live → ${res.status}`);
}

// ---------------------------------------------------------------------------
// SSE event stream — GET /events replaces the old 0x38/0x39/0x3A WS frames.
// Each named event's `data:` is the exact JSON payload of the corresponding
// frame, so the store ingest() logic is unchanged; only the transport moved.
// ---------------------------------------------------------------------------

export interface DigitalEventsHandlers {
  /** Fires on EVERY successful open, including EventSource auto-reconnects —
   *  the caller must re-hydrate (/ft8, /ft8/tx, /wspr) and re-push identity +
   *  config here, because SSE replays nothing that happened in a gap. */
  onOpen?: () => void;
  /** Connection-state edge (true on open, false on error/close). */
  onConnectionChange?: (connected: boolean) => void;
  onFt8Decode?: (json: string) => void;
  onWsprSpot?: (json: string) => void;
  onCwSkim?: (json: string) => void;
  onSstv?: (json: string) => void;
  onTxStatus?: (json: string) => void;
}

/** Recreate delay after a HARD EventSource failure (readyState CLOSED — e.g.
 *  the plugin route answered 404/503, which EventSource never retries). */
const SSE_RETRY_MS = 5_000;

/**
 * Open the plugin's SSE stream. EventSource retries transient drops natively;
 * a hard failure (CLOSED) is recreated on a timer so a server restart under an
 * open tab eventually reattaches without a reload. Returns a close function.
 */
export function openDigitalEvents(h: DigitalEventsHandlers): () => void {
  let es: EventSource | null = null;
  let retry: ReturnType<typeof setTimeout> | null = null;
  let closed = false;

  const connect = () => {
    if (closed) return;
    // Remote (WebRTC) sessions have no same-origin backend: on the static
    // Pages host this EventSource gets index.html (text/html), aborts, and
    // retries forever — the field console showed 58 aborted connections in
    // a couple of minutes. SSE cannot ride the fetch-based api-tunnel
    // (EventSource is not fetch), so in remote mode the digital plugin's
    // event stream simply stays closed; the handlers see a permanent
    // disconnected state, which is truthful.
    if (isRemoteMode()) {
      // Remote sessions bridge the stream over the WebRTC control channel
      // instead: we ask the host to open the plugin's SSE endpoint on our
      // behalf (loopback, host-side) and forward each frame verbatim as 0x40.
      // The host sends exactly the bytes EventSource would have delivered, so
      // the parsing below is shared.
      setRemoteDigitalHandlers(h);
      const ok = requestRemoteDigitalEvents(`${digitalPluginBase()}/events`);
      // Connection state is reported when the first bridged frame lands (or
      // stays false if the host never opens it) — see onRemoteDigitalFrame.
      if (!ok) h.onConnectionChange?.(false);
      return;
    }
    // Relative on web/desktop; Capacitor builds prefix the configured LAN base
    // (EventSource bypasses the fetch interceptor, so resolve it explicitly).
    es = new EventSource(`${getServerBaseUrl()}${digitalPluginBase()}/events`);
    es.onopen = () => {
      h.onConnectionChange?.(true);
      h.onOpen?.();
    };
    es.onerror = () => {
      h.onConnectionChange?.(false);
      if (es?.readyState === EventSource.CLOSED && !closed && retry == null) {
        retry = setTimeout(() => {
          retry = null;
          es?.close();
          connect();
        }, SSE_RETRY_MS);
      }
    };
    es.addEventListener('ft8decode', (ev) => h.onFt8Decode?.((ev as MessageEvent<string>).data));
    es.addEventListener('wsprspot', (ev) => h.onWsprSpot?.((ev as MessageEvent<string>).data));
    es.addEventListener('cwskim', (ev) => h.onCwSkim?.((ev as MessageEvent<string>).data));
    es.addEventListener('sstv', (ev) => h.onSstv?.((ev as MessageEvent<string>).data));
    es.addEventListener('txstatus', (ev) => h.onTxStatus?.((ev as MessageEvent<string>).data));
  };

  connect();

  return () => {
    closed = true;
    if (retry != null) clearTimeout(retry);
    es?.close();
    h.onConnectionChange?.(false);
  };
}


// ---------------------------------------------------------------------------
// Remote (WebRTC) digital event bridge — client half.
//
// EventSource cannot ride the fetch-based api tunnel, so on a remote session
// the host opens the plugin's SSE endpoint over its own loopback and forwards
// each complete SSE frame to us as a 0x40 control frame. We parse the same
// "event: <name>\ndata: <json>\n\n" text EventSource would have given us and
// hand it to the same handlers, so nothing downstream knows the difference.

let remoteHandlers: DigitalEventsHandlers | null = null;
let remoteStreamLive = false;

export function setRemoteDigitalHandlers(h: DigitalEventsHandlers | null): void {
  remoteHandlers = h;
  if (h === null) {
    remoteStreamLive = false;
    remotePendingPath = null;
    stopRemoteRetry();
  }
}

// The path we want bridged, remembered so the subscribe can be re-sent. The
// first attempt races the WebRTC control channel: openDigitalEvents fires
// when the plugin store decides the plugin is live, which can easily be
// BEFORE startRemoteClient installs the control sender (field: the banner
// never cleared, because a one-shot subscribe with no sender was dropped
// silently and nothing retried). It must also be re-sent after a reconnect,
// since the host's bridge died with the old session.
let remotePendingPath: string | null = null;
let remoteRetryTimer: ReturnType<typeof setInterval> | null = null;

function stopRemoteRetry(): void {
  if (remoteRetryTimer !== null) {
    clearInterval(remoteRetryTimer);
    remoteRetryTimer = null;
  }
}

/** Re-send the pending subscribe — called when a remote control channel comes
 *  up, so the request lands whichever way the race went. */
export function resubscribeRemoteDigitalEvents(): void {
  if (remotePendingPath === null) return;
  remoteStreamLive = false;
  requestRemoteDigitalEvents(remotePendingPath);
}

/** Ask the host to bridge the plugin's SSE stream. Returns false when no
 *  remote control channel is available to carry the request yet — in which
 *  case we keep trying until one appears. */
export function requestRemoteDigitalEvents(path: string): boolean {
  remotePendingPath = path;
  const sender = getRemoteControlSender();
  if (!sender) {
    if (remoteRetryTimer === null) {
      remoteRetryTimer = setInterval(() => {
        if (remotePendingPath === null) {
          stopRemoteRetry();
          return;
        }
        if (getRemoteControlSender()) {
          stopRemoteRetry();
          requestRemoteDigitalEvents(remotePendingPath);
        }
      }, 1000);
    }
    return false;
  }
  stopRemoteRetry();
  const pathBytes = new TextEncoder().encode(path);
  const buf = new Uint8Array(pathBytes.length + 2);
  buf[0] = 0x24; // MsgTypeDigitalEventsSubscribe
  buf[1] = 1;    // enable
  buf.set(pathBytes, 2);
  sender(buf.buffer);
  return true;
}

/** Feed one bridged 0x40 frame (payload only, 0x40 already stripped). */
export function onRemoteDigitalFrame(payload: ArrayBuffer): void {
  const h = remoteHandlers;
  if (!h) return;
  if (!remoteStreamLive) {
    // First frame through = the host's stream is genuinely open. Report the
    // connection and re-hydrate, exactly as es.onopen would have.
    remoteStreamLive = true;
    h.onConnectionChange?.(true);
    h.onOpen?.();
  }
  let text: string;
  try {
    text = new TextDecoder().decode(payload);
  } catch {
    return;
  }
  let event = '';
  let data = '';
  for (const line of text.split('\n')) {
    if (line.startsWith('event: ')) event = line.slice(7).trim();
    else if (line.startsWith('data: ')) data += line.slice(6);
  }
  if (!event || !data) return;
  switch (event) {
    case 'ft8decode':
      h.onFt8Decode?.(data);
      break;
    case 'txstatus':
      h.onTxStatus?.(data);
      break;
    case 'wsprspot':
      h.onWsprSpot?.(data);
      break;
    case 'cwskim':
      h.onCwSkim?.(data);
      break;
    case 'sstv':
      h.onSstv?.(data);
      break;
    default:
      break;
  }
}
