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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { create } from 'zustand';
import type {
  LogEntry,
  CreateLogEntryRequest,
  UpdateLogEntryRequest,
  LogCapabilities,
  AdifImportResponse,
  AdifExportToFileResponse,
  QrzPublishResponse,
  LogDeleteResponse,
  WorkedCallsignSummary,
} from '../api/log';
import {
  getLogEntries,
  getLogCapabilities,
  getLogTags,
  getWorkedCallsignSummary,
  createLogEntry,
  updateLogEntry,
  exportAdifToDirectory,
  exportToAdif,
  importAdif,
  publishToQrz,
  deleteLogEntries,
} from '../api/log';
import { useCapabilitiesStore } from './capabilities-store';
import { isLogbookPluginReady, logbookPluginUnavailableReason } from './logbook-plugin-store';

let logbookUnavailableNoticeShown = false;
const LOGBOOK_UNAVAILABLE_FALLBACK = 'Logbook plugin unavailable';
const LOGBOOK_INSTALL_REASON = 'Install the Logbook plugin from Settings → Plugins';

function isLogbookUnavailableMessage(message: string | null): boolean {
  return message === LOGBOOK_UNAVAILABLE_FALLBACK || message === LOGBOOK_INSTALL_REASON;
}

function applyLogPatch(entry: LogEntry, patch: UpdateLogEntryRequest): LogEntry {
  return {
    ...entry,
    ...(patch.name !== undefined ? { name: patch.name } : {}),
    ...(patch.grid !== undefined ? { grid: patch.grid } : {}),
    ...(patch.country !== undefined ? { country: patch.country } : {}),
    ...(patch.state !== undefined ? { state: patch.state } : {}),
    ...(patch.comment !== undefined ? { comment: patch.comment } : {}),
    ...(patch.tags !== undefined ? { tags: patch.tags } : {}),
    ...(patch.qslSent !== undefined ? { qslSent: patch.qslSent } : {}),
    ...(patch.qslRcvd !== undefined ? { qslRcvd: patch.qslRcvd } : {}),
    ...(patch.qslSentDate !== undefined ? { qslSentDate: patch.qslSentDate } : {}),
    ...(patch.qslRcvdDate !== undefined ? { qslRcvdDate: patch.qslRcvdDate } : {}),
    ...(patch.clearQslSentDate ? { qslSentDate: null } : {}),
    ...(patch.clearQslRcvdDate ? { qslRcvdDate: null } : {}),
    ...(patch.rig !== undefined ? { rig: patch.rig } : {}),
    ...(patch.antenna !== undefined ? { antenna: patch.antenna } : {}),
    ...(patch.txPowerW !== undefined ? { txPowerW: patch.txPowerW } : {}),
    ...(patch.clearTxPowerW ? { txPowerW: null } : {}),
    ...(patch.rstSent !== undefined ? { rstSent: patch.rstSent ?? entry.rstSent } : {}),
    ...(patch.rstRcvd !== undefined ? { rstRcvd: patch.rstRcvd ?? entry.rstRcvd } : {}),
    ...(patch.mode !== undefined ? { mode: patch.mode ?? entry.mode } : {}),
    ...(patch.band !== undefined ? { band: patch.band ?? entry.band } : {}),
    ...(patch.frequencyMhz !== undefined ? { frequencyMhz: patch.frequencyMhz } : {}),
    ...(patch.clearFrequencyMhz ? { frequencyMhz: null } : {}),
    ...(patch.qsoDateTimeUtc !== undefined ? { qsoDateTimeUtc: patch.qsoDateTimeUtc ?? entry.qsoDateTimeUtc } : {}),
  };
}

type LoggerState = {
  entries: LogEntry[];
  totalCount: number;
  capabilities: LogCapabilities | null;
  tagList: string[];
  /** True once `entries` holds the complete log (via loadAllEntries), not just
   *  the 100-row quick page. The dashboard stats are only honest when set. */
  fullyLoaded: boolean;
  loading: boolean;
  error: string | null;
  importInFlight: boolean;
  importError: string | null;
  lastImportResult: AdifImportResponse | null;
  publishInFlight: boolean;
  publishError: string | null;
  lastPublishResult: QrzPublishResponse | null;
  exportInFlight: boolean;
  exportError: string | null;
  lastExportResult: AdifExportToFileResponse | null;
  deleteInFlight: boolean;
  deleteError: string | null;
  lastDeleteResult: LogDeleteResponse | null;
  selectedIds: Set<string>;
  workedSummary: WorkedCallsignSummary | null;
  workedSummaryLoading: boolean;
  workedSummaryError: string | null;

  // Actions
  loadCapabilities: () => Promise<void>;
  loadTags: () => Promise<void>;
  loadEntries: () => Promise<void>;
  /** Load the ENTIRE log (paged) for the popped-out Logbook workspace so its
   *  table and dashboard stats cover every QSO, not the quick 100-row page. */
  loadAllEntries: () => Promise<void>;
  /** Reload after a mutation, preserving whichever depth is currently loaded:
   *  the full log if the workspace has hydrated it, otherwise the quick page. */
  refreshEntries: () => Promise<void>;
  loadWorkedSummary: (callsign: string, signal?: AbortSignal) => Promise<WorkedCallsignSummary | null>;
  clearWorkedSummary: () => void;
  addLogEntry: (request: CreateLogEntryRequest) => Promise<LogEntry | null>;
  updateEntry: (id: string, patch: UpdateLogEntryRequest) => Promise<LogEntry | null>;
  exportAdif: () => Promise<void>;
  clearExportResult: () => void;
  importAdifFile: (file: File) => Promise<AdifImportResponse | null>;
  clearImportResult: () => void;
  publishSelectedToQrz: (logEntryIds: string[]) => Promise<void>;
  clearPublishResult: () => void;
  deleteSelected: (logEntryIds: string[]) => Promise<void>;
  clearDeleteResult: () => void;
  toggleSelected: (id: string) => void;
  setSelectedIds: (ids: Iterable<string>) => void;
  clearSelected: () => void;
};

