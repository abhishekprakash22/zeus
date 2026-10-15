// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// What the spectrum renderers ask when the light (paper) theme is on: which
// palette to use (the paper palette replaces the operator's dark palette —
// those start at black and invert badly on paper), the surface, and the
// trace colour made readable on it.

import type { ColormapId, RenderColormapId } from '../gl/colormap';
import { useThemeStore } from '../state/theme-store';
import { paperTraceRgb, type Rgb } from './paper';

export function paperActive(): boolean {
  return useThemeStore.getState().theme === 'light';
}

/** The palette the displays should render with right now. */
export function displayColormap(id: ColormapId): RenderColormapId {
  return paperActive() ? 'paper' : id;
}

/**
 * Trace colour as float RGB for the GL renderer: the operator's colour in the
 * dark theme; on paper, the same hue darkened until it reads on the surface.
 */
export function displayTraceFloats(traceHex: string, darkFloats: { r: number; g: number; b: number }): {
  r: number;
  g: number;
  b: number;
} {
  if (!paperActive()) return darkFloats;
  const c: Rgb = paperTraceRgb(traceHex, useThemeStore.getState().paperSurface);
  return { r: c[0] / 255, g: c[1] / 255, b: c[2] / 255 };
}
