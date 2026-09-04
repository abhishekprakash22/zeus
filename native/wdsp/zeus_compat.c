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

/* zeus_compat.c — see zeus_compat.h. */

#include "comm.h"
#include "zeus_compat.h"

/* Per-channel interleaved staging buffers. Thetis kept these inside the
   CALCC struct (temptx / temprx); upstream 2.x has no such members, so they
   live here. Growth-only allocation, serialised on the same calcc update
   critical section Thetis's psccF took. */
static double* zc_tx[MAX_CHANNELS];
static double* zc_rx[MAX_CHANNELS];
static int     zc_cap[MAX_CHANNELS];

static void zc_ensure (int channel, int size)
{
	if (zc_cap[channel] >= size) return;
	_aligned_free (zc_tx[channel]);
	_aligned_free (zc_rx[channel]);
	zc_tx[channel]  = (double*) malloc0 (size * sizeof (complex));
	zc_rx[channel]  = (double*) malloc0 (size * sizeof (complex));
	zc_cap[channel] = size;
}

void zeus_compat_release_pscc_buffers (int channel)
{
	_aligned_free (zc_tx[channel]);
	_aligned_free (zc_rx[channel]);
	zc_tx[channel]  = 0;
	zc_rx[channel]  = 0;
	zc_cap[channel] = 0;
}

PORT
void psccF (int channel, int size, float* Itxbuff, float* Qtxbuff,
	float* Irxbuff, float* Qrxbuff, int mox, int solidmox)
{
	int i;
	double *tx, *rx;
	(void) mox;
	(void) solidmox;
	EnterCriticalSection (&txa[channel].calcc.cs_update);
	zc_ensure (channel, size);
	tx = zc_tx[channel];
	rx = zc_rx[channel];
	LeaveCriticalSection (&txa[channel].calcc.cs_update);
	for (i = 0; i < size; i++)
	{
		tx[2 * i + 0] = (double) Itxbuff[i];
		tx[2 * i + 1] = (double) Qtxbuff[i];
		rx[2 * i + 0] = (double) Irxbuff[i];
		rx[2 * i + 1] = (double) Qrxbuff[i];
	}
	pscc (channel, size, tx, rx);
}
