import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import {
  normalizeSignalJammerConfig,
  useSignalJammerStore,
} from './signal-jammer-store';

describe('signal-jammer-store', () => {
  beforeEach(() => useSignalJammerStore.getState().__resetForTests());
  afterEach(() => useSignalJammerStore.getState().__resetForTests());

  it('starts disabled and inactive', () => {
    const s = useSignalJammerStore.getState();
    expect(s.enabled).toBe(false);
    expect(s.active).toBe(false);
  });

  it('does not let QRM become active until TX testing tools are enabled', () => {
    useSignalJammerStore.getState().setActive(true);
    expect(useSignalJammerStore.getState().active).toBe(false);

    useSignalJammerStore.getState().setEnabled(true);
    useSignalJammerStore.getState().setActive(true);
    expect(useSignalJammerStore.getState().active).toBe(true);
  });

  it('disabling TX testing tools stops active QRM and hides runtime errors', () => {
    const s = useSignalJammerStore.getState();
    s.setEnabled(true);
    s.setActive(true);
    s.setRuntimeStatus('unavailable', 'no audio');

    useSignalJammerStore.getState().setEnabled(false);

    const next = useSignalJammerStore.getState();
    expect(next.enabled).toBe(false);
    expect(next.active).toBe(false);
    expect(next.runtimeStatus).toBe('idle');
    expect(next.runtimeMessage).toBeNull();
  });

  it('normalizes operator-facing control ranges', () => {
    expect(
      normalizeSignalJammerConfig({
        preset: 'hash',
        level: 240,
        toneHz: 99,
        driftHz: 999,
        pulseRateHz: 0.1,
      }),
    ).toEqual({
      preset: 'hash',
      level: 100,
      toneHz: 250,
      driftHz: 500,
      pulseRateHz: 0.5,
      textSoundText: 'CQ QRM',
      textPixelsPerSecond: 3,
    });
  });

  it('normalizes typed spectrogram text and speed', () => {
    const s = useSignalJammerStore.getState();
    s.setTextSoundText('HELLO    QRM');
    s.setTextPixelsPerSecond(999);

    const next = useSignalJammerStore.getState();
    expect(next.textSoundText).toBe('HELLO QRM');
    expect(next.textPixelsPerSecond).toBe(15);

    s.setTextPixelsPerSecond(1);
    expect(useSignalJammerStore.getState().textPixelsPerSecond).toBe(2);
  });
});
