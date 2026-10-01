// SPDX-License-Identifier: GPL-2.0-or-later
//
// RX meter hold-off around transmissions.
//
// While keyed, the RX DDC still sees our own carrier through the T/R path, so
// RX meter frames report S9+. Those frames used to land in the RX stores, and
// on unkey the S-meter swung up and its peak-hold pinned the bogus value. RX
// meter frames are now dropped while MOX / TUNE / two-tone is on and for
// RX_METER_UNKEY_HOLDOFF_MS after the last of them drops (WDSP meter decay +
// relay recovery), so the meter resumes from its pre-key reading.

import { useTxStore } from './tx-store';

export const RX_METER_UNKEY_HOLDOFF_MS = 500;

type KeyState = { moxOn: boolean; tunOn: boolean; twoToneOn: boolean };

export function createRxMeterGate(now: () => number = () => performance.now()) {
  let keyed = false;
  let unkeyedAt = Number.NEGATIVE_INFINITY;
  return {
    noteKeyState(s: KeyState): void {
      const next = s.moxOn || s.tunOn || s.twoToneOn;
      if (keyed && !next) unkeyedAt = now();
      keyed = next;
    },
    accepts(): boolean {
      return !keyed && now() - unkeyedAt >= RX_METER_UNKEY_HOLDOFF_MS;
    },
  };
}

const gate = createRxMeterGate();
gate.noteKeyState(useTxStore.getState());
useTxStore.subscribe((s) => gate.noteKeyState(s));

/** True when an arriving RX meter frame may update the RX meter stores. */
export function rxMetersAccepted(): boolean {
  return gate.accepts();
}
