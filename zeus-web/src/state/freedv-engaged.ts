// SPDX-License-Identifier: GPL-2.0-or-later
//
// Whether FreeDV is engaged on a receiver the operator is actually using.
// RX2's stored mode survives RX2 being switched off, so a VFO B left in
// FREEDV must not hold the FreeDV popup open while only RX1 is running.

export function isFreeDvEngaged(
  modeA: string,
  modeB: string,
  rx2Enabled: boolean,
): boolean {
  return modeA === 'FREEDV' || (rx2Enabled && modeB === 'FREEDV');
}
