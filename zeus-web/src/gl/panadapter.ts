// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { instrumentGlForStats } from './render-stats';
import { buildProgram } from './util';
import { lutFor, type ColormapId } from './colormap';
import {
  PAN_VS,
  PAN_FS,
  PAN_FILL_VS,
  PAN_FILL_FS,
  CURSOR_VS,
  CURSOR_FS,
  PHOS_QUAD_VS,
  PHOS_DECAY_FS,
  PHOS_SPLAT_FS,
  PHOS_DISPLAY_FS,
} from './shaders';

export type PanRenderer = {
  resize: (w: number, h: number) => void;
  draw: (
    panDb: Float32Array,
    dbMin: number,
    dbMax: number,
    offsetPx: number,
    scaleX?: number,
  ) => void;
  // Update the trace + fill colour. Components 0..1, premultiplied alpha is
  // applied inside draw via the FILL_ALPHA_TOP uniform; callers pass plain
  // RGB. No GL re-init — the next draw picks up the new uniform values.
  setTraceColor: (r: number, g: number, b: number) => void;
  /** Enables and tunes the high-contrast Signal Pop visual treatment. */
  setPopMode: (active: boolean, intensity?: number) => void;
  /** The waterfall palette the trace / fill / phosphor colour through. */
  setPalette: (id: ColormapId) => void;
  /**
   * Palette-coloured trace (Effects ≥ Subtle). mix 0 = the classic single
   * colour. wfDbMin/Max = the waterfall's window, so a bin takes the colour
   * the waterfall gives it.
   */
  setPaletteTrace: (mix: number, wfDbMin: number, wfDbMax: number) => void;
  /** Phosphor persistence (Effects: Full). Off releases nothing; it just stops. */
  setPhosphor: (on: boolean) => void;
  dispose: () => void;
};

// Phosphor tuning. Per NEW display frame (not per redraw): density decays by
// PHOS_DECAY then each pixel the trace crosses gains PHOS_HIT. At ~30 frames/s
// a burst fades over roughly a second; a steady carrier saturates.
export const PHOS_DECAY = 0.965;
export const PHOS_HIT = 0.09;
const PHOS_GAIN = 1.6;
// Linear-light floor so an 8-bit fallback target still decays to zero.
const PHOS_FLOOR_8BIT = 1.5 / 255;

/**
 * Whether accumulated persistence must be thrown away: the view moved (tune,
 * glide, zoom), the dB window changed, or the canvas resized — density is in
 * screen space, so old hits would sit on the wrong frequencies.
 */
export function phosphorNeedsReset(
  prev: { offsetPx: number; scaleX: number; dbMin: number; dbMax: number; w: number; h: number } | null,
  next: { offsetPx: number; scaleX: number; dbMin: number; dbMax: number; w: number; h: number },
): boolean {
  if (!prev) return true;
  return (
    Math.abs(prev.offsetPx - next.offsetPx) > 0.5 ||
    Math.abs(prev.scaleX - next.scaleX) > 1e-4 ||
    prev.dbMin !== next.dbMin ||
    prev.dbMax !== next.dbMax ||
    prev.w !== next.w ||
    prev.h !== next.h
  );
}

// Deferred WebGL context teardown — mirrors Waterfall.tsx (#629). Each WebGL2
// canvas holds a scarce ANGLE/driver context slot; freeing GL objects in
// dispose() does NOT release the context itself. On a workspace where the
// operator keeps rearranging panels, React can mount/unmount the panadapter
// repeatedly, and without an explicit WEBGL_lose_context call the orphaned
// contexts accumulate until the browser force-loses the oldest ones (the GPU
// context-loss spiral that contributes to the "blank workspace" symptom). We
// release the slot on real unmounts, but defer it briefly so React StrictMode's
// dev-only effect remount (and a quick drag-induced remount) can cancel the
// teardown and reuse the same canvas instead of tearing down a live context.
const CONTEXT_LOSS_TEARDOWN_DELAY_MS = 250;
const pendingContextLossTimers = new WeakMap<HTMLCanvasElement, number>();

// Cancel a pending deferred context loss for this canvas (call on (re)mount
// before creating the context). Returns nothing.
export function cancelPendingPanContextLoss(canvas: HTMLCanvasElement): void {
  const timer = pendingContextLossTimers.get(canvas);
  if (timer !== undefined) {
    window.clearTimeout(timer);
    pendingContextLossTimers.delete(canvas);
  }
}