export const useLoggerStore = create<LoggerState>((set, get) => ({
  entries: [],
  totalCount: 0,
  capabilities: null,
  tagList: [],
  fullyLoaded: false,
  loading: false,
  error: null,
  importInFlight: false,
  importError: null,
  lastImportResult: null,
  publishInFlight: false,
  publishError: null,
  lastPublishResult: null,
  exportInFlight: false,
  exportError: null,
  lastExportResult: null,
  deleteInFlight: false,
  deleteError: null,
  lastDeleteResult: null,
  selectedIds: new Set<string>(),
  workedSummary: null,
  workedSummaryLoading: false,
  workedSummaryError: null,

  loadCapabilities: async () => {
    try {
      const capabilities = await getLogCapabilities();
      set({ capabilities });
      if (capabilities.canTags) void get().loadTags();
    } catch {
      set({ capabilities: null, tagList: [] });
    }
  },

  loadTags: async () => {
    try {
      const tags = await getLogTags();
      set({ tagList: tags });
    } catch {
      set({ tagList: [] });
    }
  },

  loadEntries: async () => {
    set({ loading: true, error: null });
    try {
      const response = await getLogEntries(0, 100);
      set({
        entries: response.entries,
        totalCount: response.totalCount,
        // The quick page is "complete" only when the whole log fits in it.
        fullyLoaded: response.entries.length >= response.totalCount,
        loading: false,
      });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : 'Failed to load log entries', loading: false });
    }
  },

  loadAllEntries: async () => {
    set({ loading: true, error: null });
    // Page through the log in chunks. Capped so a pathological log can't pin the
    // UI thread forever; if the cap is hit the dashboard simply reflects the
    // rows we have (the count readout still shows the true total).
    const PAGE = 500;
    const MAX = 20000;
    try {
      const all: LogEntry[] = [];
      let total = 0;
      for (let skip = 0; skip < MAX; skip += PAGE) {
        const response = await getLogEntries(skip, PAGE);
        total = response.totalCount;
        all.push(...response.entries);
        if (response.entries.length < PAGE || all.length >= total) break;
      }
      set({
        entries: all,
        totalCount: total,
        fullyLoaded: all.length >= total,
        loading: false,
      });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : 'Failed to load log entries', loading: false });
    }
  },

  refreshEntries: async () => {
    if (get().fullyLoaded) {
      await get().loadAllEntries();
    } else {
      await get().loadEntries();
    }
  },

  loadWorkedSummary: async (callsign: string, signal?: AbortSignal) => {
    const key = callsign.trim().toUpperCase();
    if (!key) {
      set({ workedSummary: null, workedSummaryLoading: false, workedSummaryError: null });
      return null;
    }

    set({ workedSummaryLoading: true, workedSummaryError: null });
    try {
      const summary = await getWorkedCallsignSummary(key, signal);
      if (signal?.aborted) return null;
      set({ workedSummary: summary, workedSummaryLoading: false });
      return summary;
    } catch (err) {
      if (signal?.aborted) return null;
      set({
        workedSummary: null,
        workedSummaryError: err instanceof Error ? err.message : 'Failed to load worked-before summary',
        workedSummaryLoading: false,
      });
      return null;
    }
  },

  clearWorkedSummary: () => {
    set({ workedSummary: null, workedSummaryLoading: false, workedSummaryError: null });
  },

  addLogEntry: async (request: CreateLogEntryRequest) => {
    if (isLogbookPluginReady()) {
      if (logbookUnavailableNoticeShown) {
        logbookUnavailableNoticeShown = false;
        if (isLogbookUnavailableMessage(get().error)) set({ error: null });
      }
      set({ error: null });
    } else {
      const message = logbookPluginUnavailableReason() ?? LOGBOOK_UNAVAILABLE_FALLBACK;
      if (!logbookUnavailableNoticeShown) {
        logbookUnavailableNoticeShown = true;
        set({ error: message });
      }
      return null;
    }

    try {
      const entry = await createLogEntry(request);
      // Reload the list (full log if the workspace hydrated it, else quick page).
      await get().refreshEntries();
      const activeSummaryCall = get().workedSummary?.callsign;
      if (activeSummaryCall && activeSummaryCall === entry.callsign.trim().toUpperCase()) {
        await get().loadWorkedSummary(activeSummaryCall);
      }
      return entry;
    } catch (err) {
      set({ error: err instanceof Error ? err.message : 'Failed to create log entry' });
      return null;
    }
  },

  updateEntry: async (id: string, patch: UpdateLogEntryRequest) => {
    const prevEntries = get().entries;
    const previous = prevEntries.find((entry) => entry.id === id) ?? null;
    if (!previous) return null;

    const optimistic = applyLogPatch(previous, patch);
    set({
      entries: prevEntries.map((entry) => (entry.id === id ? optimistic : entry)),
      error: null,
    });

    try {
      const saved = await updateLogEntry(id, patch);
      set((state) => ({
        entries: state.entries.map((entry) => (entry.id === id ? saved : entry)),
      }));
      if (patch.tags) void get().loadTags();
      return saved;
    } catch (err) {
      set((state) => ({
        entries: state.entries.map((entry) => (entry.id === id ? previous : entry)),
        error: err instanceof Error ? err.message : 'Failed to update log entry',
      }));
      return null;
    }
  },

  exportAdif: async () => {
    // When the backend is on the operator's own machine (desktop, or a
    // loopback web session), write the .adi into a directory there and report
    // the path. When it's remote (LAN/headless backend), a server-side file
    // would be unreachable, so fall back to delivering the ADIF to the
    // operator's browser as a download.
    const localToServer = useCapabilitiesStore.getState().localToServer;
    if (!localToServer) {
      set({ exportInFlight: false, exportError: null, lastExportResult: null, error: null });
      try {
        await exportToAdif();
      } catch (err) {
        set({ exportError: err instanceof Error ? err.message : 'Failed to export ADIF' });
      }
      return;
    }

    set({ exportInFlight: true, exportError: null, lastExportResult: null, error: null });
    try {
      const result = await exportAdifToDirectory();
      set({ lastExportResult: result, exportInFlight: false });
    } catch (err) {
      set({
        exportError: err instanceof Error ? err.message : 'Failed to export ADIF',
        exportInFlight: false,
      });
    }
  },

  clearExportResult: () => {
    set({ lastExportResult: null, exportError: null });
  },

  importAdifFile: async (file: File) => {
    set({ importInFlight: true, importError: null, lastImportResult: null, error: null });
    try {
      const result = await importAdif(file);
      set({ lastImportResult: result, importInFlight: false });
      await get().refreshEntries();
      return result;
    } catch (err) {
      set({
        importError: err instanceof Error ? err.message : 'Failed to import ADIF',
        importInFlight: false,
      });
      return null;
    }
  },

  clearImportResult: () => {
    set({ lastImportResult: null, importError: null });
  },

  publishSelectedToQrz: async (logEntryIds: string[]) => {
    set({ publishInFlight: true, publishError: null, lastPublishResult: null });
    try {
      const result = await publishToQrz({ logEntryIds });
      set({ lastPublishResult: result, publishInFlight: false, selectedIds: new Set<string>() });
      // Reload so QRZ sync status refreshes.
      await get().refreshEntries();
    } catch (err) {
      set({
        publishError: err instanceof Error ? err.message : 'Failed to publish to QRZ',
        publishInFlight: false,
      });
    }
  },

  clearPublishResult: () => {
    set({ lastPublishResult: null, publishError: null });
  },

  deleteSelected: async (logEntryIds: string[]) => {
    if (logEntryIds.length === 0) return;
    set({ deleteInFlight: true, deleteError: null, lastDeleteResult: null });
    try {
      const result = await deleteLogEntries({ logEntryIds });
      set({ lastDeleteResult: result, deleteInFlight: false, selectedIds: new Set<string>() });
      // Reload so the deleted rows disappear from the table.
      await get().refreshEntries();
    } catch (err) {
      set({
        deleteError: err instanceof Error ? err.message : 'Failed to delete log entries',
        deleteInFlight: false,
      });
    }
  },

  clearDeleteResult: () => {
    set({ lastDeleteResult: null, deleteError: null });
  },

  toggleSelected: (id: string) => {
    const next = new Set(get().selectedIds);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    set({ selectedIds: next });
  },

  setSelectedIds: (ids: Iterable<string>) => set({ selectedIds: new Set(ids) }),

  clearSelected: () => set({ selectedIds: new Set<string>() }),
}));

// Load entries on module load
useLoggerStore.getState().loadEntries();
