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

import { useEffect, useState } from 'react';
import { getAudioBus, type AudioFrameSubscriber } from '../audio/audio-bus';
import { useCwDecodeStore } from '../state/cw-decode-store';

let controllerClaimed = false;

export function CwDecodeController() {
  const enabled = useCwDecodeStore((s) => s.enabled);
  // The App mounts this beside every DiversityWindow instance, and one render
  // branch contains two of those — without a claim, two controllers spawn two
  // workers and double the inference load. First mount wins; others no-op.
  const [primary, setPrimary] = useState(false);
  useEffect(() => {
    if (controllerClaimed) return;
    controllerClaimed = true;
    setPrimary(true);
    return () => {
      controllerClaimed = false;
    };
  }, []);

  useEffect(() => {
    if (!enabled || !primary) return;
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
      // frame.samples is INTERLEAVED (frame.channels wide). Feeding stereo as
      // mono halves the effective speed and pitch — the exact bug behind the
      // 'decoding junk' field report (a 600 Hz note arrived at 300 Hz, below
      // the model's 400 Hz window, at double the element length). Downmix to
      // mono here; also halves the transfer.
      const ch = Math.max(1, frame.channels);
      let samples: Float32Array;
      if (ch === 1) {
        samples = frame.samples.slice(); // MUST copy: view onto WS buffer
      } else {
        const n = Math.floor(frame.samples.length / ch);
        samples = new Float32Array(n);
        for (let i = 0; i < n; i++) {
          let acc = 0;
          for (let c = 0; c < ch; c++) acc += frame.samples[i * ch + c]!;
          samples[i] = acc / ch;
        }
      }
      worker.postMessage({ type: 'pcm', samples, sampleRate: frame.sampleRateHz }, [samples.buffer]);
    };
    const unsub = getAudioBus().subscribe(onFrame);
    return () => {
      unsub();
      worker.postMessage({ type: 'stop' });
      setTimeout(() => worker.terminate(), 200);
      useCwDecodeStore.getState().setStatus('off');
    };
  }, [enabled, primary]);

  return null;
}