// Schedule a deferred WEBGL_lose_context for this canvas (call on unmount,
// AFTER renderer.dispose()). Safe no-op if the extension is unavailable.
export function schedulePanContextLoss(
  canvas: HTMLCanvasElement,
  gl: WebGL2RenderingContext,
): void {
  const loseContext = gl.getExtension('WEBGL_lose_context');
  if (!loseContext) return;
  const timer = window.setTimeout(() => {
    if (pendingContextLossTimers.get(canvas) !== timer) return;
    pendingContextLossTimers.delete(canvas);
    loseContext.loseContext();
  }, CONTEXT_LOSS_TEARDOWN_DELAY_MS);
  pendingContextLossTimers.set(canvas, timer);
}

// Convert a #RRGGBB string into the 0..1 RGB triplet the renderer wants.
// Malformed input falls back to amber so a typo can never crash GL.
export function hexToRgbFloats(hex: string): { r: number; g: number; b: number } {
  const m = /^#([0-9A-Fa-f]{2})([0-9A-Fa-f]{2})([0-9A-Fa-f]{2})$/.exec(hex);
  if (!m || m[1] === undefined || m[2] === undefined || m[3] === undefined) {
    return { r: 1.0, g: 0.627, b: 0.157 };
  }
  return {
    r: parseInt(m[1], 16) / 255,
    g: parseInt(m[2], 16) / 255,
    b: parseInt(m[3], 16) / 255,
  };
}

// Fade at the trace edge; fragment alpha drops to 0 at the floor.
const FILL_ALPHA_TOP = 0.55;

