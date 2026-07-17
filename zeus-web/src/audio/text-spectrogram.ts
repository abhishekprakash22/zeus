// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { normalizeTextSoundText } from '../state/signal-jammer-store';
import {
  SIGNAL_JAMMER_TEXT_MAX_PIXELS_PER_SECOND,
  SIGNAL_JAMMER_TEXT_MIN_PIXELS_PER_SECOND,
} from '../state/signal-jammer-limits';

export type TextSpectrogramOptions = {
  pixelsPerSecond?: number;
  minHz?: number;
  maxHz?: number;
  level?: number;
};

export type TextSpectrogram = {
  text: string;
  rows: number;
  columns: number;
  pixels: Uint8Array;
};

type SinkSelectableAudioContext = AudioContext & {
  setSinkId?: (sinkId: string) => Promise<void>;
};

const GLYPH_ROWS = 7;
const GLYPH_WIDTH = 5;
const GLYPH_ADVANCE = 6;
const RENDER_SCALE_X = 5;
const RENDER_SCALE_Y = 6;
const ROWS = GLYPH_ROWS * RENDER_SCALE_Y;
const DEFAULT_TEXT = 'CQ QRM';
const DEFAULT_PIXELS_PER_SECOND = 3;
const DEFAULT_MIN_HZ = 300;
const DEFAULT_MAX_HZ = 2600;
const TAU = Math.PI * 2;

let ctx: AudioContext | null = null;
let appliedSinkId: string | null = null;

const GLYPHS: Record<string, readonly string[]> = {
  ' ': ['00000', '00000', '00000', '00000', '00000', '00000', '00000'],
  '!': ['00100', '00100', '00100', '00100', '00100', '00000', '00100'],
  '?': ['01110', '10001', '00001', '00010', '00100', '00000', '00100'],
  '.': ['00000', '00000', '00000', '00000', '00000', '00110', '00110'],
  '-': ['00000', '00000', '00000', '11111', '00000', '00000', '00000'],
  '/': ['00001', '00010', '00100', '01000', '10000', '00000', '00000'],
  '0': ['01110', '10001', '10011', '10101', '11001', '10001', '01110'],
  '1': ['00100', '01100', '00100', '00100', '00100', '00100', '01110'],
  '2': ['01110', '10001', '00001', '00010', '00100', '01000', '11111'],
  '3': ['11110', '00001', '00001', '01110', '00001', '00001', '11110'],
  '4': ['00010', '00110', '01010', '10010', '11111', '00010', '00010'],
  '5': ['11111', '10000', '11110', '00001', '00001', '10001', '01110'],
  '6': ['00110', '01000', '10000', '11110', '10001', '10001', '01110'],
  '7': ['11111', '00001', '00010', '00100', '01000', '01000', '01000'],
  '8': ['01110', '10001', '10001', '01110', '10001', '10001', '01110'],
  '9': ['01110', '10001', '10001', '01111', '00001', '00010', '11100'],
  A: ['01110', '10001', '10001', '11111', '10001', '10001', '10001'],
  B: ['11110', '10001', '10001', '11110', '10001', '10001', '11110'],
  C: ['01111', '10000', '10000', '10000', '10000', '10000', '01111'],
  D: ['11110', '10001', '10001', '10001', '10001', '10001', '11110'],
  E: ['11111', '10000', '10000', '11110', '10000', '10000', '11111'],
  F: ['11111', '10000', '10000', '11110', '10000', '10000', '10000'],
  G: ['01111', '10000', '10000', '10011', '10001', '10001', '01111'],
  H: ['10001', '10001', '10001', '11111', '10001', '10001', '10001'],
  I: ['01110', '00100', '00100', '00100', '00100', '00100', '01110'],
  J: ['00001', '00001', '00001', '00001', '10001', '10001', '01110'],
  K: ['10001', '10010', '10100', '11000', '10100', '10010', '10001'],
  L: ['10000', '10000', '10000', '10000', '10000', '10000', '11111'],
  M: ['10001', '11011', '10101', '10101', '10001', '10001', '10001'],
  N: ['10001', '11001', '10101', '10011', '10001', '10001', '10001'],
  O: ['01110', '10001', '10001', '10001', '10001', '10001', '01110'],
  P: ['11110', '10001', '10001', '11110', '10000', '10000', '10000'],
  Q: ['01110', '10001', '10001', '10001', '10101', '10010', '01101'],
  R: ['11110', '10001', '10001', '11110', '10100', '10010', '10001'],
  S: ['01111', '10000', '10000', '01110', '00001', '00001', '11110'],
  T: ['11111', '00100', '00100', '00100', '00100', '00100', '00100'],
  U: ['10001', '10001', '10001', '10001', '10001', '10001', '01110'],
  V: ['10001', '10001', '10001', '10001', '10001', '01010', '00100'],
  W: ['10001', '10001', '10001', '10101', '10101', '10101', '01010'],
  X: ['10001', '10001', '01010', '00100', '01010', '10001', '10001'],
  Y: ['10001', '10001', '01010', '00100', '00100', '00100', '00100'],
  Z: ['11111', '00001', '00010', '00100', '01000', '10000', '11111'],
};

