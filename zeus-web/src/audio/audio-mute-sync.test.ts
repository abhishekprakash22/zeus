// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

import { describe, expect, it } from 'vitest';
import {
  browserMuteAction,
  decodeAudioMuteFrame,
  handleAudioMuteFrame,
  useAudioMuteSyncStore,
} from './audio-mute-sync';

const frame = (...b: number[]) => new Uint8Array(b).buffer;

describe('decodeAudioMuteFrame', () => {
  it('reads state and source', () => {
    expect(decodeAudioMuteFrame(frame(0x3f, 1, 1))).toEqual({ muted: true, source: 'midi' });
    expect(decodeAudioMuteFrame(frame(0x3f, 0, 0))).toEqual({ muted: false, source: 'ui' });
  });

  it('rejects short, foreign or malformed frames', () => {
    expect(decodeAudioMuteFrame(frame(0x3f, 1))).toBeNull();
    expect(decodeAudioMuteFrame(frame(0x3e, 1, 1))).toBeNull();
    expect(decodeAudioMuteFrame(frame(0x3f, 7, 1))).toBeNull();
  });
});

describe('handleAudioMuteFrame', () => {
  it('publishes each frame as a new event, even when repeated', () => {
    handleAudioMuteFrame(frame(0x3f, 1, 1));
    const a = useAudioMuteSyncStore.getState().last!;
    handleAudioMuteFrame(frame(0x3f, 1, 1));
    const b = useAudioMuteSyncStore.getState().last!;
    expect(b.muted).toBe(true);
    expect(b.seq).toBe(a.seq + 1);
  });
});

describe('browserMuteAction', () => {
  const ev = (muted: boolean, source: 'ui' | 'midi') => ({ muted, source, seq: 1 });

  it('stops a playing tab on a MIDI mute and restarts it on a MIDI unmute', () => {
    expect(browserMuteAction(ev(true, 'midi'), 'playing')).toBe('stop');
    expect(browserMuteAction(ev(true, 'midi'), 'loading')).toBe('stop');
    expect(browserMuteAction(ev(false, 'midi'), 'idle')).toBe('start');
  });

  it('does nothing when already in the requested state', () => {
    expect(browserMuteAction(ev(true, 'midi'), 'idle')).toBeNull();
    expect(browserMuteAction(ev(false, 'midi'), 'playing')).toBeNull();
  });

  it("ignores another client's button", () => {
    expect(browserMuteAction(ev(true, 'ui'), 'playing')).toBeNull();
    expect(browserMuteAction(ev(false, 'ui'), 'idle')).toBeNull();
  });

  it('never starts a browser decoder on a desktop host', () => {
    expect(browserMuteAction(ev(false, 'midi'), 'native')).toBeNull();
    expect(browserMuteAction(null, 'playing')).toBeNull();
  });
});
