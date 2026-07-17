// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// WebGPU 3D panadapter surface.
//
// This is a clean-room stacked-spectrum renderer: one rolling dB texture, one
// per-row capture-geometry buffer, and procedural fill/ridge meshes. Unlike a
// CPU-reprojected history, every row maps itself into the current animated view
// in shader space, so multi-RX panes, CTUN slides, retunes, and zoom glides stay
// frequency coherent without rewriting old rows.

import { lutFor, type RenderColormapId } from '../colormap';

const HISTORY_ROWS = 512;
const MAX_TEXTURE_COLUMNS = 1024;
const MESH_COLUMNS = 640;
const VISIBLE_ROWS = 160;
const UNIFORM_FLOATS = 28;
const LARGE_CENTER_SHIFT_HZ = 25_000_000;

export type PanSurfaceRowDomain = 'rx' | 'pop' | 'tx';

export type PanSurfaceDbWindows = {
  rxDbMin: number;
  rxDbMax: number;
  txDbMin: number;
  txDbMax: number;
};

export type PanSurfaceRenderer = {
  pushRow: (
    rowDb: Float32Array,
    centerHz: number,
    hzPerPixel: number,
    domain?: PanSurfaceRowDomain,
  ) => void;
  draw: (
    dbMin: number,
    dbMax: number,
    viewCenterHz?: number | null,
    viewHzPerPixel?: number | null,
    windows?: PanSurfaceDbWindows,
  ) => void;
  resize: (w: number, h: number) => void;
  setColormap: (id: RenderColormapId) => void;
  setTraceColor: (r: number, g: number, b: number) => void;
  setReliefDepth: (depth: number) => void;
  setPopGlow: (intensity: number) => void;
  clearHistory: () => void;
  debugState: () => {
    texWidth: number;
    writeRow: number;
    validRows: number;
    baseCenterHz: number | null;
  };
  dispose: () => void;
};

function gpuSrc(view: Float32Array | Uint8Array): GPUAllowSharedBufferSource {
  return view as unknown as GPUAllowSharedBufferSource;
}

function lutTextureBytes(id: RenderColormapId): Uint8Array {
  const lut = lutFor(id);
  if (lut.length !== 256 * 4) throw new Error(`colormap LUT must be 1024 bytes, got ${lut.length}`);
  return lut;
}

function chooseTextureWidth(sourceWidth: number): number {
  if (sourceWidth >= MAX_TEXTURE_COLUMNS) return MAX_TEXTURE_COLUMNS;
  if (sourceWidth >= 512) return 512;
  if (sourceWidth >= 256) return 256;
  return 128;
}

function resamplePeakPreserving(source: Float32Array, target: Float32Array): Float32Array {
  const srcW = source.length;
  const dstW = target.length;
  if (srcW === dstW) {
    target.set(source);
    return target;
  }

  const scale = srcW / dstW;
  for (let x = 0; x < dstW; x++) {
    const start = Math.max(0, Math.floor(x * scale));
    const end = Math.min(srcW, Math.max(start + 1, Math.ceil((x + 1) * scale)));
    let peak = -300;
    let sum = 0;
    let count = 0;
    for (let i = start; i < end; i++) {
      const v = source[i] ?? Number.NaN;
      if (!Number.isFinite(v)) continue;
      peak = Math.max(peak, v);
      sum += v;
      count += 1;
    }
    if (count === 0) {
      target[x] = -300;
      continue;
    }
    const avg = sum / count;
    target[x] = peak > avg + 7 ? peak : avg * 0.35 + peak * 0.65;
  }
  return target;
}

function rowDomainCode(domain: PanSurfaceRowDomain | undefined): number {
  switch (domain) {
    case 'pop':
      return 1;
    case 'tx':
      return 2;
    default:
      return 0;
  }
}

export function panSurfaceViewSpanHzForTest(
  viewHzPerPixel: number,
  sourceWidth: number,
  textureWidth: number,
): number {
  return viewHzPerPixel * Math.max(1, sourceWidth || textureWidth);
}

