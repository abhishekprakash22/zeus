// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Remote-access bootstrap. When the SPA is opened at `…/?remote=<CALLSIGN>` it
// connects to the operator's radio over WebRTC through the Cloudflare broker
// instead of the local websocket, then feeds the unlocked binary radio frames
// through the exact same dispatch path the local /ws client uses
// (dispatchServerFrame) — so panadapter / waterfall / meters / audio render
// identically, just sourced over WebRTC.
//
// Scope: full native control + voice TX. RX display + audio + meters stream over
// the frames channel; the read-write `/api/*` tunnel (api-tunnel.ts) carries the
// SPA's control REST to the radio's loopback Kestrel — VFO/mode/band/filter/AGC/
// drive/MOX/TUN, exactly as the desktop app does; and a MOX-gated sendonly Opus
// audio track carries the operator's mic for voice TX (see connect.ts /
// RemoteMicAudioPipeline on the radio). The server gates the burn-zone
// (PureSignal) + secrets and dead-man un-keys a dropped session. Deny-by-default
// holds: nothing flows until connectViaBroker's SPAKE2+ password handshake unlocks.

import { connectViaBroker, type RemoteConnection } from './connect';
import { installApiTunnel, setApiChannel } from './api-tunnel';
import { useTxStore } from '../state/tx-store';
import {
  dispatchServerFrame,
  sendAudioStreamRequest,
  sendDisplayStreamRequest,
  setRemoteControlSender,
} from '../realtime/ws-client';

/** Parse `?remote=<CALLSIGN>` from the current URL; '' / absent → not remote. */
export function getRemoteCallsign(): string | null {
  try {
    const cs = new URLSearchParams(window.location.search).get('remote');
    const trimmed = cs?.trim();
    return trimmed ? trimmed.toUpperCase() : null;
  } catch {
    return null;
  }
}

/** True when the SPA should run as a remote (WebRTC) monitor rather than a local client. */
export function isRemoteMode(): boolean {
  return getRemoteCallsign() !== null;
}

// Install the read-write /api/* fetch shim at module load — BEFORE the app's
// mount effects fire their `/api/state` etc. requests. In remote mode there is
// no same-origin backend, so those requests must tunnel; the shim queues them
// until the session unlocks and setApiChannel() flushes the queue. No-op outside
// remote mode (the local /ws client uses the real same-origin backend).
if (isRemoteMode()) {
  installApiTunnel();
}

// -- Link telemetry + TX lease -----------------------------------------------

// Display frames that actually arrived this second. RTT/jitter/loss come from
// pc.getStats(); this is the one number getStats can't give — the host paces
// and drops spectrum frames under SCTP backpressure (RemoteWebRtcSession
// .TrySendFrame), so the arriving rate is the most honest "is the display
// about to stutter" signal there is.
let displayFramesSinceRead = 0;
export function takeDisplayFrameCount(): number {
  const n = displayFramesSinceRead;
  displayFramesSinceRead = 0;
  return n;
}

// TX lease keepalive (host: RemoteWebRtcSession.LeaseTick). One byte, 0x23,
// every 250 ms on the control channel for the life of the session. If the
// pulses stop while the radio is keyed, the host un-keys within 1.5 s — no
// waiting for ICE to notice the peer is gone. Driven from a Web Worker: page
// timers in a hidden/minimized tab are throttled to 1 Hz, then ~1/min after
// five minutes (Chrome intensive throttling), which would lapse the lease on
// a perfectly healthy session. Worker timers are exempt. Falls back to a page
// setInterval if a blob Worker is refused (CSP); the 1.5 s host deadline
// still tolerates the 1 Hz foreground-throttle case.
const TX_LEASE_KEEPALIVE = 0x23;
const TX_LEASE_PERIOD_MS = 250;

function startTxLeasePulse(control: RTCDataChannel): () => void {
  const pulse = new Uint8Array([TX_LEASE_KEEPALIVE]);
  const send = () => {
    if (control.readyState !== 'open') return;
    try { control.send(pulse); } catch { /* closing — watchdogs handle it */ }
  };
  let worker: Worker | null = null;
  try {
    const src = `setInterval(() => postMessage(0), ${TX_LEASE_PERIOD_MS});`;
    const url = URL.createObjectURL(new Blob([src], { type: 'text/javascript' }));
    worker = new Worker(url);
    URL.revokeObjectURL(url);
    worker.onmessage = send;
    return () => { worker?.terminate(); worker = null; };
  } catch (e) {
    console.warn('[remote] lease pulse worker unavailable, using page timer:', e);
    const t = setInterval(send, TX_LEASE_PERIOD_MS);
    return () => clearInterval(t);
  }
}

