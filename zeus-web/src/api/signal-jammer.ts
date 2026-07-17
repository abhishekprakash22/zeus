// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import type { SignalJammerConfig } from '../state/signal-jammer-store';
import { SIGNAL_JAMMER_TEXT_TX_MAX_SAMPLES } from '../state/signal-jammer-limits';

export type SignalJammerTxSnapshot = {
  enabled: boolean;
  preset: string;
  level: number;
  toneHz: number;
  driftHz: number;
  pulseRateHz: number;
  textQueued: boolean;
  textPendingSamples: number;
};

type SignalJammerTxConfig = Pick<
  SignalJammerConfig,
  'preset' | 'level' | 'toneHz' | 'driftHz' | 'pulseRateHz'
> & {
  enabled: boolean;
};

type SignalJammerTextOptions = {
  autoTransmit?: boolean;
  signal?: AbortSignal;
};

export async function setSignalJammerTx(
  config: SignalJammerTxConfig,
  signal?: AbortSignal,
): Promise<SignalJammerTxSnapshot> {
  return jsonFetch('/api/tx/qrm', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(config),
    signal,
  });
}

export async function sendSignalJammerText(
  samples: Float32Array,
  sampleRate: number,
  options: SignalJammerTextOptions = {},
): Promise<SignalJammerTxSnapshot> {
  if (samples.length > SIGNAL_JAMMER_TEXT_TX_MAX_SAMPLES) {
    throw new Error(`text audio may not exceed ${SIGNAL_JAMMER_TEXT_TX_MAX_SAMPLES} samples`);
  }

  return jsonFetch('/api/tx/qrm/text', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      samplesBase64: float32ToBase64(samples),
      sampleRate,
      autoTransmit: options.autoTransmit === true,
    }),
    signal: options.signal,
  });
}

async function jsonFetch(input: RequestInfo, init: RequestInit): Promise<SignalJammerTxSnapshot> {
  const res = await fetch(input, init);
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = (await res.json()) as { error?: unknown };
      if (typeof body.error === 'string') message = body.error;
    } catch {
      // keep HTTP status text
    }
    throw new Error(message);
  }
  return normalizeSnapshot((await res.json()) as unknown);
}

function normalizeSnapshot(raw: unknown): SignalJammerTxSnapshot {
  const r = raw && typeof raw === 'object' ? (raw as Record<string, unknown>) : {};
  return {
    enabled: r.enabled === true,
    preset: typeof r.preset === 'string' ? r.preset : 'hash',
    level: typeof r.level === 'number' ? r.level : 0,
    toneHz: typeof r.toneHz === 'number' ? r.toneHz : 0,
    driftHz: typeof r.driftHz === 'number' ? r.driftHz : 0,
    pulseRateHz: typeof r.pulseRateHz === 'number' ? r.pulseRateHz : 0,
    textQueued: r.textQueued === true,
    textPendingSamples: typeof r.textPendingSamples === 'number' ? r.textPendingSamples : 0,
  };
}

function float32ToBase64(samples: Float32Array): string {
  const bytes = new Uint8Array(samples.length * 4);
  const view = new DataView(bytes.buffer);
  for (let i = 0; i < samples.length; i += 1) {
    view.setFloat32(i * 4, samples[i] ?? 0, true);
  }

  let binary = '';
  const chunkSize = 0x8000;
  for (let offset = 0; offset < bytes.length; offset += chunkSize) {
    const chunk = bytes.subarray(offset, offset + chunkSize);
    binary += String.fromCharCode(...chunk);
  }
  return btoa(binary);
}
