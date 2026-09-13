// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// The NR mode cycle, shared by the DSP panel's NR button and the assignable
// NR deck key. Extracted rather than duplicated: the cycle is availability-
// gated (NR3 needs libwdsp's RNNoise export AND a loaded model; NR5 needs the
// NNR setters), and two copies of that reasoning would drift the first time a
// mode's gating changed.

import type { NrMode } from '../api/client';

const NR_CYCLE_WITHOUT_NR3: readonly NrMode[] = ['Off', 'Anr', 'Emnr', 'Sbnr'];
const NR_CYCLE_WITH_NR3: readonly NrMode[] = ['Off', 'Anr', 'Emnr', 'Rnnr', 'Sbnr'];

/** Off → NR1 → NR2 → (NR3) → NR4 → (NR5) → Off. Modes whose engine support is
 *  absent are left out entirely rather than offered and failing. */
export function nrCycleFor(nr3Ready: boolean, nnrAvailable = false): readonly NrMode[] {
  const base = nr3Ready ? NR_CYCLE_WITH_NR3 : NR_CYCLE_WITHOUT_NR3;
  return nnrAvailable ? [...base, 'Nnr'] : base;
}

/** The engine's mode names are historical; operators read NR1..NR5. */
export const NR_LABEL: Record<NrMode, string> = {
  Off: 'NR',
  Anr: 'NR1',
  Emnr: 'NR2',
  Sbnr: 'NR4',
  Rnnr: 'NR3',
  Nnr: 'NR5',
};

export function nrModeTitle(mode: NrMode): string {
  switch (mode) {
    case 'Off': return 'Noise reduction off';
    case 'Anr': return 'NR1 (ANR, time-domain LMS)';
    case 'Emnr': return 'NR2 (EMNR, spectral)';
    case 'Sbnr': return 'NR4 (SBNR, libspecbleach)';
    case 'Rnnr': return 'NR3 (RNNoise, neural)';
    case 'Nnr': return 'NR5 (NNR, WDSP neural, +51 ms)';
  }
}

/** Next mode in the availability-gated cycle. */
export function nextNrMode(
  current: NrMode,
  nr3Ready: boolean,
  nnrAvailable: boolean,
): NrMode {
  const cycle = nrCycleFor(nr3Ready, nnrAvailable);
  const idx = cycle.indexOf(current);
  return cycle[(idx < 0 ? 0 : idx + 1) % cycle.length]!;
}