export function renderTextSpectrogram(text: string): TextSpectrogram {
  const normalized = (normalizeTextSoundText(text) || DEFAULT_TEXT).toUpperCase();
  const logicalColumns = Math.max(1, normalized.length * GLYPH_ADVANCE - 1);
  const columns = logicalColumns * RENDER_SCALE_X;
  const pixels = new Uint8Array(columns * ROWS);

  let x = 0;
  for (const char of normalized) {
    const glyph = (GLYPHS[char] ?? GLYPHS['?'])!;
    for (let row = 0; row < GLYPH_ROWS; row += 1) {
      const line = glyph[row] ?? '00000';
      for (let col = 0; col < GLYPH_WIDTH; col += 1) {
        if (line[col] !== '1') continue;
        const scaledX = (x + col) * RENDER_SCALE_X;
        const scaledY = row * RENDER_SCALE_Y;
        for (let dy = 0; dy < RENDER_SCALE_Y; dy += 1) {
          const outRow = scaledY + dy;
          for (let dx = 0; dx < RENDER_SCALE_X; dx += 1) {
            pixels[outRow * columns + scaledX + dx] = 1;
          }
        }
      }
    }
    x += GLYPH_ADVANCE;
  }

  return { text: normalized, rows: ROWS, columns, pixels };
}

export function estimateTextSpectrogramDurationSec(
  text: string,
  options: TextSpectrogramOptions = {},
): number {
  const spectrogram = renderTextSpectrogram(text);
  return spectrogram.columns / (normalizedPixelsPerSecond(options.pixelsPerSecond) * RENDER_SCALE_X);
}

export function estimateTextSpectrogramSampleCount(
  text: string,
  options: TextSpectrogramOptions = {},
  sampleRate = 48000,
): number {
  const spectrogram = renderTextSpectrogram(text);
  return samplesPerTextSpectrogramColumn(sampleRate, options.pixelsPerSecond) * spectrogram.columns;
}

export function rowToFrequencyHz(
  row: number,
  rows: number,
  minHz = DEFAULT_MIN_HZ,
  maxHz = DEFAULT_MAX_HZ,
): number {
  const clampedRows = Math.max(2, rows);
  const bottomToTop = (clampedRows - 1 - row) / (clampedRows - 1);
  return minHz * Math.pow(maxHz / minHz, bottomToTop);
}

export function synthesizeTextSpectrogramSamples(
  text: string,
  options: TextSpectrogramOptions = {},
  sampleRate = 48000,
): { sampleRate: number; samples: Float32Array; spectrogram: TextSpectrogram } {
  const spectrogram = renderTextSpectrogram(text);
  const pixelsPerSecond = normalizedPixelsPerSecond(options.pixelsPerSecond);
  const minHz = normalizedHz(options.minHz, DEFAULT_MIN_HZ);
  const maxHz = Math.max(minHz + 100, normalizedHz(options.maxHz, DEFAULT_MAX_HZ));
  const level = normalizedLevel(options.level);
  const samplesPerColumn = samplesPerTextSpectrogramColumn(sampleRate, pixelsPerSecond);
  const samples = new Float32Array(samplesPerColumn * spectrogram.columns);
  const rowFreqs = Array.from({ length: spectrogram.rows }, (_, row) =>
    rowToFrequencyHz(row, spectrogram.rows, minHz, maxHz),
  );
  const rowIncrements = rowFreqs.map((hz) => TAU * hz / sampleRate);

  for (let col = 0; col < spectrogram.columns; col += 1) {
    const litRows: number[] = [];
    for (let row = 0; row < spectrogram.rows; row += 1) {
      if (spectrogram.pixels[row * spectrogram.columns + col]! > 0) litRows.push(row);
    }
    if (litRows.length === 0) continue;

    const columnGain = 1 / Math.sqrt(litRows.length);
    const start = col * samplesPerColumn;
    for (let i = 0; i < samplesPerColumn; i += 1) {
      const sampleIndex = start + i;
      const envelope = clipEnvelope(sampleIndex, samples.length, sampleRate);
      let sample = 0;
      for (const row of litRows) {
        sample += Math.sin(rowIncrements[row]! * sampleIndex);
      }
      samples[sampleIndex] = samples[sampleIndex]! + sample * columnGain * envelope;
    }
  }

  normalizeSamples(samples, level);
  return { sampleRate, samples, spectrogram };
}