// Hidden <audio> sink for the radio's RX audio when it arrives on the WebRTC
// media track (Opus-RX host path). The browser owns decode + jitter buffer +
// PLC; we just attach the stream and let it play. One element, reused across
// reconnects.
let rxAudioEl: HTMLAudioElement | null = null;
let rxAudioStream: MediaStream | null = null;
let rxLevelTimer: ReturnType<typeof setInterval> | null = null;
let rxAudioMuted = false;
const rxMuteListeners = new Set<(muted: boolean) => void>();

// Operator mute for the remote audible path (field: 'Mute does nothing').
// In a WebRTC session RX audio plays through the hidden element above — the
// /ws audio client the AudioToggle drives elsewhere doesn't exist here, so
// the toggle calls this instead. Sticky across reconnects (module state
// survives; playRemoteRxAudioTrack re-applies it to the fresh element).
export function setRemoteRxAudioMuted(muted: boolean): void {
  rxAudioMuted = muted;
  if (rxAudioEl) rxAudioEl.muted = muted;
  for (const fn of rxMuteListeners) fn(muted);
}
export function isRemoteRxAudioMuted(): boolean { return rxAudioMuted; }
export function subscribeRemoteRxAudioMuted(fn: (muted: boolean) => void): () => void {
  rxMuteListeners.add(fn);
  return () => rxMuteListeners.delete(fn);
}

// Field-debug tap: the page measures its OWN received audio, so a silent-ear
// report can be split into "track carries silence" vs "render path eats it"
// with one console read instead of photographing webrtc-internals. Enable
// with localStorage.setItem('zeus.remote.audiodebug','1') + reload; a line
// logs each second with the decoded stream's RMS in dBFS. The AnalyserNode
// is a pure tap (never connected onward) — it cannot affect playback.
function startRxLevelTap(stream: MediaStream): void {
  try {
    if (localStorage.getItem('zeus.remote.audiodebug') !== '1') return;
    if (rxLevelTimer) { clearInterval(rxLevelTimer); rxLevelTimer = null; }
    const ctx = new AudioContext();
    const src = ctx.createMediaStreamSource(stream);
    const an = ctx.createAnalyser();
    an.fftSize = 2048;
    src.connect(an);
    const buf = new Float32Array(an.fftSize);
    rxLevelTimer = setInterval(() => {
      an.getFloatTimeDomainData(buf as Float32Array<ArrayBuffer>);
      let sum = 0;
      for (let i = 0; i < buf.length; i++) { const v = buf[i] ?? 0; sum += v * v; }
      const rms = Math.sqrt(sum / buf.length);
      const db = rms > 0 ? (20 * Math.log10(rms)).toFixed(1) : '-inf';
      const el = rxAudioEl;
      console.log(
        `[rx-audio] rms=${db} dBFS | paused=${el?.paused} muted=${el?.muted} vol=${el?.volume} readyState=${el?.readyState}`,
      );
    }, 1000);
  } catch (e) {
    console.warn('[remote] rx level tap failed:', e);
  }
}

function playRemoteRxAudioTrack(stream: MediaStream): void {
  if (typeof document === 'undefined') return;
  rxAudioStream = stream;
  if (!rxAudioEl) {
    rxAudioEl = document.createElement('audio');
    rxAudioEl.muted = rxAudioMuted;
    rxAudioEl.autoplay = true;
    rxAudioEl.id = 'zeus-remote-rx-audio';
    // In the DOM (hidden) rather than detached: reachable by console probes,
    // visible to devtools, and immune to any detached-element edge cases.
    rxAudioEl.style.display = 'none';
    document.body.appendChild(rxAudioEl);
  }
  rxAudioEl.srcObject = stream;
  void rxAudioEl.play().catch((e) => {
    console.warn('[remote] RX audio track autoplay blocked:', e);
  });
  startRxLevelTap(stream);
  // Live repair lever for field debugging: rebuilds the player from scratch
  // against the current stream. If silent audio snaps on after a kick, the
  // element/render path was wedged; if not, the stream itself is silent.
  (window as unknown as Record<string, unknown>).__zeusRxAudioKick = () => {
    try { rxAudioEl?.remove(); } catch { /* detached */ }
    rxAudioEl = null;
    if (rxAudioStream) playRemoteRxAudioTrack(rxAudioStream);
    return 'kicked';
  };
  (window as unknown as Record<string, unknown>).__zeusRxAudio = rxAudioEl;
}

function stopRemoteRxAudioTrack(): void {
  if (!rxAudioEl) return;
  rxAudioEl.pause();
  rxAudioEl.srcObject = null;
}

