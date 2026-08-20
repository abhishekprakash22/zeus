// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// The one disconnect. ConnectPanel's Disconnect button and the G2 drawer's
// DISC key both call this, so the protocol cascade and the store resets can
// never drift apart. The cascade tries all three protocol endpoints — the
// server answers on whichever one is live and the others no-op — then pulls
// a fresh RadioStateDto so every store reflects the disconnected radio.

import {
  disconnect as apiDisconnect,
  disconnectP2 as apiDisconnectP2,
  disconnectP3 as apiDisconnectP3,
  fetchState,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';

export async function disconnectAll(): Promise<void> {
  const conn = useConnectionStore.getState();
  try { await apiDisconnectP3(); } catch { /* may be P1/P2 */ }
  try { await apiDisconnect(); } catch { /* may be P2 */ }
  try { await apiDisconnectP2(); } catch { /* may have been P1 */ }
  const fresh = await fetchState();
  conn.applyState(fresh);
  useTxStore.getState().hydrateFromState(fresh);
  conn.setBoardId(null);
  conn.setConnectedProtocol(null);
}