export function createPanRenderer(gl: WebGL2RenderingContext): PanRenderer {
  instrumentGlForStats(gl);   // render meter: no-op without ?renderstats=1
  // Mutable trace colour, default amber. setTraceColor swaps these in place;
  // every draw reads the current values into the fill + line uniforms so the
  // operator's choice from the Display tab takes effect on the next frame.
  let traceR = 1.0;
  let traceG = 0.627;
  let traceB = 0.157;
  let popIntensity = 1;
  let canvasW = 0;
  let canvasH = 0;

  const traceProg = buildProgram(gl, PAN_VS, PAN_FS);
  const uTraceWidth = gl.getUniformLocation(traceProg, 'uWidth');
  const uTraceDbMin = gl.getUniformLocation(traceProg, 'uDbMin');
  const uTraceDbMax = gl.getUniformLocation(traceProg, 'uDbMax');
  const uTraceOffsetPx = gl.getUniformLocation(traceProg, 'uOffsetPx');
  const uTraceScaleX = gl.getUniformLocation(traceProg, 'uScaleX');
  const uTraceColor = gl.getUniformLocation(traceProg, 'uColor');
  const uTracePopIntensity = gl.getUniformLocation(traceProg, 'uPopIntensity');

  const fillProg = buildProgram(gl, PAN_FILL_VS, PAN_FILL_FS);
  const uFillWidth = gl.getUniformLocation(fillProg, 'uWidth');
  const uFillDbMin = gl.getUniformLocation(fillProg, 'uDbMin');
  const uFillDbMax = gl.getUniformLocation(fillProg, 'uDbMax');
  const uFillOffsetPx = gl.getUniformLocation(fillProg, 'uOffsetPx');
  const uFillScaleX = gl.getUniformLocation(fillProg, 'uScaleX');
  const uFillColor = gl.getUniformLocation(fillProg, 'uColor');
  const uFillAlphaTop = gl.getUniformLocation(fillProg, 'uFillAlphaTop');
  const uFillPan = gl.getUniformLocation(fillProg, 'uPan');
  const uFillPopIntensity = gl.getUniformLocation(fillProg, 'uPopIntensity');
  const uTraceWfMin = gl.getUniformLocation(traceProg, 'uWfDbMin');
  const uTraceWfMax = gl.getUniformLocation(traceProg, 'uWfDbMax');
  const uTraceLut = gl.getUniformLocation(traceProg, 'uLut');
  const uTraceLutMix = gl.getUniformLocation(traceProg, 'uLutMix');
  const uFillWfMin = gl.getUniformLocation(fillProg, 'uWfDbMin');
  const uFillWfMax = gl.getUniformLocation(fillProg, 'uWfDbMax');
  const uFillLut = gl.getUniformLocation(fillProg, 'uLut');
  const uFillLutMix = gl.getUniformLocation(fillProg, 'uLutMix');

  // Palette LUT on texture unit 1 (unit 0 is the pan row, unit 2 phosphor).
  const lutTex = gl.createTexture()!;
  let paletteId: ColormapId | null = null;
  let lutMix = 0;
  let wfDbMin = -140;
  let wfDbMax = -60;
  const uploadPalette = (id: ColormapId) => {
    if (id === paletteId) return;
    paletteId = id;
    gl.activeTexture(gl.TEXTURE1);
    gl.bindTexture(gl.TEXTURE_2D, lutTex);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, 256, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, lutFor(id));
    gl.activeTexture(gl.TEXTURE0);
  };
  uploadPalette('blue');

  // Phosphor persistence: built lazily on first use, so Off/Subtle pay nothing.
  let phosphorOn = false;
  type Phos = {
    quadVao: WebGLVertexArrayObject;
    quadVbo: WebGLBuffer;
    decayProg: WebGLProgram;
    splatProg: WebGLProgram;
    displayProg: WebGLProgram;
    tex: [WebGLTexture, WebGLTexture];
    fbo: [WebGLFramebuffer, WebGLFramebuffer];
    cur: 0 | 1;
    w: number;
    h: number;
    float: boolean;
    last: { offsetPx: number; scaleX: number; dbMin: number; dbMax: number; w: number; h: number } | null;
    u: {
      decayPrev: WebGLUniformLocation | null;
      decayDecay: WebGLUniformLocation | null;
      decayFloor: WebGLUniformLocation | null;
      splatHit: WebGLUniformLocation | null;
      splatWidth: WebGLUniformLocation | null;
      splatDbMin: WebGLUniformLocation | null;
      splatDbMax: WebGLUniformLocation | null;
      splatOffsetPx: WebGLUniformLocation | null;
      splatScaleX: WebGLUniformLocation | null;
      dispDensity: WebGLUniformLocation | null;
      dispGain: WebGLUniformLocation | null;
      dispLut: WebGLUniformLocation | null;
      dispLutMix: WebGLUniformLocation | null;
    };
  };
  let phos: Phos | null = null;
  const floatTargets = !!gl.getExtension('EXT_color_buffer_float');

  const allocPhosTargets = (p: Phos, w: number, h: number) => {
    // Phosphor textures live on unit 2 — never disturb the pan row on unit 0.
    gl.activeTexture(gl.TEXTURE2);
    for (let i = 0; i < 2; i++) {
      gl.bindTexture(gl.TEXTURE_2D, p.tex[i]!);
      if (p.float) gl.texImage2D(gl.TEXTURE_2D, 0, gl.R16F, w, h, 0, gl.RED, gl.HALF_FLOAT, null);
      else gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
      gl.bindFramebuffer(gl.FRAMEBUFFER, p.fbo[i]!);
      gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, p.tex[i]!, 0);
      if (p.float && gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) {
        // Half-float target refused by this driver — fall back to 8-bit.
        p.float = false;
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        allocPhosTargets(p, w, h);
        return;
      }
      gl.viewport(0, 0, w, h);
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
    }
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.activeTexture(gl.TEXTURE0);
    p.w = w;
    p.h = h;
    p.last = null;
  };

  const buildPhos = (): Phos => {
    const quadVao = gl.createVertexArray()!;
    const quadVbo = gl.createBuffer()!;
    gl.bindVertexArray(quadVao);
    gl.bindBuffer(gl.ARRAY_BUFFER, quadVbo);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
    gl.bindVertexArray(null);
    const decayProg = buildProgram(gl, PHOS_QUAD_VS, PHOS_DECAY_FS);
    const splatProg = buildProgram(gl, PAN_VS, PHOS_SPLAT_FS);
    const displayProg = buildProgram(gl, PHOS_QUAD_VS, PHOS_DISPLAY_FS);
    gl.activeTexture(gl.TEXTURE2);
    const mkTex = () => {
      const t = gl.createTexture()!;
      gl.bindTexture(gl.TEXTURE_2D, t);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      return t;
    };
    const p: Phos = {
      quadVao,
      quadVbo,
      decayProg,
      splatProg,
      displayProg,
      tex: [mkTex(), mkTex()],
      fbo: [gl.createFramebuffer()!, gl.createFramebuffer()!],
      cur: 0,
      w: 0,
      h: 0,
      float: floatTargets,
      last: null,
      u: {
        decayPrev: gl.getUniformLocation(decayProg, 'uPrev'),
        decayDecay: gl.getUniformLocation(decayProg, 'uDecay'),
        decayFloor: gl.getUniformLocation(decayProg, 'uFloor'),
        splatHit: gl.getUniformLocation(splatProg, 'uHit'),
        splatWidth: gl.getUniformLocation(splatProg, 'uWidth'),
        splatDbMin: gl.getUniformLocation(splatProg, 'uDbMin'),
        splatDbMax: gl.getUniformLocation(splatProg, 'uDbMax'),
        splatOffsetPx: gl.getUniformLocation(splatProg, 'uOffsetPx'),
        splatScaleX: gl.getUniformLocation(splatProg, 'uScaleX'),
        dispDensity: gl.getUniformLocation(displayProg, 'uDensity'),
        dispGain: gl.getUniformLocation(displayProg, 'uGain'),
        dispLut: gl.getUniformLocation(displayProg, 'uLut'),
        dispLutMix: gl.getUniformLocation(displayProg, 'uLutMix'),
      },
    };
    gl.activeTexture(gl.TEXTURE0);
    return p;
  };

  const disposePhos = (p: Phos) => {
    gl.deleteFramebuffer(p.fbo[0]);
    gl.deleteFramebuffer(p.fbo[1]);
    gl.deleteTexture(p.tex[0]);
    gl.deleteTexture(p.tex[1]);
    gl.deleteProgram(p.decayProg);
    gl.deleteProgram(p.splatProg);
    gl.deleteProgram(p.displayProg);
    gl.deleteBuffer(p.quadVbo);
    gl.deleteVertexArray(p.quadVao);
  };

  // Trace VBO: one float per bin, rendered as LINE_STRIP for the sharp
  // top edge. Fill reuses the same data via a 1-row R32F texture sampled
  // with `texelFetch(uPan, ivec2(gl_VertexID >> 1, 0))` so both verts of a
  // bin share one dB value without a CPU-side duplication pass.
  const traceVao = gl.createVertexArray()!;
  const traceVbo = gl.createBuffer()!;
  gl.bindVertexArray(traceVao);
  gl.bindBuffer(gl.ARRAY_BUFFER, traceVbo);
  let traceCapacity = 0;
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 1, gl.FLOAT, false, 0, 0);
  gl.bindVertexArray(null);

  // Attribute-less VAO for the fill draw; the shader derives position from
  // gl_VertexID and fetches dB from uPan.
  const fillVao = gl.createVertexArray()!;

  const panTex = gl.createTexture()!;
  gl.bindTexture(gl.TEXTURE_2D, panTex);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  let panTexWidth = 0;
  // Issue #597: dataDirty elision. During a view-center glide the SAME
  // adopted Float32Array is drawn many times with only the offset uniform
  // changing — re-uploading the texture + VBO each tick is pure waste.
  // Reference identity is the dirty signal: the component swaps the array
  // reference only when it adopts fresh data.
  let lastUploadedPan: Float32Array | null = null;

  const cursorProg = buildProgram(gl, CURSOR_VS, CURSOR_FS);
  const uCursorColor = gl.getUniformLocation(cursorProg, 'uColor');
  const cursorVao = gl.createVertexArray()!;
  const cursorVbo = gl.createBuffer()!;
  gl.bindVertexArray(cursorVao);
  gl.bindBuffer(gl.ARRAY_BUFFER, cursorVbo);
  gl.bufferData(
    gl.ARRAY_BUFFER,
    new Float32Array([0, -1, 0, 1]),
    gl.STATIC_DRAW,
  );
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
  gl.bindVertexArray(null);

  return {
    resize(w, h) {
      const nextW = Math.max(1, Math.floor(w));
      const nextH = Math.max(1, Math.floor(h));
      if (nextW === canvasW && nextH === canvasH) return;
      canvasW = nextW;
      canvasH = nextH;
      gl.viewport(0, 0, canvasW, canvasH);
    },
    draw(panDb, dbMin, dbMax, offsetPx, scaleX = 1) {
      // Draw-time zoom about the view centre (view-zoom.ts). 1.0 = no zoom;
      // mid-glide of a zoom packs/spreads the trace so it tracks the waterfall.
      const sx = Number.isFinite(scaleX) && scaleX > 0 ? scaleX : 1;
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);

      // Upload pan dB into the 1-row R32F texture. texImage2D re-allocates on
      // width change; texSubImage2D otherwise just streams the row. Skipped
      // entirely when this exact array was already uploaded (motion-only
      // redraw during a view-center glide — issue #597).
      const dataDirty = panDb !== lastUploadedPan || panDb.length !== panTexWidth;
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, panTex);
      if (dataDirty) {
        if (panDb.length !== panTexWidth) {
          gl.texImage2D(
            gl.TEXTURE_2D,
            0,
            gl.R32F,
            panDb.length,
            1,
            0,
            gl.RED,
            gl.FLOAT,
            panDb,
          );
          panTexWidth = panDb.length;
        } else {
          gl.texSubImage2D(
            gl.TEXTURE_2D,
            0,
            0,
            0,
            panDb.length,
            1,
            gl.RED,
            gl.FLOAT,
            panDb,
          );
        }
      }

      // Make sure the trace VBO holds this frame before any pass reads it.
      gl.bindBuffer(gl.ARRAY_BUFFER, traceVbo);
      const traceBytes = panDb.byteLength;
      if (traceBytes > traceCapacity) {
        gl.bufferData(gl.ARRAY_BUFFER, traceBytes, gl.STREAM_DRAW);
        traceCapacity = traceBytes;
        gl.bufferSubData(gl.ARRAY_BUFFER, 0, panDb);
      } else if (dataDirty) {
        gl.bufferSubData(gl.ARRAY_BUFFER, 0, panDb);
      }
      lastUploadedPan = panDb;

      // Palette LUT stays bound on unit 1 for every pass that reads it.
      gl.activeTexture(gl.TEXTURE1);
      gl.bindTexture(gl.TEXTURE_2D, lutTex);
      gl.activeTexture(gl.TEXTURE0);

      const phosphorActive = phosphorOn && canvasW > 1 && canvasH > 1;
      if (phosphorActive) {
        phos ??= buildPhos();
        const p = phos;
        const pw = Math.max(1, Math.floor(canvasW / 2));
        const ph = Math.max(1, Math.floor(canvasH / 2));
        if (p.w !== pw || p.h !== ph) allocPhosTargets(p, pw, ph);
        const geom = { offsetPx, scaleX: sx, dbMin, dbMax, w: pw, h: ph };
        if (phosphorNeedsReset(p.last, geom)) {
          for (let i = 0; i < 2; i++) {
            gl.bindFramebuffer(gl.FRAMEBUFFER, p.fbo[i]!);
            gl.viewport(0, 0, pw, ph);
            gl.clearColor(0, 0, 0, 0);
            gl.clear(gl.COLOR_BUFFER_BIT);
          }
        }
        p.last = geom;
        if (dataDirty) {
          const src = p.cur;
          const dst: 0 | 1 = src === 0 ? 1 : 0;
          gl.bindFramebuffer(gl.FRAMEBUFFER, p.fbo[dst]);
          gl.viewport(0, 0, pw, ph);
          // Decay src → dst.
          gl.disable(gl.BLEND);
          gl.useProgram(p.decayProg);
          gl.activeTexture(gl.TEXTURE2);
          gl.bindTexture(gl.TEXTURE_2D, p.tex[src]);
          gl.uniform1i(p.u.decayPrev, 2);
          gl.uniform1f(p.u.decayDecay, PHOS_DECAY);
          gl.uniform1f(p.u.decayFloor, p.float ? 0.0005 : PHOS_FLOOR_8BIT);
          gl.bindVertexArray(p.quadVao);
          gl.drawArrays(gl.TRIANGLES, 0, 3);
          // Splat the new trace, additively.
          gl.enable(gl.BLEND);
          gl.blendFunc(gl.ONE, gl.ONE);
          gl.useProgram(p.splatProg);
          gl.bindVertexArray(traceVao);
          gl.uniform1f(p.u.splatHit, PHOS_HIT);
          gl.uniform1f(p.u.splatWidth, panDb.length);
          gl.uniform1f(p.u.splatDbMin, dbMin);
          gl.uniform1f(p.u.splatDbMax, dbMax);
          gl.uniform1f(p.u.splatOffsetPx, offsetPx);
          gl.uniform1f(p.u.splatScaleX, sx);
          gl.drawArrays(gl.LINE_STRIP, 0, panDb.length);
          p.cur = dst;
          gl.activeTexture(gl.TEXTURE0);
        }
        // Display the density under everything else.
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        gl.viewport(0, 0, canvasW, canvasH);
        gl.enable(gl.BLEND);
        gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
        gl.useProgram(p.displayProg);
        gl.activeTexture(gl.TEXTURE2);
        gl.bindTexture(gl.TEXTURE_2D, p.tex[p.cur]);
        gl.activeTexture(gl.TEXTURE0);
        gl.uniform1i(p.u.dispDensity, 2);
        gl.uniform1i(p.u.dispLut, 1);
        gl.uniform1f(p.u.dispGain, PHOS_GAIN);
        gl.uniform1f(p.u.dispLutMix, 1);
        gl.bindVertexArray(p.quadVao);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, panTex);
      } else if (phos) {
        phos.last = null;
      }

      // Fill (premultiplied alpha to avoid halo on the glowing top edge).
      // Under phosphor the density IS the fill — drawing both muddies it.
      gl.enable(gl.BLEND);
      gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
      gl.useProgram(fillProg);
      gl.bindVertexArray(fillVao);
      gl.uniform1i(uFillPan, 0);
      gl.uniform1f(uFillWidth, panDb.length);
      gl.uniform1f(uFillDbMin, dbMin);
      gl.uniform1f(uFillDbMax, dbMax);
      gl.uniform1f(uFillOffsetPx, offsetPx);
      gl.uniform1f(uFillScaleX, sx);
      gl.uniform3f(uFillColor, traceR, traceG, traceB);
      gl.uniform1f(uFillAlphaTop, FILL_ALPHA_TOP);
      gl.uniform1f(uFillPopIntensity, popIntensity);
      gl.uniform1i(uFillLut, 1);
      gl.uniform1f(uFillLutMix, popIntensity > 0 ? 0 : lutMix);
      gl.uniform1f(uFillWfMin, wfDbMin);
      gl.uniform1f(uFillWfMax, wfDbMax);
      if (!phosphorActive) gl.drawArrays(gl.TRIANGLE_STRIP, 0, panDb.length * 2);

      // Sharp trace line on top.
      gl.disable(gl.BLEND);
      gl.useProgram(traceProg);
      gl.bindVertexArray(traceVao);
      gl.uniform1f(uTraceWidth, panDb.length);
      gl.uniform1f(uTraceDbMin, dbMin);
      gl.uniform1f(uTraceDbMax, dbMax);
      gl.uniform1f(uTraceOffsetPx, offsetPx);
      gl.uniform1f(uTraceScaleX, sx);
      gl.uniform3f(uTraceColor, traceR, traceG, traceB);
      gl.uniform1f(uTracePopIntensity, popIntensity);
      gl.uniform1i(uTraceLut, 1);
      gl.uniform1f(uTraceLutMix, popIntensity > 0 ? 0 : lutMix);
      gl.uniform1f(uTraceWfMin, wfDbMin);
      gl.uniform1f(uTraceWfMax, wfDbMax);
      gl.drawArrays(gl.LINE_STRIP, 0, panDb.length);

      gl.useProgram(cursorProg);
      gl.bindVertexArray(cursorVao);
      gl.uniform3f(uCursorColor, 0.96, 0.74, 0.18);
      gl.drawArrays(gl.LINES, 0, 2);

      gl.bindVertexArray(null);
    },
    setTraceColor(r, g, b) {
      traceR = r;
      traceG = g;
      traceB = b;
    },
    setPopMode(active, intensity = active ? 1 : 0) {
      popIntensity = Math.max(0, Math.min(1, intensity));
    },
    setPalette(id) {
      uploadPalette(id);
    },
    setPaletteTrace(mix, min, max) {
      lutMix = Number.isFinite(mix) ? Math.max(0, Math.min(1, mix)) : 0;
      if (Number.isFinite(min) && Number.isFinite(max) && max > min) {
        wfDbMin = min;
        wfDbMax = max;
      }
    },
    setPhosphor(on) {
      phosphorOn = on;
    },
    dispose() {
      gl.deleteBuffer(traceVbo);
      gl.deleteBuffer(cursorVbo);
      gl.deleteTexture(panTex);
      gl.deleteTexture(lutTex);
      if (phos) disposePhos(phos);
      phos = null;
      gl.deleteVertexArray(traceVao);
      gl.deleteVertexArray(fillVao);
      gl.deleteVertexArray(cursorVao);
      gl.deleteProgram(traceProg);
      gl.deleteProgram(fillProg);
      gl.deleteProgram(cursorProg);
    },
  };
}
