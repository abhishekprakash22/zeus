/*
 * SPDX-License-Identifier: GPL-2.0-or-later
 *
 * Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
 * Copyright (C) 2025-2026 Brian Keating (EI6LF),
 *                         Douglas J. Cerrato (KB2UKA),
 *                         Christian Suarez (N9WAR), and contributors.
 *
 * This program is free software: you can redistribute it and/or modify it
 * under the terms of the GNU General Public License as published by the
 * Free Software Foundation, either version 2 of the License, or (at your
 * option) any later version. See the LICENSE file at the root of this
 * repository for the full text, or https://www.gnu.org/licenses/.
 *
 * WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
 * (NR0V), distributed under GPL v2 or later.
 *
 * Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
 * License for details.
 */

/* zeus_compat.h
 *
 * Zeus-owned compatibility entry points layered over upstream WDSP.
 *
 * Upstream WDSP 2.x dropped the Thetis-lineage `psccF` export (the
 * separate-buffer float I/Q wrapper around `pscc`). Zeus's PureSignal
 * feedback pump (WdspDspEngine.FeedPsFeedbackBlock) P/Invokes `psccF`, and
 * the PureSignal hard rule in CLAUDE.md forbids touching that managed path
 * without KB2UKA approval. This shim keeps the exported ABI identical so the
 * 2.1.0 port lands with zero managed-side PureSignal changes. Retiring it in
 * favour of a direct `pscc` call is a Phase-3 item in
 * docs/designs/wdsp-2.1.0-upgrade-plan.md.
 */

#ifndef _zeus_compat_h
#define _zeus_compat_h

/* Thetis signature, unchanged. `mox` / `solidmox` are ignored exactly as
   they were in the Thetis implementation (MOX is conveyed via SetPSMox). */
extern __declspec (dllexport) void psccF (int channel, int size,
	float* Itxbuff, float* Qtxbuff, float* Irxbuff, float* Qrxbuff,
	int mox, int solidmox);

/* Frees the per-channel staging buffers. Called from destroy_calcc. */
extern void zeus_compat_release_pscc_buffers (int channel);

#endif
