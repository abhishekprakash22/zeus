// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// DeepCW inference worker. The preprocessing contract and CTC decode are
// ported from e04/deepcw-engine (AGPL-3.0-or-later; model.onnx and its
// metadata are that project's work — see THIRD-PARTY-NOTICES). Pipeline per
// the engine metadata: resample RX audio to 3200 Hz, Hann/256 STFT hop 48,
// magnitude bins covering 400-1200 Hz (65 bins), log1p, tensor
// [1,1,T,65] -> "log_probs" [1,T,42] -> greedy CTC (blank 41).
//
// Streaming strategy (v1): keep a sliding window of the last WINDOW_SEC of
// audio; decode the whole window each tick; treat everything except the
// trailing VOLATILE_SEC as settled, and emit only characters beyond the
// longest already-emitted stable prefix. On disagreement with previously
// emitted text, re-anchor silently (the transcript favors availability over
// retroactive edits — same trade CW Skimmer makes).

import * as ort from 'onnxruntime-web/wasm';

interface Meta {
  chars: string[]; blank_index: number; sample_rate: number;
  fft_length: number; hop_length: number;
  spectrogram_min_freq_hz: number; spectrogram_max_freq_hz: number;
  spectrogram_frequency_bins: number;
}

const WINDOW_SEC = 12;
const VOLATILE_SEC = 1.6;
const TICK_MS = 1400;

let session: ort.InferenceSession | null = null;
let meta: Meta | null = null;
let ring = new Float32Array(0);
let ringRate = 3200;
let emitted = '';
let timer: ReturnType<typeof setInterval> | null = null;
let busy = false;

function resampleTo(audio: Float32Array, from: number, to: number): Float32Array {
  if (from === to) return audio;
  const outLen = Math.round((audio.length * to) / from);
  const out = new Float32Array(outLen);
  for (let i = 0; i < outLen; i++) {
    const p = (i * from) / to;
    const l = Math.floor(p);
    const r = Math.min(l + 1, audio.length - 1);
    const f = p - l;
    out[i] = audio[l]! * (1 - f) + audio[r]! * f;
  }
  return out;
}

function spectrogram(audio: Float32Array, m: Meta): { data: Float32Array; frames: number } {
  const N = m.fft_length, hop = m.hop_length, bins = m.spectrogram_frequency_bins;
  const binHz = m.sample_rate / N;
  const startBin = Math.ceil(m.spectrogram_min_freq_hz / binHz);
  const pad = Math.floor(N / 2);
  const padded = new Float32Array(audio.length + pad * 2);
  for (let i = 0; i < pad; i++) {
    padded[i] = audio[Math.min(pad - i, audio.length - 1)]!;
    padded[pad + audio.length + i] = audio[Math.max(0, audio.length - 2 - i)]!;
  }
  padded.set(audio, pad);
  const frames = 1 + Math.floor((padded.length - N) / hop);
  const out = new Float32Array(frames * bins);
  const win = new Float32Array(N);
  for (let i = 0; i < N; i++) win[i] = 0.5 - 0.5 * Math.cos((2 * Math.PI * i) / N);
  // Precompute DFT twiddles for just the 65 needed bins.
  const cos = new Float32Array(bins * N), sin = new Float32Array(bins * N);
  for (let b = 0; b < bins; b++)
    for (let n = 0; n < N; n++) {
      const a = (-2 * Math.PI * (startBin + b) * n) / N;
      cos[b * N + n] = Math.cos(a); sin[b * N + n] = Math.sin(a);
    }
  const frame = new Float32Array(N);
  for (let f = 0; f < frames; f++) {
    const s0 = f * hop;
    for (let i = 0; i < N; i++) frame[i] = padded[s0 + i]! * win[i]!;
    for (let b = 0; b < bins; b++) {
      let re = 0, im = 0;
      const o = b * N;
      for (let n = 0; n < N; n++) { re += frame[n]! * cos[o + n]!; im += frame[n]! * sin[o + n]!; }
      out[f * bins + b] = Math.log1p(Math.hypot(re, im));
    }
  }
  return { data: out, frames };
}

function ctcGreedy(logProbs: Float32Array, frames: number, classes: number, m: Meta): string {
  let prev = -1; let text = '';
  for (let t = 0; t < frames; t++) {
    let best = 0, bestV = -Infinity;
    const o = t * classes;
    for (let c = 0; c < classes; c++) { const v = logProbs[o + c]!; if (v > bestV) { bestV = v; best = c; } }
    if (best !== prev && best !== m.blank_index) text += m.chars[best] ?? '';
    prev = best;
  }
  return text;
}

async function tick(): Promise<void> {
  if (busy || !session || !meta) return;
  if (ring.length < meta.sample_rate * 4) return; // need a few seconds
  busy = true;
  try {
    const { data, frames } = spectrogram(ring, meta);
    const input = new ort.Tensor('float32', data, [1, 1, frames, meta.spectrogram_frequency_bins]);
    const out = await session.run({ spectrogram: input });
    const lp = out['log_probs']!;
    const [, T, C] = lp.dims as number[];
    const full = ctcGreedy(lp.data as Float32Array, T!, C!, meta);
    // stable region: drop chars produced by the trailing VOLATILE_SEC
    const stableFrac = Math.max(0, 1 - (VOLATILE_SEC * meta.sample_rate) / ring.length);
    const stable = full.slice(0, Math.floor(full.length * stableFrac));
    if (stable.length > 0) {
      if (emitted.length === 0 || stable.startsWith(emitted)) {
        const fresh = stable.slice(emitted.length);
        if (fresh) { emitted = stable; postMessage({ type: 'chars', text: fresh }); }
      } else {
        // Window slid past our anchor or the net revised history: re-anchor on
        // the longest suffix of `emitted` that prefixes `stable`.
        let k = Math.min(emitted.length, stable.length);
        while (k > 0 && !stable.startsWith(emitted.slice(emitted.length - k))) k--;
        const fresh = stable.slice(k);
        emitted = stable;
        if (fresh) postMessage({ type: 'chars', text: fresh });
      }
    }
  } catch (err) {
    postMessage({ type: 'error', message: String(err) });
  } finally {
    busy = false;
  }
}

onmessage = async (e: MessageEvent) => {
  const msg = e.data;
  if (msg.type === 'init') {
    try {
      ort.env.wasm.wasmPaths = msg.ortBase as string;
      ort.env.wasm.numThreads = 1;
      meta = msg.meta as Meta;
      ringRate = meta.sample_rate;
      session = await ort.InferenceSession.create(msg.modelUrl as string, {
        executionProviders: ['wasm'],
      });
      timer = setInterval(() => void tick(), TICK_MS);
      postMessage({ type: 'ready' });
    } catch (err) {
      postMessage({ type: 'error', message: String(err) });
    }
  } else if (msg.type === 'pcm') {
    if (!meta) return;
    const chunk = resampleTo(msg.samples as Float32Array, msg.sampleRate as number, ringRate);
    const maxLen = WINDOW_SEC * ringRate;
    const merged = new Float32Array(Math.min(maxLen, ring.length + chunk.length));
    const keep = merged.length - chunk.length;
    if (keep > 0) merged.set(ring.subarray(ring.length - keep), 0);
    merged.set(chunk.subarray(Math.max(0, chunk.length - merged.length)), Math.max(0, keep));
    ring = merged;
  } else if (msg.type === 'stop') {
    if (timer) clearInterval(timer);
    session = null; ring = new Float32Array(0); emitted = '';
    close();
  }
};
