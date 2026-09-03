// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// render-stats — the meter that comes before the diet. The Pi 5 GPU
// optimization series (R8+LUT rows, zero-copy flags, vertex-fetch traces)
// ships benchmark-gated after the R16F lesson: on V3D, an "optimization"
// without numbers is a coin flip. This module makes the numbers cheap:
//
//   open the workspace with  ?renderstats=1   (or set
//   localStorage['zeus.renderStats']='1' and reload)
//
// and a small fixed overlay reports, once per second:
//   - main-thread frame cadence: fps, EMA frame ms, worst frame in window
//   - texture upload traffic: MB/s and calls/s (texImage2D + texSubImage2D)
//   - draw calls/s (drawArrays + drawElements)
//
// Instrumentation is GL-level: instrumentGlForStats(gl) wraps the context's
// upload/draw entry points with counting proxies, so each renderer carries
// exactly one hook line and the accounting can never drift from the code it
// measures. When the flag is off, the hook is a no-op and nothing is
// wrapped — zero cost in normal operation, inert under vitest's fake GL
// objects (methods are wrapped only when present and only when enabled).

const enabled: boolean =
  typeof window !== 'undefined' &&
  (new URLSearchParams(window.location.search).has('renderstats') ||
    window.localStorage?.getItem('zeus.renderStats') === '1');

let uploadBytes = 0;
let uploadCalls = 0;
let drawCalls = 0;
let overlayStarted = false;

// ---- the splitter: where do the milliseconds live? ----
// GL-span: wall time from the FIRST to the LAST instrumented GL call
// within one rAF frame — the render functions' whole working section,
// internal JS included. Epoch = the overlay's own rAF counter; a wrapped
// call landing in a new epoch flushes the previous frame's span.
let frameEpoch = 0;
let spanEpoch = -1;
let spanStart = 0;
let spanEnd = 0;
let glSpanAccumMs = 0;
let glSpanFrames = 0;
// Long tasks: every main-thread stall > 50 ms, counted and totalled —
// the species the worst-frame number belongs to. Standard observer, no
// hooks needed.
let longTasks = 0;
let longTaskMs = 0;

function noteGlCall(): void {
  const t = performance.now();
  if (spanEpoch !== frameEpoch) {
    if (spanEpoch >= 0 && spanEnd > spanStart) {
      glSpanAccumMs += spanEnd - spanStart;
      glSpanFrames++;
    }
    spanEpoch = frameEpoch;
    spanStart = t;
  }
  spanEnd = performance.now();
}

function byteLengthOfLastView(args: unknown[]): number {
  // texImage2D / texSubImage2D overloads end with an ArrayBufferView (or a
  // TexImageSource, or null). The view's byteLength is the honest upload
  // size; other overloads count 0 rather than guessing.
  for (let i = args.length - 1; i >= 0; i--) {
    const a = args[i] as { byteLength?: number } | null | undefined;
    if (a && typeof a.byteLength === 'number') return a.byteLength;
    if (a != null) break;
  }
  return 0;
}

function wrap<T extends object>(
  obj: T,
  name: string,
  onCall: (args: unknown[]) => void,
): void {
  const anyObj = obj as Record<string, unknown>;
  const fn = anyObj[name];
  if (typeof fn !== 'function') return;
  anyObj[name] = function (this: unknown, ...args: unknown[]) {
    onCall(args);
    return (fn as (...a: unknown[]) => unknown).apply(this, args);
  };
}

function startOverlay(): void {
  if (overlayStarted || typeof document === 'undefined') return;
  overlayStarted = true;

  const el = document.createElement('div');
  el.style.cssText =
    'position:fixed;left:8px;bottom:8px;z-index:999;pointer-events:none;' +
    'font:10px/1.5 monospace;color:#9fd7ff;background:rgba(10,16,24,0.72);' +
    'padding:4px 8px;border-radius:4px;white-space:pre;';
  el.textContent = 'render-stats: measuring…';
  document.body.appendChild(el);

  // Frame cadence from rAF deltas: EMA for the steady picture, worst-of-
  // window for the jank that EMAs hide.
  let last = performance.now();
  let emaMs = 16.7;
  let worstMs = 0;
  let frames = 0;
  const tick = (now: number) => {
    frameEpoch++;
    const dt = now - last;
    last = now;
    if (dt > 0 && dt < 1000) {
      emaMs = emaMs * 0.9 + dt * 0.1;
      if (dt > worstMs) worstMs = dt;
      frames++;
    }
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);

  try {
    const po = new PerformanceObserver((list) => {
      for (const e of list.getEntries()) {
        longTasks++;
        longTaskMs += e.duration;
      }
    });
    po.observe({ entryTypes: ['longtask'] });
  } catch {
    /* observer unsupported — lines read 0 */
  }

  window.setInterval(() => {
    const mb = uploadBytes / (1024 * 1024);
    const glPerFrame = glSpanFrames > 0 ? glSpanAccumMs / glSpanFrames : 0;
    el.textContent =
      `render ${frames} fps  ${emaMs.toFixed(1)} ms (worst ${worstMs.toFixed(0)} ms)\n` +
      `gl-span ${glPerFrame.toFixed(2)} ms/frame (${glSpanFrames} gl-frames/s)\n` +
      `longtask ${longTasks}/s  ${longTaskMs.toFixed(0)} ms/s\n` +
      `upload ${mb.toFixed(2)} MB/s  ${uploadCalls} calls/s\n` +
      `draws  ${drawCalls}/s`;
    uploadBytes = 0;
    uploadCalls = 0;
    drawCalls = 0;
    worstMs = 0;
    frames = 0;
    glSpanAccumMs = 0;
    glSpanFrames = 0;
    longTasks = 0;
    longTaskMs = 0;
  }, 1000);
}

const INSTRUMENTED = Symbol.for('zeus.renderStats.instrumented');

/** One line per renderer, where the context arrives. No-op unless
 *  ?renderstats=1 (or the localStorage flag) is set. Idempotent per
 *  context — shared contexts are wrapped once and counted together. */
export function instrumentGlForStats(gl: WebGL2RenderingContext): void {
  if (!enabled || !gl) return;
  const marker = gl as unknown as Record<symbol, boolean>;
  if (marker[INSTRUMENTED]) return;
  marker[INSTRUMENTED] = true;

  wrap(gl, 'texSubImage2D', (args) => {
    noteGlCall();
    uploadCalls++;
    uploadBytes += byteLengthOfLastView(args);
    spanEnd = performance.now();
  });
  wrap(gl, 'texImage2D', (args) => {
    noteGlCall();
    uploadCalls++;
    uploadBytes += byteLengthOfLastView(args);
    spanEnd = performance.now();
  });
  wrap(gl, 'drawArrays', () => {
    noteGlCall();
    drawCalls++;
    spanEnd = performance.now();
  });
  wrap(gl, 'drawElements', () => {
    noteGlCall();
    drawCalls++;
    spanEnd = performance.now();
  });

  startOverlay();
}
