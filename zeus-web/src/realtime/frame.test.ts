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

import { describe, expect, it } from 'vitest';
import {
  HEADER_BYTES,
  BODY_FIXED_BYTES,
  MSG_TYPE_DISPLAY_FRAME,
  QUANT_MIN_DB,
  DEQUANT_STEP_DB,
  decodeDisplayFrame,
  encodeDisplayFrame,
  FrameDecodeError,
} from './frame';

function sampleFrame(width: number) {
  const panDb = new Float32Array(width);
  const wfDb = new Float32Array(width);
  for (let i = 0; i < width; i++) {
    panDb[i] = -80 + (i % 32);
    wfDb[i] = -90 + (i % 16);
  }
  return {
    seq: 42,
    tsUnixMs: 1_700_000_000_123.5,
    rxId: 0,
    bodyFlags: 0x03,
    width,
    centerHz: 14_074_000n,
    hzPerPixel: 192_000 / width,
    panDb,
    wfDb,
  };
}

// Hand-build a u8-quantized wire frame (server SerializeU8 layout) so the
// decoder's u8 branch is exercised without an encoder that emits u8.
function u8WireFrame(width: number, panU8: number[], wfU8: number[]): ArrayBuffer {
  const bodyLen = BODY_FIXED_BYTES + width * 2;
  const buf = new ArrayBuffer(HEADER_BYTES + bodyLen);
  const dv = new DataView(buf);
  dv.setUint8(0, MSG_TYPE_DISPLAY_FRAME);
  dv.setUint8(1, 1); // headerFlags
  dv.setUint16(2, bodyLen, true);
  dv.setUint32(4, 99, true); // seq
  dv.setFloat64(8, 123.5, true); // ts
  const b = HEADER_BYTES;
  dv.setUint8(b + 0, 0); // rxId
  dv.setUint8(b + 1, 0x01 | 0x02 | 0x04); // PanValid | WfValid | BinsU8
  dv.setUint16(b + 2, width, true);
  dv.setBigInt64(b + 4, 14_200_000n, true);
  dv.setFloat32(b + 12, 192_000 / width, true);
  const u8 = new Uint8Array(buf, b + BODY_FIXED_BYTES, width * 2);
  for (let i = 0; i < width; i++) u8[i] = panU8[i] ?? 0;
  for (let i = 0; i < width; i++) u8[width + i] = wfU8[i] ?? 0;
  return buf;
}

describe('decodeDisplayFrame u8 bins', () => {
  it('expands u8-quantized bins to float dB', () => {
    const width = 8;
    const panU8 = [0, 32, 64, 128, 191, 200, 255, 100];
    const wfU8 = [255, 200, 128, 64, 32, 10, 0, 50];
    const dec = decodeDisplayFrame(u8WireFrame(width, panU8, wfU8));

    expect(dec.binsU8).toBe(true);
    expect(dec.width).toBe(width);
    expect(dec.panDb.length).toBe(width);
    // Byte 0 → floor, byte 255 → near ceiling; each byte maps linearly.
    for (let i = 0; i < width; i++) {
      expect(dec.panDb[i]).toBeCloseTo(QUANT_MIN_DB + (panU8[i] ?? 0) * DEQUANT_STEP_DB, 4);
      expect(dec.wfDb[i]).toBeCloseTo(QUANT_MIN_DB + (wfU8[i] ?? 0) * DEQUANT_STEP_DB, 4);
    }
  });

  it('rejects a u8 frame whose payload is short', () => {
    const bad = u8WireFrame(8, [0, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0]);
    // Truncate one bin byte off the end.
    const trunc = bad.slice(0, bad.byteLength - 1);
    const dv = new DataView(trunc);
    dv.setUint16(2, trunc.byteLength - HEADER_BYTES, true); // fix payloadLen header
    expect(() => decodeDisplayFrame(trunc)).toThrow(FrameDecodeError);
  });
});

