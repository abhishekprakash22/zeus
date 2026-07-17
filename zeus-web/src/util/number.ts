// SPDX-License-Identifier: GPL-2.0-or-later

export function clampFinite(value: unknown, min: number, max: number, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value)
    ? Math.max(min, Math.min(max, value))
    : fallback;
}

/** Round `value` to the nearest multiple of `stepHz`. A non-positive or
 *  non-finite step degrades to whole-unit rounding. Used by the snap path to
 *  quantize a measured signal edge onto the operator's tuning-step grid so the
 *  hover preview and the self-correcting lock hold a stable frequency instead of
 *  chasing the edge as it breathes with the noise floor. */
export function roundToStep(value: number, stepHz: number): number {
  if (!Number.isFinite(value)) return 0;
  if (!Number.isFinite(stepHz) || stepHz <= 0) return Math.round(value);
  return Math.round(value / stepHz) * stepHz;
}
