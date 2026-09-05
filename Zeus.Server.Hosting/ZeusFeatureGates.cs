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

namespace Zeus.Server.Hosting;

/// <summary>
/// Build-time feature gates for shipped-but-shelved features. A gate set to
/// false unmaps the feature's endpoints (they 404) — the services, storage
/// and all code stay in the tree so flipping the const back re-enables the
/// feature whole. The frontend mirror lives in zeus-web/src/features.ts;
/// keep the two in sync.
///
/// What a false gate does NOT touch: stored PA gain profiles keep applying
/// to TX exactly as calibrated (that is the radio's data, not the wizard),
/// the live SWR meter keeps reading, and SWR protection stays armed.
/// </summary>
public static class ZeusFeatureGates
{
    /// <summary>Automated PA gain calibration + the factory run
    /// (/api/pa-cal/*, PA-tab calibration card). Shelved 2026-09; flip to
    /// true to restore.</summary>
    public const bool PaCalibration = false;

    /// <summary>SWR analyzer — the low-power in-band sweep tool
    /// (/api/swr-sweep/*, PA-tab analyzer card). Shelved 2026-09; flip to
    /// true to restore.</summary>
    public const bool SwrAnalyzer = false;
}
