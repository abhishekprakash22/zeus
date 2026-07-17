// SPDX-License-Identifier: GPL-2.0-or-later

export const WIDEBAND_MIN_HZ = 0;
export const WIDEBAND_MAX_HZ = 60_000_000;
export const WIDEBAND_SPAN_HZ = WIDEBAND_MAX_HZ - WIDEBAND_MIN_HZ;
export const WIDEBAND_CENTER_HZ = WIDEBAND_SPAN_HZ / 2;

// The full-band source frame stays fixed at 0-60 MHz. Local zoom is a GPU view
// transform over that source, so this can be far deeper than backend DDC zoom
// without asking the radio or sidecar for another stream.
export const WIDEBAND_LOCAL_ZOOM_MAX = 4096;
export const WIDEBAND_ZOOM_STEP = 1.25;

const FULL_SPAN_RATIO = 0.96;
const EDGE_TOLERANCE_HZ = 750_000;
const EPS_REL = 1e-6;

export type SpectrumViewport = {
  centerHz: number;
  hzPerPixel: number;
  spanHz: number;
  wideband: boolean;
};

export type WidebandZoomResult = {
  centerHz: number;
  hzPerPixel: number;
  anchorHz: number;
};

function trimFixed(value: number, decimals: number): string {
  const fixed = value.toFixed(decimals);
  return fixed.replace(/\.0+$/, '').replace(/(\.\d*?)0+$/, '$1');
}

export function sourceSpanHz(width: number, hzPerPixel: number): number {
  return width > 0 && hzPerPixel > 0 ? width * hzPerPixel : 0;
}

export function isWidebandDisplayGeometry(i: {
  width: number;
  hzPerPixel: number;
  centerHz: number;
}): boolean {
  const spanHz = sourceSpanHz(i.width, i.hzPerPixel);
  if (spanHz <= 0 || !Number.isFinite(i.centerHz)) return false;
  const loHz = i.centerHz - spanHz / 2;
  const hiHz = i.centerHz + spanHz / 2;
  return (
    spanHz >= WIDEBAND_SPAN_HZ * FULL_SPAN_RATIO &&
    loHz <= WIDEBAND_MIN_HZ + EDGE_TOLERANCE_HZ &&
    hiHz >= WIDEBAND_MAX_HZ - EDGE_TOLERANCE_HZ
  );
}

export function clampWidebandHzPerPixel(sourceHzPerPixel: number, hzPerPixel: number): number {
  if (!(sourceHzPerPixel > 0)) return 0;
  const minHzPerPixel = sourceHzPerPixel / WIDEBAND_LOCAL_ZOOM_MAX;
  if (!(hzPerPixel > 0)) return sourceHzPerPixel;
  return Math.min(sourceHzPerPixel, Math.max(minHzPerPixel, hzPerPixel));
}

export function clampWidebandCenter(centerHz: number, spanHz: number): number {
  if (!(spanHz > 0) || spanHz >= WIDEBAND_SPAN_HZ) return WIDEBAND_CENTER_HZ;
  const half = spanHz / 2;
  return Math.min(WIDEBAND_MAX_HZ - half, Math.max(WIDEBAND_MIN_HZ + half, centerHz));
}

export function clampPointerFraction(frac: number): number {
  if (!Number.isFinite(frac)) return 0.5;
  return Math.min(1, Math.max(0, frac));
}

export function resolveSpectrumViewport(i: {
  width: number;
  sourceCenterHz: number;
  sourceHzPerPixel: number;
  viewCenterHz?: number;
  viewHzPerPixel?: number;
}): SpectrumViewport | null {
  if (!(i.width > 0) || !(i.sourceHzPerPixel > 0) || !Number.isFinite(i.sourceCenterHz)) {
    return null;
  }
  const wideband = isWidebandDisplayGeometry({
    width: i.width,
    hzPerPixel: i.sourceHzPerPixel,
    centerHz: i.sourceCenterHz,
  });
  if (!wideband) {
    const spanHz = i.width * i.sourceHzPerPixel;
    return {
      centerHz: i.sourceCenterHz,
      hzPerPixel: i.sourceHzPerPixel,
      spanHz,
      wideband: false,
    };
  }

  const hzPerPixel = clampWidebandHzPerPixel(i.sourceHzPerPixel, i.viewHzPerPixel ?? i.sourceHzPerPixel);
  const spanHz = i.width * hzPerPixel;
  const centerHz = clampWidebandCenter(i.viewCenterHz ?? i.sourceCenterHz, spanHz);
  return { centerHz, hzPerPixel, spanHz, wideband: true };
}

export function zoomWidebandViewport(i: {
  width: number;
  sourceHzPerPixel: number;
  currentHzPerPixel: number;
  currentCenterHz: number;
  pointerFraction: number;
  zoomDelta: number;
  stepFactor?: number;
}): WidebandZoomResult | null {
  if (i.zoomDelta === 0 || !(i.width > 0) || !(i.sourceHzPerPixel > 0)) return null;
  const stepFactor = i.stepFactor && i.stepFactor > 1 ? i.stepFactor : WIDEBAND_ZOOM_STEP;
  const pointerFraction = clampPointerFraction(i.pointerFraction);
  const currentHzPerPixel = clampWidebandHzPerPixel(i.sourceHzPerPixel, i.currentHzPerPixel);
  const factor = Math.pow(stepFactor, Math.abs(i.zoomDelta));
  const rawNextHzPerPixel =
    i.zoomDelta > 0 ? currentHzPerPixel / factor : currentHzPerPixel * factor;
  const hzPerPixel = clampWidebandHzPerPixel(i.sourceHzPerPixel, rawNextHzPerPixel);
  const currentSpanHz = i.width * currentHzPerPixel;
  const nextSpanHz = i.width * hzPerPixel;
  const anchorHz = i.currentCenterHz + (pointerFraction - 0.5) * currentSpanHz;
  const centerHz =
    Math.abs(hzPerPixel - i.sourceHzPerPixel) <= i.sourceHzPerPixel * EPS_REL
      ? WIDEBAND_CENTER_HZ
      : clampWidebandCenter(anchorHz - (pointerFraction - 0.5) * nextSpanHz, nextSpanHz);
  return { centerHz, hzPerPixel, anchorHz };
}

export function formatWidebandRangeLabel(startHz: number, endHz: number): string {
  const loHz = Math.max(WIDEBAND_MIN_HZ, Math.min(startHz, endHz));
  const hiHz = Math.min(WIDEBAND_MAX_HZ, Math.max(startHz, endHz));
  const spanHz = Math.max(0, hiHz - loHz);
  const decimals = spanHz >= 10_000_000 ? 0 : spanHz >= 1_000_000 ? 2 : spanHz >= 100_000 ? 3 : 4;
  return `${trimFixed(loHz / 1e6, decimals)}-${trimFixed(hiHz / 1e6, decimals)} MHz`;
}

export function formatWidebandZoomRatio(sourceHzPerPixel: number, hzPerPixel: number): string {
  if (!(sourceHzPerPixel > 0) || !(hzPerPixel > 0)) return '1x';
  const ratio = Math.max(1, sourceHzPerPixel / hzPerPixel);
  if (ratio >= 100) return `${Math.round(ratio)}x`;
  if (ratio >= 10) return `${trimFixed(ratio, 1)}x`;
  return `${trimFixed(ratio, 2)}x`;
}
