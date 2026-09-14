// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// NR mode cycles. TWO of them, deliberately:
//
//  - the NB-NR page keeps every mode the engine can run, so nothing becomes
//    unreachable;
//  - the assignable deck key cycles OFF -> EMNR -> Neural only (operator
//    decision, 2026-09-13), because those are the two anyone actually reaches
//    for and the others aren't worth a press each on the front glass.
//
// Both are availability-gated: NR3 needs libwdsp's RNNoise export AND a
// loaded model, NR5 needs the NNR setters. A mode the engine can't run is
// left out of the cycle rather than offered and failing.

import type { NrMode } from '../api/client';

const NR_CYCLE_WITHOUT_NR3: readonly NrMode[] = ['Off', 'Anr', 'Emnr', 'Sbnr'];
const NR_CYCLE_WITH_NR3: readonly NrMode[] = ['Off', 'Anr', 'Emnr', 'Rnnr', 'Sbnr'];

/** Full cycle - Off -> NR1 -> NR2 -> (NR3) -> NR4 -> (NR5) -> Off. Used by the
 *  NB-NR page, which is where every mode stays reachable. */
export function nrCycleFor(nr3Ready: boolean, nnrAvailable = false): readonly NrMode[] {
  const base = nr3Ready ? NR_CYCLE_WITH_NR3 : NR_CYCLE_WITHOUT_NR3;
  return nnrAvailable ? [...base, 'Nnr'] : base;
}

/** Deck-key cycle - Off -> EMNR -> (Neural) -> Off. */
export function nrKeyCycleFor(nnrAvailable: boolean): readonly NrMode[] {
  const base: readonly NrMode[] = ['Off', 'Emnr'];
  return nnrAvailable ? [...base, 'Nnr'] : base;
}

/** Short labels for the NB-NR page, where the button is small and sits beside
 *  NB/ANF/SNB. Unchanged from the historical numbering. */
export const NR_LABEL: Record<NrMode, string> = {
  Off: 'NR',
  Anr: 'NR1',
  Emnr: 'NR2',
  Sbnr: 'NR4',
  Rnnr: 'NR3',
  Nnr: 'NR5',
};

/** Deck-key labels name the ALGORITHM: on the front glass 'NR2' says nothing
 *  useful, and the key has room for the real distinction. */
export const NR_KEY_LABEL: Record<NrMode, string> = {
  Off: 'NR',
  Anr: 'NR (ANR)',
  Emnr: 'NR (EMNR)',
  Sbnr: 'NR (SBNR)',
  Rnnr: 'NR (RNNoise)',
  Nnr: 'NR (Neural)',
};

export function nrModeTitle(mode: NrMode): string {
  switch (mode) {
    case 'Off': return 'Noise reduction off';
    case 'Anr': return 'NR1 (ANR, time-domain LMS)';
    case 'Emnr': return 'EMNR - spectral noise reduction';
    case 'Sbnr': return 'NR4 (SBNR, libspecbleach)';
    case 'Rnnr': return 'NR3 (RNNoise, neural)';
    case 'Nnr': return 'Neural NR (WDSP NNR, adds ~51 ms latency)';
  }
}

/** Next mode in the deck key's cycle. A mode set elsewhere (the NB-NR page or
 *  the front panel) that isn't in this cycle steps to the first entry rather
 *  than sticking - indexOf returns -1 and we start over. */
export function nextNrMode(
  current: NrMode,
  _nr3Ready: boolean,
  nnrAvailable: boolean,
): NrMode {
  const cycle = nrKeyCycleFor(nnrAvailable);
  const idx = cycle.indexOf(current);
  return cycle[(idx < 0 ? 0 : idx + 1) % cycle.length]!;
}
