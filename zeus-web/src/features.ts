// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

/**
 * Build-time feature gates for shipped-but-shelved features — the frontend
 * mirror of Zeus.Server.Hosting/ZeusFeatureGates.cs; keep the two in sync.
 * A false gate hides the feature's UI; the components stay in the tree and
 * flipping the flag restores them whole. Stored PA gain profiles keep
 * applying to TX and the live SWR meter / SWR protection are unaffected —
 * those are the radio's data and safety paths, not the shelved tools.
 */
export const FEATURES = {
  /** Automated PA gain calibration + factory run (PA-tab card). */
  paCalibration: false,
  /** SWR analyzer sweep tool (PA-tab card). */
  swrAnalyzer: false,
} as const;
