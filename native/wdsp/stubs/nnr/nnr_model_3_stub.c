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
 * Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
 * License for details.
 */

/* nnr_model_3_stub.c
 *
 * Empty stand-in for upstream WDSP's CW Premium NNR model (nnr_model_3.c),
 * compiled when WDSP_WITH_NNR_PREMIUM=OFF. nnet.c references both symbols
 * unconditionally; a zero-length blob makes slot 3 report itself empty, so
 * SetRXANNRModel(ch, 3) keeps the current slot and consoles can hide the option.
 */

const unsigned char nnr_model_3_data[1] = { 0 };
const unsigned int  nnr_model_3_size    = 0u;
