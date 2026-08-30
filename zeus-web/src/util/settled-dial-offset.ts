// SPDX-License-Identifier: GPL-2.0-or-later
//
// Settled offset of the dial from the animated view center, for positioning
// the dial marker and passband overlays.
//
// Outside CTUN the view center IS the effective LO, so the dial's offset
// from it is pure mode math: 0 in phone/data modes, ±cw_pitch in CWU/CWL.
// The old expression — live (vfoHz − vc.getTargetCenterHz()) — assumed vfo
// and target "move in lockstep at input time", which held for browser
// gestures (both updated locally) but broke for front-panel tuning: vfo
// arrives fresh over the 30 Hz push while the view target follows spectrum
// frames that lag the dial by 100–200 ms through the DSP pipeline. Fresh
// minus stale made every overlay lead a few mm in the tuning direction,
// jitter at frame cadence, and spring back at stop (field report,
// 2026-08-30). Mode math is timeline-free, so the overlays are rigid at any
// tuning speed — the spectrum slides under a pinned passband, exactly the
// 2026-06-12 operator intent (and piHPSDR's behaviour).
//
// Under CTUN the live expression stays: the view center is frozen while the
// dial roams, so both terms are coherent by construction and the overlay
// genuinely moves across the screen.
//
// Secondary receivers (RX2 = index 1, RX3+) have no CTUN in the model —
// their DDC center is their LO — so they always take the settled branch.

import type { useConnectionStore } from '../state/connection-store';
import { getReceiverMode, getReceiverVfoHz, type ReceiverKey } from '../state/receiver-state';
import type { viewCenterFor } from '../state/view-center';

type ConnState = ReturnType<typeof useConnectionStore.getState>;
type ViewCenter = ReturnType<typeof viewCenterFor>;

export function settledDialOffsetHz(
  conn: ConnState,
  receiver: ReceiverKey,
  vc: ViewCenter,
): number {
  const isPrimary = receiver === 0 || receiver === 'A';
  if (isPrimary && conn.ctunEnabled && vc.isInitialized()) {
    return getReceiverVfoHz(conn, receiver) - vc.getTargetCenterHz();
  }
  const mode = getReceiverMode(conn, receiver);
  return mode === 'CWU' ? conn.cwPitchHz : mode === 'CWL' ? -conn.cwPitchHz : 0;
}
