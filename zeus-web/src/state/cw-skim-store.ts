// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// CW skimmer state (DeepCW phase 2). The Pi-side CwSkimmerService detects
// concurrent CW carriers, decodes each with the neural model, and streams
// `cwskim` SSE events (kind:'roster' for the channel set, kind:'text' for
// per-channel transcript deltas). This store mirrors that into the lanes the
// waterfall overlay paints.

import { create } from 'zustand';

export interface CwSkimChannelDto {
  id: number;
  pitchHz: number;
  snrDb: number;
  active: boolean;
  decodable: boolean;
}

export interface CwSkimChannel extends CwSkimChannelDto {
  text: string;
  lastCharAt: number;
}

interface CwSkimEvent {
  receiver: number;
  kind: 'roster' | 'text';
  enabled?: boolean;
  channels?: CwSkimChannelDto[];
  channel?: CwSkimChannelDto;
  delta?: string;
}

export interface CwSkimState {
  enabled: boolean;
  receiver: number;
  channels: Record<number, CwSkimChannel>;
  setEnabled: (on: boolean) => void;
  ingest: (ev: CwSkimEvent) => void;
  clear: () => void;
}

const TEXT_CAP = 600;

export const useCwSkimStore = create<CwSkimState>((set) => ({
  enabled: false,
  receiver: 0,
  channels: {},
  setEnabled: (on) => set((s) => ({ enabled: on, channels: on ? s.channels : {} })),
  ingest: (ev) =>
    set((s) => {
      if (ev.kind === 'roster') {
        const next: Record<number, CwSkimChannel> = {};
        for (const c of ev.channels ?? []) {
          const prev = s.channels[c.id];
          next[c.id] = { ...c, text: prev?.text ?? '', lastCharAt: prev?.lastCharAt ?? 0 };
        }
        return {
          channels: next,
          receiver: ev.receiver,
          enabled: ev.enabled ?? s.enabled,
        };
      }
      const c = ev.channel;
      if (!c) return s;
      const prev = s.channels[c.id];
      const text = ((prev?.text ?? '') + (ev.delta ?? '')).slice(-TEXT_CAP);
      return {
        channels: {
          ...s.channels,
          [c.id]: { ...c, text, lastCharAt: Date.now() },
        },
      };
    }),
  clear: () => set({ channels: {} }),
}));