export function panSurfaceSourceUForTest(
  freqU: number,
  viewCenterOffsetHz: number,
  viewSpanHz: number,
  rowCenterOffsetHz: number,
  rowHzPerPixel: number,
  textureWidth: number,
): number {
  const rowSpanHz = rowHzPerPixel * textureWidth;
  return (
    0.5 +
    (viewCenterOffsetHz - rowCenterOffsetHz) / rowSpanHz +
    (freqU - 0.5) * (viewSpanHz / rowSpanHz)
  );
}

const SHADER = /* wgsl */ `
struct Uniforms {
  p0 : vec4<f32>, // writeRow, historyRows, texW, validRows
  p1 : vec4<f32>, // rxDbMin, rxDbMax, meshCols, drawRows
  p2 : vec4<f32>, // viewCenterOffsetHz, viewSpanHz, frontY, backY
  p3 : vec4<f32>, // reserved, heightGain, zCurve, reliefDepth
  p4 : vec4<f32>, // traceColor.rgb, popGlow
  p5 : vec4<f32>, // canvasW, canvasH, lineAlpha, _
  p6 : vec4<f32>, // txDbMin, txDbMax, popDbMin, popDbMax
};
@group(0) @binding(0) var<uniform> u : Uniforms;
@group(0) @binding(1) var historyTex : texture_2d<f32>;
@group(0) @binding(2) var lutTex : texture_2d<f32>;
@group(0) @binding(3) var lutSampler : sampler;
@group(0) @binding(4) var<storage, read> rowGeom : array<vec4<f32>>;

fn ringRow(age : f32) -> i32 {
  let h = u.p0.y;
  let r = ((u.p0.x - age) % h + h) % h;
  return i32(r);
}

fn rawLevel(freqU : f32, age : f32) -> f32 {
  if (age < 0.0 || age > u.p0.w - 1.0) { return 0.0; }
  let row = ringRow(age);
  let g = rowGeom[row];
  if (g.y <= 0.0) { return 0.0; }
  let texW = u.p0.z;
  let rowSpanHz = g.y * texW;
  let srcX = 0.5 + (u.p2.x - g.x) / rowSpanHz + (freqU - 0.5) * (u.p2.y / rowSpanHz);
  if (srcX < 0.0 || srcX > 1.0) { return 0.0; }
  let texX = clamp(i32(round(srcX * (texW - 1.0))), 0, i32(texW) - 1);
  let db = textureLoad(historyTex, vec2<i32>(texX, row), 0).r;
  var dbMin = u.p1.x;
  var dbMax = u.p1.y;
  if (g.z > 1.5) {
    dbMin = u.p6.x;
    dbMax = u.p6.y;
  } else if (g.z > 0.5) {
    dbMin = u.p6.z;
    dbMax = u.p6.w;
  }
  return clamp((db - dbMin) / max(0.0001, dbMax - dbMin), 0.0, 1.0);
}

fn rowDomain(age : f32) -> f32 {
  if (age < 0.0 || age > u.p0.w - 1.0) { return 0.0; }
  let row = ringRow(age);
  return rowGeom[row].z;
}

struct Sample {
  level : f32,
  light : f32,
  crest : f32,
};

fn sampleSurface(freqU : f32, age : f32) -> Sample {
  let level = rawLevel(freqU, age);
  let dx = 1.0 / max(8.0, u.p1.z);
  let lx = rawLevel(freqU - dx, age);
  let rx = rawLevel(freqU + dx, age);
  let newer = rawLevel(freqU, max(0.0, age - 1.0));
  let older = rawLevel(freqU, age + 1.0);
  let normal = normalize(vec3<f32>((lx - rx) * 7.5, 1.0, (newer - older) * 4.5));
  let lightDir = normalize(vec3<f32>(-0.55, 0.82, -0.42));
  let lambert = clamp(dot(normal, lightDir) * 0.5 + 0.5, 0.0, 1.0);
  let cross = (lx + rx + newer + older) * 0.25;
  var s : Sample;
  s.level = level;
  s.light = mix(0.52, 1.48, pow(lambert, mix(1.1, 1.85, u.p3.w)));
  s.crest = max(0.0, level - cross);
  return s;
}

fn project(freqU : f32, age : f32, level : f32) -> vec4<f32> {
  let drawRows = max(1.0, u.p1.w);
  let depth = clamp(age / max(1.0, drawRows - 1.0), 0.0, 1.0);
  let curveDepth = pow(depth, 0.82);
  // X is deliberately frequency-linear at every depth. Perspective width
  // compression looks dramatic, but it makes older rows drift inward and breaks
  // registration with the waterfall below. Depth comes from Y, relief, haze,
  // and ridge lighting; frequency stays exact for RX and TX rows.
  let x = (freqU - 0.5) * 2.0;
  let baseY = mix(u.p2.z, u.p2.w, curveDepth);
  let gain = u.p3.y * mix(1.0, 0.55, curveDepth);
  let y = baseY + pow(level, u.p3.z) * gain;
  return vec4<f32>(x, y, depth * 0.55, 1.0);
}

struct VsOut {
  @builtin(position) clip : vec4<f32>,
  @location(0) level : f32,
  @location(1) depth : f32,
  @location(2) light : f32,
  @location(3) crest : f32,
  @location(4) domain : f32,
};

@vertex
fn vsFill(@builtin(vertex_index) vi : u32) -> VsOut {
  let segs = max(1u, u32(u.p1.z) - 1u);
  let cell = vi / 6u;
  let tri = vi % 6u;
  let band = cell / segs;
  let seg = cell - band * segs;
  var corners = array<vec2<f32>, 6>(
    vec2<f32>(0.0, 0.0),
    vec2<f32>(1.0, 0.0),
    vec2<f32>(0.0, 1.0),
    vec2<f32>(0.0, 1.0),
    vec2<f32>(1.0, 0.0),
    vec2<f32>(1.0, 1.0)
  );
  let c = corners[tri];
  let freqU = (f32(seg) + c.x) / f32(segs);
  let backAge = u.p1.w - 1.0 - f32(band);
  let frontAge = backAge - 1.0;
  let age = mix(backAge, frontAge, c.y);
  let s = sampleSurface(freqU, age);
  var out : VsOut;
  out.clip = project(freqU, age, s.level);
  out.level = s.level;
  out.depth = clamp(age / max(1.0, u.p1.w - 1.0), 0.0, 1.0);
  out.light = s.light;
  out.crest = s.crest;
  out.domain = rowDomain(age);
  return out;
}

@vertex
fn vsLine(@builtin(vertex_index) vi : u32) -> VsOut {
  let segs = max(1u, u32(u.p1.z) - 1u);
  let pair = vi / 2u;
  let end = vi % 2u;
  let row = pair / segs;
  let seg = pair - row * segs;
  let freqU = (f32(seg) + f32(end)) / f32(segs);
  let age = u.p1.w - 1.0 - f32(row);
  let s = sampleSurface(freqU, age);
  var out : VsOut;
  out.clip = project(freqU, age, s.level);
  out.level = s.level;
  out.depth = clamp(age / max(1.0, u.p1.w - 1.0), 0.0, 1.0);
  out.light = s.light;
  out.crest = s.crest;
  out.domain = rowDomain(age);
  return out;
}

@fragment
fn fsFill(in : VsOut) -> @location(0) vec4<f32> {
  let lvl = clamp(in.level * 0.98 + in.crest * 0.55 * u.p4.w, 0.0, 1.0);
  var col = textureSample(lutTex, lutSampler, vec2<f32>(lvl, 0.5)).rgb * in.light;
  let txRow = smoothstep(1.5, 2.0, in.domain);
  col = mix(col, col * vec3<f32>(1.22, 0.78, 0.70) + vec3<f32>(0.12, 0.02, 0.01), txRow * smoothstep(0.10, 0.85, in.level));
  let horizon = vec3<f32>(0.015, 0.045, 0.09);
  col = mix(col, horizon, in.depth * 0.42);
  let glow = smoothstep(0.42, 0.95, in.level) * (0.18 + u.p4.w * 0.28) + in.crest * u.p4.w * 0.75;
  col += vec3<f32>(0.35, 0.68, 1.0) * glow * (1.0 - in.depth * 0.45);
  let alpha = mix(0.58, 0.93, smoothstep(0.04, 0.82, in.level)) * (1.0 - in.depth * 0.28);
  return vec4<f32>(min(col, vec3<f32>(1.0)), alpha);
}

@fragment
fn fsLine(in : VsOut) -> @location(0) vec4<f32> {
  let whiteLift = smoothstep(0.74, 1.0, in.level) * 0.55;
  let txRow = smoothstep(1.5, 2.0, in.domain);
  let trace = mix(u.p4.rgb, vec3<f32>(1.0, 0.22, 0.16), txRow);
  let col = mix(trace, vec3<f32>(1.0), whiteLift);
  let alpha = (0.10 + smoothstep(0.02, 0.90, in.level) * 0.88) * (1.0 - in.depth * 0.32) * u.p5.z;
  return vec4<f32>(col, alpha);
}
`;

