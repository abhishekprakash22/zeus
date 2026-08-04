// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Neural CW decode controller — the audio-bus tap the bus's own comment
// promised ("any number of taps (the CW decoder, ...)"). While enabled:
// spawn the DeepCW worker, feed it every decoded RX AudioFrame (sliced —
// the frame's samples may be a view onto the WS receive buffer), and land
// decoded characters in the store. Renders nothing.

import { useEffect } from 'react';
import { getAudioBus, type AudioFrameSubscriber } from '../audio/audio-bus';
import { useCwDecodeStore } from '../state/cw-decode-store';

export function CwDecodeController() {
  const enabled = useCwDecodeStore((s) => s.enabled);

  useEffect(() => {
    if (!enabled) return;
    const store = useCwDecodeStore.getState();
    store.setStatus('loading');
    const worker = new Worker(new URL('../dsp/cw-decoder.worker.ts', import.meta.url), {
      type: 'module',
    });
    worker.onmessage = (e: MessageEvent) => {
      const msg = e.data;
      if (msg.type === 'ready') useCwDecodeStore.getState().setStatus('running');
      else if (msg.type === 'chars') useCwDecodeStore.getState().appendChars(msg.text as string);
      else if (msg.type === 'error')
        useCwDecodeStore.getState().setStatus('error', msg.message as string);
    };
    // metadata is tiny — fetch it, then a single init spins the session up
    void fetch('/deepcw/model_en.json')
      .then((r) => r.json())
      .then((meta) => worker.postMessage({ type: 'init', modelUrl: '/deepcw/model_en.onnx', ortBase: '/deepcw/ort/', meta }))
      .catch((err) => useCwDecodeStore.getState().setStatus('error', String(err)));
    const onFrame: AudioFrameSubscriber = (frame) => {
      const samples = frame.samples.slice(); // MUST copy: view onto WS buffer
      worker.postMessage({ type: 'pcm', samples, sampleRate: frame.sampleRateHz }, [samples.buffer]);
    };
    const unsub = getAudioBus().subscribe(onFrame);
    return () => {
      unsub();
      worker.postMessage({ type: 'stop' });
      setTimeout(() => worker.terminate(), 200);
      useCwDecodeStore.getState().setStatus('off');
    };
  }, [enabled]);

  return null;
}
