// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { create } from 'zustand';
import {
  SIGNAL_JAMMER_TEXT_MAX_CHARS,
  SIGNAL_JAMMER_TEXT_MAX_PIXELS_PER_SECOND,
  SIGNAL_JAMMER_TEXT_MIN_PIXELS_PER_SECOND,
} from './signal-jammer-limits';

export const SIGNAL_JAMMER_PRESETS = ['hash', 'heterodyne', 'pulse'] as const;

export type SignalJammerPreset = (typeof SIGNAL_JAMMER_PRESETS)[number];

export type SignalJammerRuntimeStatus = 'idle' | 'running' | 'unavailable';

export type SignalJammerConfig = {
  preset: SignalJammerPreset;
  level: number;
  toneHz: number;
  driftHz: number;
  pulseRateHz: number;
  textSoundText: string;
  textPixelsPerSecond: number;
};

type SignalJammerState = SignalJammerConfig & {
  enabled: boolean;
  active: boolean;
  runtimeStatus: SignalJammerRuntimeStatus;
  runtimeMessage: string | null;
  setEnabled: (enabled: boolean) => void;
  setActive: (active: boolean) => void;
  toggleActive: () => void;
  setPreset: (preset: SignalJammerPreset) => void;
  setLevel: (level: number) => void;
  setToneHz: (toneHz: number) => void;
  setDriftHz: (driftHz: number) => void;
  setPulseRateHz: (pulseRateHz: number) => void;
  setTextSoundText: (textSoundText: string) => void;
  setTextPixelsPerSecond: (textPixelsPerSecond: number) => void;
  setRuntimeStatus: (
    runtimeStatus: SignalJammerRuntimeStatus,
    runtimeMessage?: string | null,
  ) => void;
  __resetForTests: () => void;
};

export const SIGNAL_JAMMER_DEFAULTS: SignalJammerConfig = {
  preset: 'hash',
  level: 28,
  toneHz: 980,
  driftHz: 80,
  pulseRateHz: 3.5,
  textSoundText: 'CQ QRM',
  textPixelsPerSecond: 3,
};

const initialState = {
  enabled: false,
  active: false,
  runtimeStatus: 'idle' as SignalJammerRuntimeStatus,
  runtimeMessage: null,
  ...SIGNAL_JAMMER_DEFAULTS,
};

function clampNumber(value: number, min: number, max: number): number {
  if (!Number.isFinite(value)) return min;
  return Math.min(max, Math.max(min, value));
}

export function normalizeSignalJammerConfig(
  config: Partial<SignalJammerConfig>,
): SignalJammerConfig {
  const preset = SIGNAL_JAMMER_PRESETS.includes(config.preset as SignalJammerPreset)
    ? (config.preset as SignalJammerPreset)
    : SIGNAL_JAMMER_DEFAULTS.preset;
  return {
    preset,
    level: clampNumber(config.level ?? SIGNAL_JAMMER_DEFAULTS.level, 0, 100),
    toneHz: Math.round(clampNumber(config.toneHz ?? SIGNAL_JAMMER_DEFAULTS.toneHz, 250, 3200)),
    driftHz: Math.round(clampNumber(config.driftHz ?? SIGNAL_JAMMER_DEFAULTS.driftHz, 0, 500)),
    pulseRateHz: Number(clampNumber(config.pulseRateHz ?? SIGNAL_JAMMER_DEFAULTS.pulseRateHz, 0.5, 12).toFixed(1)),
    textSoundText: normalizeTextSoundText(config.textSoundText ?? SIGNAL_JAMMER_DEFAULTS.textSoundText),
    textPixelsPerSecond: Math.round(
      clampNumber(
        config.textPixelsPerSecond ?? SIGNAL_JAMMER_DEFAULTS.textPixelsPerSecond,
        SIGNAL_JAMMER_TEXT_MIN_PIXELS_PER_SECOND,
        SIGNAL_JAMMER_TEXT_MAX_PIXELS_PER_SECOND,
      ),
    ),
  };
}

export function normalizeTextSoundText(text: string): string {
  const collapsed = text.replace(/\s+/g, ' ').trimStart();
  return collapsed.length > SIGNAL_JAMMER_TEXT_MAX_CHARS
    ? collapsed.slice(0, SIGNAL_JAMMER_TEXT_MAX_CHARS)
    : collapsed;
}

export const useSignalJammerStore = create<SignalJammerState>((set, get) => ({
  ...initialState,

  setEnabled: (enabled) =>
    set((s) => ({
      enabled,
      active: enabled ? s.active : false,
      runtimeStatus: enabled ? s.runtimeStatus : 'idle',
      runtimeMessage: enabled ? s.runtimeMessage : null,
    })),

  setActive: (active) =>
    set((s) => ({
      active: s.enabled ? active : false,
      runtimeStatus: 'idle',
      runtimeMessage: null,
    })),

  toggleActive: () => {
    const s = get();
    set({
      active: s.enabled ? !s.active : false,
      runtimeStatus: 'idle',
      runtimeMessage: null,
    });
  },

  setPreset: (preset) => set({ preset: normalizeSignalJammerConfig({ preset }).preset }),
  setLevel: (level) => set({ level: normalizeSignalJammerConfig({ level }).level }),
  setToneHz: (toneHz) => set({ toneHz: normalizeSignalJammerConfig({ toneHz }).toneHz }),
  setDriftHz: (driftHz) => set({ driftHz: normalizeSignalJammerConfig({ driftHz }).driftHz }),
  setPulseRateHz: (pulseRateHz) =>
    set({ pulseRateHz: normalizeSignalJammerConfig({ pulseRateHz }).pulseRateHz }),
  setTextSoundText: (textSoundText) =>
    set({ textSoundText: normalizeSignalJammerConfig({ textSoundText }).textSoundText }),
  setTextPixelsPerSecond: (textPixelsPerSecond) =>
    set({
      textPixelsPerSecond: normalizeSignalJammerConfig({ textPixelsPerSecond }).textPixelsPerSecond,
    }),

  setRuntimeStatus: (runtimeStatus, runtimeMessage = null) =>
    set({ runtimeStatus, runtimeMessage }),

  __resetForTests: () => set(initialState),
}));
