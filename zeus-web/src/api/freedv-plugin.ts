// SPDX-License-Identifier: GPL-2.0-or-later
//
// REST client for the Zeus FreeDV plugin (org.openhpsdr.freedv). The FreeDV UI
// shell stays in core; the modem/reporter backend now lives under the plugin
// route prefix.

export const FREEDV_PLUGIN_ID = 'org.openhpsdr.freedv';
export const FREEDV_PLUGIN_BASE = `/api/plugins/${FREEDV_PLUGIN_ID}`;

export async function probeFreeDvPlugin(signal?: AbortSignal): Promise<boolean> {
  try {
    const res = await fetch(`${FREEDV_PLUGIN_BASE}/status`, { signal });
    return res.ok;
  } catch {
    return false;
  }
}
