// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { useEffect } from 'react';
import { setSignalJammerTx } from '../api/signal-jammer';
import { startOrUpdateSignalJammer, stopSignalJammer } from '../audio/signal-jammer';
import { useAudioDeviceStore } from '../state/audio-device-store';
import { useSignalJammerStore } from '../state/signal-jammer-store';

export function SignalJammerRuntime() {
  const enabled = useSignalJammerStore((s) => s.enabled);
  const active = useSignalJammerStore((s) => s.active);
  const preset = useSignalJammerStore((s) => s.preset);
  const level = useSignalJammerStore((s) => s.level);
  const toneHz = useSignalJammerStore((s) => s.toneHz);
  const driftHz = useSignalJammerStore((s) => s.driftHz);
  const pulseRateHz = useSignalJammerStore((s) => s.pulseRateHz);
  const setRuntimeStatus = useSignalJammerStore((s) => s.setRuntimeStatus);
  const outputDeviceId = useAudioDeviceStore((s) => s.browserOutputDeviceId);

  useEffect(
    () => () => {
      const s = useSignalJammerStore.getState();
      stopSignalJammer();
      void setSignalJammerTx({
        enabled: false,
        preset: s.preset,
        level: s.level,
        toneHz: s.toneHz,
        driftHz: s.driftHz,
        pulseRateHz: s.pulseRateHz,
      }).catch(() => {});
    },
    [],
  );

  useEffect(() => {
    const controller = new AbortController();
    const txEnabled = enabled && active;
    void setSignalJammerTx(
      {
        enabled: txEnabled,
        preset,
        level,
        toneHz,
        driftHz,
        pulseRateHz,
      },
      controller.signal,
    ).catch((err) => {
      if (controller.signal.aborted) return;
      console.warn('signal-jammer.tx.update failed', err);
      if (txEnabled) setRuntimeStatus('unavailable', 'TX test unavailable');
    });

    if (!enabled) {
      stopSignalJammer();
      setRuntimeStatus('idle');
      return () => controller.abort();
    }

    if (!active) {
      stopSignalJammer();
      if (useSignalJammerStore.getState().runtimeStatus === 'running') {
        setRuntimeStatus('idle');
      }
      return () => controller.abort();
    }

    const started = startOrUpdateSignalJammer(
      { preset, level, toneHz, driftHz, pulseRateHz },
      outputDeviceId,
    );
    if (started) {
      setRuntimeStatus('running');
      return () => controller.abort();
    }

    setRuntimeStatus('running', 'Local monitor unavailable');
    return () => controller.abort();
  }, [
    active,
    driftHz,
    enabled,
    level,
    outputDeviceId,
    preset,
    pulseRateHz,
    setRuntimeStatus,
    toneHz,
  ]);

  return null;
}