export function playTextSpectrogram(
  text: string,
  options: TextSpectrogramOptions = {},
  outputDeviceId = '',
): boolean {
  const audio = getCtx();
  if (!audio) return false;
  if (audio.state === 'suspended') {
    audio.resume().catch(() => {});
  }
  setOutputDeviceIfAvailable(audio, outputDeviceId);

  const rendered = synthesizeTextSpectrogramSamples(text, options, audio.sampleRate);
  const buffer = audio.createBuffer(1, rendered.samples.length, rendered.sampleRate);
  buffer.getChannelData(0).set(rendered.samples);
  const source = audio.createBufferSource();
  source.buffer = buffer;
  source.connect(audio.destination);
  source.start();
  return true;
}

function normalizedPixelsPerSecond(value: number | undefined): number {
  if (!Number.isFinite(value)) return DEFAULT_PIXELS_PER_SECOND;
  return Math.max(
    SIGNAL_JAMMER_TEXT_MIN_PIXELS_PER_SECOND,
    Math.min(SIGNAL_JAMMER_TEXT_MAX_PIXELS_PER_SECOND, value as number),
  );
}

function samplesPerTextSpectrogramColumn(sampleRate: number, pixelsPerSecond: number | undefined): number {
  return Math.max(
    16,
    Math.round(sampleRate / (normalizedPixelsPerSecond(pixelsPerSecond) * RENDER_SCALE_X)),
  );
}

function normalizedHz(value: number | undefined, fallback: number): number {
  if (!Number.isFinite(value)) return fallback;
  return Math.max(80, Math.min(8000, value as number));
}

function normalizedLevel(value: number | undefined): number {
  if (!Number.isFinite(value)) return 0.35;
  return Math.max(0, Math.min(1, (value as number) / 100));
}

function clipEnvelope(sampleIndex: number, totalSamples: number, sampleRate: number): number {
  const edge = Math.min(sampleIndex, totalSamples - 1 - sampleIndex);
  const ramp = Math.max(16, Math.round(sampleRate * 0.006));
  return Math.min(1, Math.max(0, edge / ramp));
}

function normalizeSamples(samples: Float32Array, level: number): void {
  let peak = 0;
  for (let i = 0; i < samples.length; i += 1) {
    peak = Math.max(peak, Math.abs(samples[i]!));
  }
  if (peak <= 0) return;
  const scale = (0.82 * level) / peak;
  for (let i = 0; i < samples.length; i += 1) {
    samples[i] = samples[i]! * scale;
  }
}

function getCtx(): AudioContext | null {
  if (ctx) return ctx;
  const Ctor =
    typeof window === 'undefined'
      ? undefined
      : (window.AudioContext ??
          (window as unknown as { webkitAudioContext?: typeof AudioContext })
            .webkitAudioContext);
  if (!Ctor) return null;
  try {
    ctx = new Ctor({ sampleRate: 48000, latencyHint: 'interactive' });
  } catch {
    ctx = null;
  }
  return ctx;
}

function setOutputDeviceIfAvailable(audio: AudioContext, outputDeviceId: string): void {
  const selectable = audio as SinkSelectableAudioContext;
  if (typeof selectable.setSinkId !== 'function') return;
  const sinkId = outputDeviceId.trim();
  if (sinkId === appliedSinkId) return;
  appliedSinkId = sinkId;
  selectable.setSinkId(sinkId).catch((err) => {
    appliedSinkId = null;
    console.warn('text-spectrogram.output.setSinkId failed; using browser default output', err);
  });
}