export function createPanSurfaceRenderer(
  device: GPUDevice,
  context: GPUCanvasContext,
  format: GPUTextureFormat,
): PanSurfaceRenderer {
  context.configure({ device, format, alphaMode: 'premultiplied' });

  const module = device.createShaderModule({ code: SHADER });
  const uniformBuffer = device.createBuffer({
    size: UNIFORM_FLOATS * 4,
    usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
  });
  const uniformData = new Float32Array(UNIFORM_FLOATS);
  const lutSampler = device.createSampler({ magFilter: 'linear', minFilter: 'linear' });

  const bindGroupLayout = device.createBindGroupLayout({
    entries: [
      {
        binding: 0,
        visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT,
        buffer: { type: 'uniform' },
      },
      { binding: 1, visibility: GPUShaderStage.VERTEX, texture: { sampleType: 'unfilterable-float' } },
      { binding: 2, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float' } },
      { binding: 3, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'filtering' } },
      { binding: 4, visibility: GPUShaderStage.VERTEX, buffer: { type: 'read-only-storage' } },
    ],
  });
  const pipelineLayout = device.createPipelineLayout({ bindGroupLayouts: [bindGroupLayout] });
  const blend: GPUBlendState = {
    color: { srcFactor: 'src-alpha', dstFactor: 'one-minus-src-alpha', operation: 'add' },
    alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha', operation: 'add' },
  };

  const fillPipeline = device.createRenderPipeline({
    layout: pipelineLayout,
    vertex: { module, entryPoint: 'vsFill' },
    fragment: { module, entryPoint: 'fsFill', targets: [{ format, blend }] },
    primitive: { topology: 'triangle-list' },
  });
  const linePipeline = device.createRenderPipeline({
    layout: pipelineLayout,
    vertex: { module, entryPoint: 'vsLine' },
    fragment: { module, entryPoint: 'fsLine', targets: [{ format, blend }] },
    primitive: { topology: 'line-list' },
  });

  const lutTexture = device.createTexture({
    size: [256, 1, 1],
    format: 'rgba8unorm',
    usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST,
  });
  const uploadLut = (id: RenderColormapId) => {
    device.queue.writeTexture(
      { texture: lutTexture },
      gpuSrc(lutTextureBytes(id)),
      { bytesPerRow: 256 * 4, rowsPerImage: 1 },
      { width: 256, height: 1, depthOrArrayLayers: 1 },
    );
  };
  uploadLut('blue');

  const rowGeomBuffer = device.createBuffer({
    size: HISTORY_ROWS * 4 * 4,
    usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
  });
  const rowGeomScratch = new Float32Array(4);
  const rowGeomZero = new Float32Array(HISTORY_ROWS * 4);

  let texWidth = 0;
  let writeRow = HISTORY_ROWS - 1;
  let validRows = 0;
  let canvasW = 1;
  let canvasH = 1;
  let reliefDepth = 0.74;
  let popGlow = 0;
  let traceR = 1;
  let traceG = 0.62;
  let traceB = 0.16;
  let baseCenterHz: number | null = null;
  let anchorCenterHz = 0;
  let anchorHzPerPixel = 0;
  let anchorSourceWidth = 0;
  let uploadScratch = new Float32Array(MAX_TEXTURE_COLUMNS);
  let historyTexture: GPUTexture | null = null;
  let bindGroup: GPUBindGroup | null = null;

  const rebuildBindGroup = () => {
    if (!historyTexture) return;
    bindGroup = device.createBindGroup({
      layout: bindGroupLayout,
      entries: [
        { binding: 0, resource: { buffer: uniformBuffer } },
        { binding: 1, resource: historyTexture.createView() },
        { binding: 2, resource: lutTexture.createView() },
        { binding: 3, resource: lutSampler },
        { binding: 4, resource: { buffer: rowGeomBuffer } },
      ],
    });
  };

  const allocHistory = (width: number) => {
    historyTexture?.destroy();
    historyTexture = device.createTexture({
      size: [width, HISTORY_ROWS, 1],
      format: 'r32float',
      usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST,
    });
    device.queue.writeBuffer(rowGeomBuffer, 0, gpuSrc(rowGeomZero));
    texWidth = width;
    writeRow = HISTORY_ROWS - 1;
    validRows = 0;
    baseCenterHz = null;
    anchorSourceWidth = 0;
    rebuildBindGroup();
  };

  const clearHistoryInternal = () => {
    writeRow = HISTORY_ROWS - 1;
    validRows = 0;
    baseCenterHz = null;
    anchorSourceWidth = 0;
    device.queue.writeBuffer(rowGeomBuffer, 0, gpuSrc(rowGeomZero));
  };

  const writeUniforms = (
    viewCenterHz: number,
    viewHzPerPixel: number,
    drawRows: number,
    meshCols: number,
    windows: PanSurfaceDbWindows,
  ) => {
    const base = baseCenterHz ?? viewCenterHz;
    const centerOffset = viewCenterHz - base;
    const viewSpanHz = panSurfaceViewSpanHzForTest(viewHzPerPixel, anchorSourceWidth, texWidth);
    const usableHeight = Math.max(80, canvasH);
    const frontY = -0.88;
    const backY = Math.max(-0.08, Math.min(0.34, -0.02 + usableHeight / 900));
    const heightGain = Math.max(0.30, Math.min(0.76, 0.42 + usableHeight / 1000)) * (0.72 + reliefDepth * 0.48);

    uniformData[0] = writeRow;
    uniformData[1] = HISTORY_ROWS;
    uniformData[2] = texWidth;
    uniformData[3] = validRows;
    uniformData[4] = windows.rxDbMin;
    uniformData[5] = windows.rxDbMax;
    uniformData[6] = meshCols;
    uniformData[7] = drawRows;
    uniformData[8] = centerOffset;
    uniformData[9] = viewSpanHz;
    uniformData[10] = frontY;
    uniformData[11] = backY;
    uniformData[12] = 0;
    uniformData[13] = heightGain;
    uniformData[14] = 0.66;
    uniformData[15] = reliefDepth;
    uniformData[16] = traceR;
    uniformData[17] = traceG;
    uniformData[18] = traceB;
    uniformData[19] = popGlow;
    uniformData[20] = canvasW;
    uniformData[21] = canvasH;
    uniformData[22] = 0.86;
    uniformData[23] = 0;
    uniformData[24] = windows.txDbMin;
    uniformData[25] = windows.txDbMax;
    uniformData[26] = 0;
    uniformData[27] = 1;
    device.queue.writeBuffer(uniformBuffer, 0, gpuSrc(uniformData));
  };

  return {
    pushRow(rowDb, centerHz, hzPerPixel, domain = 'rx') {
      if (rowDb.length === 0) return;
      const targetWidth = chooseTextureWidth(rowDb.length);
      if (texWidth !== targetWidth) allocHistory(targetWidth);
      if (!historyTexture) return;

      if (
        baseCenterHz !== null &&
        Number.isFinite(centerHz) &&
        Math.abs(centerHz - baseCenterHz) > LARGE_CENTER_SHIFT_HZ
      ) {
        clearHistoryInternal();
      }
      if (baseCenterHz === null && Number.isFinite(centerHz)) {
        baseCenterHz = centerHz;
      }

      if (uploadScratch.length !== targetWidth) uploadScratch = new Float32Array(targetWidth);
      const uploadRow = resamplePeakPreserving(rowDb, uploadScratch);
      const rowHzPerPixel =
        Number.isFinite(hzPerPixel) && hzPerPixel > 0
          ? (rowDb.length * hzPerPixel) / targetWidth
          : 0;

      anchorCenterHz = Number.isFinite(centerHz) ? centerHz : anchorCenterHz;
      anchorSourceWidth = rowDb.length;
      if (Number.isFinite(hzPerPixel) && hzPerPixel > 0) anchorHzPerPixel = hzPerPixel;
      writeRow = (writeRow + 1) % HISTORY_ROWS;
      device.queue.writeTexture(
        { texture: historyTexture, origin: { x: 0, y: writeRow, z: 0 } },
        gpuSrc(uploadRow),
        { bytesPerRow: targetWidth * 4, rowsPerImage: 1 },
        { width: targetWidth, height: 1, depthOrArrayLayers: 1 },
      );
      rowGeomScratch[0] =
        baseCenterHz !== null && Number.isFinite(centerHz) ? centerHz - baseCenterHz : 0;
      rowGeomScratch[1] = rowHzPerPixel;
      rowGeomScratch[2] = rowDomainCode(domain);
      rowGeomScratch[3] = 0;
      device.queue.writeBuffer(rowGeomBuffer, writeRow * 4 * 4, gpuSrc(rowGeomScratch));
      validRows = Math.min(HISTORY_ROWS, validRows + 1);
    },
    draw(
      dbMin,
      dbMax,
      viewCenterHz = null,
      viewHzPerPixel = null,
      windows?: PanSurfaceDbWindows,
    ) {
      if (!bindGroup || !historyTexture || texWidth <= 0 || validRows <= 0) return;
      const vCenter =
        viewCenterHz !== null && Number.isFinite(viewCenterHz) ? viewCenterHz : anchorCenterHz;
      const vHz =
        viewHzPerPixel !== null && Number.isFinite(viewHzPerPixel) && viewHzPerPixel > 0
          ? viewHzPerPixel
          : anchorHzPerPixel;
      if (!Number.isFinite(vCenter) || !(vHz > 0)) return;

      const adaptiveRows = Math.max(72, Math.min(VISIBLE_ROWS, Math.floor(canvasH * 0.82)));
      const adaptiveCols = Math.max(256, Math.min(MESH_COLUMNS, Math.floor(canvasW * 0.72)));
      const drawRows = Math.max(1, Math.min(adaptiveRows, validRows));
      const meshCols = Math.max(2, Math.min(adaptiveCols, texWidth));
      writeUniforms(vCenter, vHz, drawRows, meshCols, windows ?? {
        rxDbMin: dbMin,
        rxDbMax: dbMax,
        txDbMin: dbMin,
        txDbMax: dbMax,
      });

      const encoder = device.createCommandEncoder();
      const pass = encoder.beginRenderPass({
        colorAttachments: [
          {
            view: context.getCurrentTexture().createView(),
            clearValue: { r: 0, g: 0, b: 0, a: 0 },
            loadOp: 'clear',
            storeOp: 'store',
          },
        ],
      });
      pass.setBindGroup(0, bindGroup);
      if (drawRows >= 2) {
        pass.setPipeline(fillPipeline);
        pass.draw((drawRows - 1) * (meshCols - 1) * 6);
      }
      pass.setPipeline(linePipeline);
      pass.draw(drawRows * (meshCols - 1) * 2);
      pass.end();
      device.queue.submit([encoder.finish()]);
    },
    resize(w, h) {
      const nextW = Math.max(1, Math.floor(w));
      const nextH = Math.max(1, Math.floor(h));
      if (nextW === canvasW && nextH === canvasH) return;
      canvasW = nextW;
      canvasH = nextH;
    },
    setColormap(id) {
      uploadLut(id);
    },
    setTraceColor(r, g, b) {
      traceR = Math.max(0, Math.min(1, r));
      traceG = Math.max(0, Math.min(1, g));
      traceB = Math.max(0, Math.min(1, b));
    },
    setReliefDepth(depth) {
      reliefDepth = Number.isFinite(depth) ? Math.max(0, Math.min(1, depth)) : reliefDepth;
    },
    setPopGlow(intensity) {
      popGlow = Number.isFinite(intensity) ? Math.max(0, Math.min(1, intensity)) : 0;
    },
    clearHistory() {
      if (texWidth <= 0) return;
      clearHistoryInternal();
    },
    debugState() {
      return { texWidth, writeRow, validRows, baseCenterHz };
    },
    dispose() {
      historyTexture?.destroy();
      lutTexture.destroy();
      uniformBuffer.destroy();
      rowGeomBuffer.destroy();
    },
  };
}