describe('decodeDisplayFrame', () => {
  it('round-trips happy path', () => {
    const frame = sampleFrame(1024);
    const buf = encodeDisplayFrame(frame);
    expect(buf.byteLength).toBe(HEADER_BYTES + BODY_FIXED_BYTES + frame.width * 4 * 2);

    const dec = decodeDisplayFrame(buf);
    expect(dec.msgType).toBe(MSG_TYPE_DISPLAY_FRAME);
    expect(dec.seq).toBe(frame.seq);
    expect(dec.tsUnixMs).toBe(frame.tsUnixMs);
    expect(dec.rxId).toBe(frame.rxId);
    expect(dec.bodyFlags).toBe(frame.bodyFlags);
    expect(dec.panValid).toBe(true);
    expect(dec.wfValid).toBe(true);
    expect(dec.width).toBe(frame.width);
    expect(dec.centerHz).toBe(frame.centerHz);
    expect(dec.hzPerPixel).toBeCloseTo(frame.hzPerPixel, 4);
    expect(dec.panDb.length).toBe(frame.width);
    expect(dec.wfDb.length).toBe(frame.width);
    for (let i = 0; i < frame.width; i++) {
      expect(dec.panDb[i]).toBeCloseTo(frame.panDb[i]!, 5);
      expect(dec.wfDb[i]).toBeCloseTo(frame.wfDb[i]!, 5);
    }
  });

  it('reports valid bits from bodyFlags', () => {
    const frame = sampleFrame(128);
    frame.bodyFlags = 0x01;
    const dec = decodeDisplayFrame(encodeDisplayFrame(frame));
    expect(dec.panValid).toBe(true);
    expect(dec.wfValid).toBe(false);
  });

  it('rejects wrong msgType', () => {
    const frame = sampleFrame(64);
    const buf = encodeDisplayFrame(frame);
    new DataView(buf).setUint8(0, 0x99);
    expect(() => decodeDisplayFrame(buf)).toThrow(FrameDecodeError);
  });

  it('rejects truncated buffer', () => {
    const frame = sampleFrame(64);
    const full = encodeDisplayFrame(frame);
    const truncated = full.slice(0, full.byteLength - 4);
    expect(() => decodeDisplayFrame(truncated)).toThrow(FrameDecodeError);
  });

  it('rejects mismatched payloadLen vs width', () => {
    const frame = sampleFrame(64);
    const buf = encodeDisplayFrame(frame);
    new DataView(buf).setUint16(2, BODY_FIXED_BYTES + 4, true);
    expect(() => decodeDisplayFrame(buf)).toThrow(FrameDecodeError);
  });

  it('rejects invalid display geometry before exposing bin arrays', () => {
    for (const patch of [
      (dv: DataView) => dv.setUint16(HEADER_BYTES + 2, 0, true),
      (dv: DataView) => dv.setFloat32(HEADER_BYTES + 12, Number.NaN, true),
      (dv: DataView) => dv.setFloat32(HEADER_BYTES + 12, Infinity, true),
      (dv: DataView) => dv.setFloat32(HEADER_BYTES + 12, 0, true),
    ]) {
      const buf = encodeDisplayFrame(sampleFrame(64));
      patch(new DataView(buf));
      expect(() => decodeDisplayFrame(buf)).toThrow(FrameDecodeError);
    }
  });
});

describe('encodeDisplayFrame', () => {
  it('rejects invalid geometry instead of wrapping it onto the wire', () => {
    expect(() => encodeDisplayFrame(sampleFrame(0))).toThrow(FrameDecodeError);
    expect(() =>
      encodeDisplayFrame({
        ...sampleFrame(64),
        hzPerPixel: Number.NaN,
      }),
    ).toThrow(FrameDecodeError);
  });

  it('rejects bin arrays that do not match the declared frame width', () => {
    expect(() =>
      encodeDisplayFrame({
        ...sampleFrame(64),
        panDb: new Float32Array(63),
      }),
    ).toThrow(FrameDecodeError);
    expect(() =>
      encodeDisplayFrame({
        ...sampleFrame(64),
        wfDb: new Float32Array(65),
      }),
    ).toThrow(FrameDecodeError);
  });
});
