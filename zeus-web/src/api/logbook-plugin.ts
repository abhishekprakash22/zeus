// SPDX-License-Identifier: GPL-2.0-or-later
//
// REST client for the Zeus Logbook plugin (org.openhpsdr.logbook). The Logbook
// UI shell stays in core; the store and ADIF backend live under the plugin
// route prefix.

export const LOGBOOK_PLUGIN_ID = 'org.openhpsdr.logbook';
export const LOGBOOK_PLUGIN_BASE = `/api/plugins/${LOGBOOK_PLUGIN_ID}`;

export async function probeLogbookPlugin(signal?: AbortSignal): Promise<boolean> {
  try {
    const res = await fetch(`${LOGBOOK_PLUGIN_BASE}/status`, { signal });
    return res.ok;
  } catch {
    return false;
  }
}
