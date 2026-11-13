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

// Which items the spectrum radial menu offers for a given press, and where
// they sit. Pure, so the rules are testable without a DOM.

import type { RadialContext } from '../../state/radial-menu-store';
import type { MemoryChannel } from '../../api/memories';

export type RadialItemId = 'split' | 'rx2' | 'notch' | 'memory' | 'forget' | 'spot';

export type RadialItem = {
  id: RadialItemId;
  label: string;
  icon: string;
  hint: string;
  disabled: boolean;
};

export type RadialEnv = {
  /** VFO lock engaged: nothing that moves a dial. */
  locked: boolean;
  /** The radio exposes a second receiver. */
  hasRx2: boolean;
  /** A DX cluster node is connected (spotting possible). */
  clusterConnected: boolean;
  /** A stored memory channel sits under the press. */
  memoryHit: MemoryChannel | null;
};

/** Memory markers are hit within this many screen pixels. */
export const MEMORY_HIT_PX = 8;

export function radialItems(ctx: Pick<RadialContext, 'rxIndex' | 'panRxIndex' | 'carrierHz'>, env: RadialEnv): RadialItem[] {
  const items: RadialItem[] = [];
  const onRx1 = ctx.rxIndex === 0;
  if (onRx1) {
    items.push({
      id: 'split',
      label: 'Split',
      icon: '⇅',
      hint: env.locked ? 'VFO locked' : 'Transmit here on VFO B, keep listening on A',
      disabled: env.locked,
    });
    if (env.hasRx2) {
      items.push({
        id: 'rx2',
        label: 'RX2',
        icon: '◎',
        hint: env.locked ? 'VFO locked' : 'Listen here on RX2',
        disabled: env.locked,
      });
    }
  }
  if (ctx.panRxIndex === 0) {
    items.push({
      id: 'notch',
      label: 'Notch',
      icon: '⊘',
      hint: ctx.carrierHz != null ? 'Notch the carrier here' : 'Notch here',
      disabled: false,
    });
  }
  items.push(
    env.memoryHit
      ? {
          id: 'forget',
          label: 'Forget',
          icon: '✕',
          hint: `Delete memory "${env.memoryHit.name}"`,
          disabled: false,
        }
      : { id: 'memory', label: 'Store', icon: '★', hint: 'Store as a memory channel', disabled: false },
  );
  items.push({
    id: 'spot',
    label: 'Spot',
    icon: '⚑',
    hint: env.clusterConnected ? 'Post a DX cluster spot here' : 'Connect a DX cluster to spot',
    disabled: !env.clusterConnected,
  });
  return items;
}

export const RING_RADIUS_PX = 78;
export const ITEM_SIZE_PX = 54;
export const CENTER_SIZE_PX = 72;

/** Item centre offsets around the ring, first item at 12 o'clock, clockwise. */
export function ringOffsets(n: number, radius = RING_RADIUS_PX): { dx: number; dy: number }[] {
  const out: { dx: number; dy: number }[] = [];
  for (let i = 0; i < n; i++) {
    const a = -Math.PI / 2 + (i * 2 * Math.PI) / Math.max(1, n);
    out.push({ dx: Math.round(Math.cos(a) * radius), dy: Math.round(Math.sin(a) * radius) });
  }
  return out;
}

/** Keep the whole ring on screen: the centre moves in from the edges. */
export function clampCenter(x: number, y: number, vw: number, vh: number): { x: number; y: number } {
  const m = RING_RADIUS_PX + ITEM_SIZE_PX / 2 + 6;
  const cx = vw > 2 * m ? Math.min(vw - m, Math.max(m, x)) : vw / 2;
  const cy = vh > 2 * m ? Math.min(vh - m, Math.max(m, y)) : vh / 2;
  return { x: cx, y: cy };
}

/** "14.074 000" — MHz with the kHz/Hz groups split for reading at a glance. */
export function formatDialHz(hz: number): string {
  const h = Math.round(hz);
  const mhz = Math.floor(h / 1e6);
  const rest = h - mhz * 1e6;
  const khz = Math.floor(rest / 1000);
  const units = rest - khz * 1000;
  return `${mhz}.${String(khz).padStart(3, '0')} ${String(units).padStart(3, '0')}`;
}

/** The frequency a spot should carry: the carrier for CW, the dial otherwise. */
export function spotFrequencyHz(ctx: Pick<RadialContext, 'mode' | 'carrierHz' | 'tuneHz'>): number {
  if ((ctx.mode === 'CWU' || ctx.mode === 'CWL') && ctx.carrierHz != null) return ctx.carrierHz;
  return ctx.tuneHz;
}
