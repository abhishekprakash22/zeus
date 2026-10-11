// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Local-only maintenance. Updating ANAN Core, updating/stopping/starting
// p2app, flashing gateware and the raw PCIe bench controls can each remove
// the link a remote operator would need to recover, so they are done at the
// radio. The server refuses them over the remote tunnel
// (RemoteWebRtcSession.WriteDeniedPrefixes); these screens say so instead
// of offering a button that can only fail.

import { isRemoteMode } from './remote-client';

/** True when maintenance controls must be withheld (remote session). */
export function maintenanceLocalOnly(): boolean {
  return isRemoteMode();
}

/** The one line shown where a maintenance control would be. */
export const LOCAL_ONLY_NOTE = 'Done at the radio — not available over a remote session.';