/**
 * Connect to the operator's radio via the broker, unlock with the supplied
 * password, then route the unlocked frame stream into the stores and request
 * the RX display + audio streams over the control DataChannel.
 *
 * Resolves with the live connection once unlocked; rejects with a human-readable
 * Error (incorrect password, radio offline, broker unreachable) the gate UI can
 * surface for retry. No frame flows before this resolves.
 */
export async function startRemoteClient(
  callsign: string,
  password: string,
): Promise<RemoteConnection> {
  const conn = await connectViaBroker({
    callsign,
    password,
    onFrame: (data) => {
      if (data.byteLength >= 1 && new Uint8Array(data, 0, 1)[0] === 0x01) displayFramesSinceRead++;
      dispatchServerFrame(data);
    },
  });

  // TX lease: start pulsing the moment we're unlocked, before any key-down
  // can happen. The host arms its watchdog on the first pulse.
  const stopLeasePulse = startTxLeasePulse(conn.control);

  // Hand the read-write API tunnel its live "api" channel so queued + future
  // same-origin `/api/*` requests (reads AND control writes) flow to the radio's
  // loopback Kestrel. The session is unlocked by the time connectViaBroker
  // resolves (deny-by-default holds).
  setApiChannel(conn.api);

  // State-of-the-art RX audio: when the radio host has the Opus-RX path enabled it
  // streams RX audio back on the WebRTC audio track instead of as PCM over the
  // data channel. Play that track directly so we inherit the browser's native
  // adaptive jitter buffer + packet-loss concealment (lowest latency, robust to
  // internet loss). If the host doesn't enable it, no inbound track arrives and
  // RX audio keeps flowing through the existing PCM/WebAudio path — nothing here
  // fires. The unlock click is a user gesture, so autoplay is permitted.
  // FIELD FIX (no RX audio on the remote PC): `track` fires DURING
  // setRemoteDescription(answer), deep inside connectViaBroker — attaching
  // the listener only after it resolves missed the event every single time,
  // so the Opus track arrived and played nowhere. The receiver (and its
  // track) already exist by now: attach the present one directly, and keep
  // the listener for any future renegotiation.
  const existingAudio = conn.pc
    .getReceivers()
    .map((r) => r.track)
    .find((t): t is MediaStreamTrack => !!t && t.kind === 'audio');
  if (existingAudio) playRemoteRxAudioTrack(new MediaStream([existingAudio]));
  conn.pc.addEventListener('track', (ev) => {
    if (ev.track.kind !== 'audio') return;
    playRemoteRxAudioTrack(ev.streams[0] ?? new MediaStream([ev.track]));
  });

  // Voice-mic uplink: stream the operator's mic to the radio only while keyed
  // (MOX). The first key lazily prompts for mic permission; thereafter it's an
  // instant enable/disable. CW/RTTY/digital don't key MOX so the mic stays off.
  // A denied/absent mic leaves TX keying fully working, just without voice audio.
  const unsubMic = useTxStore.subscribe((s, prev) => {
    if (s.moxOn === prev.moxOn) return;
    void conn.setMicEnabled(s.moxOn).catch((e) => {
      console.warn('[remote] voice mic uplink unavailable:', e);
    });
  });

  // Route the 2-byte stream-request control frames (0x21/0x22) over the WebRTC
  // control channel instead of the (absent) local websocket. Drop the override
  // and tear the session down if the peer connection dies.
  setRemoteControlSender((bytes) => {
    try {
      conn.control.send(new Uint8Array(bytes));
    } catch {
      /* channel closed underneath us — onclose/onconnectionstatechange clean up */
    }
  });

  conn.pc.addEventListener('connectionstatechange', () => {
    const s = conn.pc.connectionState;
    if (s === 'closed' || s === 'failed' || s === 'disconnected') {
      stopLeasePulse();
      setRemoteControlSender(null);
      // Clear the tunnel channel and fail pending API requests so the UI gets a
      // network-style rejection rather than hanging on a dead session.
      setApiChannel(null);
      // Stop driving the mic from MOX and release the capture (conn.close also
      // stops the tracks; this unhooks the store subscription).
      unsubMic();
      // Detach the RX audio track sink so a dead stream doesn't linger.
      stopRemoteRxAudioTrack();
    }
  });

  // Ask the radio to start the RX display + audio streams. The server bumps its
  // global display/audio gates on these (RemoteWebRtcSession → hub), which is
  // what actually opens the panadapter frame fan-out for this session.
  sendDisplayStreamRequest(true);
  sendAudioStreamRequest(true);

  return conn;
}
