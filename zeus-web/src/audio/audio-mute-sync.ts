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
//
// Master audio mute announcements (MsgType 0x3F): the radio sends one every
// time its transport-bar mute flips, from a client's button or a MIDI
// control. Payload [type][muted u8][source u8: 0 = UI, 1 = MIDI]. The
// transport bar's Mute button (AudioToggle) follows them.

import { create } from 'zustand';

export const MSG_TYPE_AUDIO_MUTE = 0x3f;

export type AudioMuteSource = 'ui' | 'midi';

export type AudioMuteEvent = {
  muted: boolean;
  source: AudioMuteSource;
  /** Increments per frame so two identical events still re-run effects. */
  seq: number;
};

export function decodeAudioMuteFrame(buf: ArrayBuffer): { muted: boolean; source: AudioMuteSource } | null {
  if (buf.byteLength < 3) return null;
  const b = new Uint8Array(buf, 0, 3);
  if (b[0] !== MSG_TYPE_AUDIO_MUTE || b[1]! > 1) return null;
  return { muted: b[1] === 1, source: b[2] === 1 ? 'midi' : 'ui' };
}

type State = { last: AudioMuteEvent | null };

export const useAudioMuteSyncStore = create<State>(() => ({ last: null }));

let seq = 0;

export function handleAudioMuteFrame(buf: ArrayBuffer): void {
  const f = decodeAudioMuteFrame(buf);
  if (!f) return;
  seq += 1;
  useAudioMuteSyncStore.setState({ last: { ...f, seq } });
}

/**
 * What the in-browser audio client should do about an announcement. Browsers
 * follow MIDI-sourced changes only: a UI change came from some client's own
 * button, and one listener's button must not silence another's tab.
 */
export function browserMuteAction(
  ev: AudioMuteEvent | null,
  clientKind: string,
): 'stop' | 'start' | null {
  if (!ev || ev.source !== 'midi') return null;
  const live = clientKind === 'playing' || clientKind === 'loading';
  if (ev.muted && live) return 'stop';
  if (!ev.muted && !live && clientKind !== 'native') return 'start';
  return null;
}
