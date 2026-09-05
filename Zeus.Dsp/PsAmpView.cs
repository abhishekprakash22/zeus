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
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Dsp;

/// <summary>
/// PureSignal 3 AmpView snapshot — the amplifier as calcc measured it, plus
/// the correction curves it is applying. Read-only telemetry from WDSP's
/// <c>GetPSDisp</c> (calcc.c); no control path involved.
///
/// <para><see cref="X"/>/<see cref="GainY"/> are the measured transfer scatter
/// (normalized input amplitude 0..1 against measured relative gain);
/// <see cref="PhaseDegY"/> is the measured phase at each point in degrees
/// (atan2 of calcc's sin/cos components), relative to
/// <see cref="PhaseRefDeg"/>. <see cref="MagCorX"/>/<see cref="MagCorY"/> and
/// <see cref="PhaseCorX"/>/<see cref="PhaseCorY"/> are the smoothed magnitude
/// and phase correction curves (512 points upstream, downsampled with the
/// scatter). Empty arrays until the first completed fit populates the display
/// buffers.</para>
/// </summary>
public sealed record PsAmpView(
    double[] X,
    double[] GainY,
    double[] PhaseDegY,
    double[] MagCorX,
    double[] MagCorY,
    double[] PhaseCorX,
    double[] PhaseCorY,
    double PhaseRefDeg);
