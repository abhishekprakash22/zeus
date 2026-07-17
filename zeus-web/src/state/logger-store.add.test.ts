// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { CreateLogEntryRequest, LogEntry } from '../api/log';

function makeRequest(callsign = 'K1ABC'): CreateLogEntryRequest {
  return {
    callsign,
    frequencyMhz: 14.074,
    band: '20M',
    mode: 'FT8',
    rstSent: '-12',
    rstRcvd: '-09',
  } as CreateLogEntryRequest;
}

function makeEntry(callsign = 'K1ABC'): LogEntry {
  return {
    id: `qso-${callsign}`,
    qsoDateTimeUtc: '2026-06-28T12:00:00.000Z',
    callsign,
    name: null,
    frequencyMhz: 14.074,
    band: '20M',
    mode: 'FT8',
    rstSent: '-12',
    rstRcvd: '-09',
    grid: null,
    country: null,
    dxcc: null,
    cqZone: null,
    ituZone: null,
    state: null,
    comment: null,
    createdUtc: '2026-06-28T12:00:00.000Z',
    qrzLogId: null,
    qrzUploadedUtc: null,
  } as LogEntry;
}

const h = vi.hoisted(() => ({
  createLogEntry: vi.fn(),
  getLogEntries: vi.fn(),
}));

vi.mock('../api/log', () => ({
  getLogEntries: h.getLogEntries,
  getWorkedCallsignSummary: vi.fn(),
  createLogEntry: h.createLogEntry,
  exportAdifToDirectory: vi.fn(),
  exportToAdif: vi.fn(),
  importAdif: vi.fn(),
  publishToQrz: vi.fn(),
  deleteLogEntries: vi.fn(),
}));

import { useLoggerStore } from './logger-store';
import { useLogbookPluginStore } from './logbook-plugin-store';

describe('logger-store addLogEntry logbook gate', () => {
  beforeEach(() => {
    h.createLogEntry.mockReset();
    h.createLogEntry.mockImplementation(async (req: CreateLogEntryRequest) => makeEntry(req.callsign));
    h.getLogEntries.mockReset();
    h.getLogEntries.mockResolvedValue({ entries: [], totalCount: 0 });
    useLoggerStore.setState({
      entries: [],
      totalCount: 0,
      error: null,
      workedSummary: null,
      workedSummaryLoading: false,
      workedSummaryError: null,
    });
    useLogbookPluginStore.setState({ installed: false, live: false, probed: true });
  });

  it('resets the one-shot unavailable notice after the plugin becomes ready', async () => {
    await expect(useLoggerStore.getState().addLogEntry(makeRequest('K1ABC'))).resolves.toBeNull();
    expect(h.createLogEntry).not.toHaveBeenCalled();
    expect(useLoggerStore.getState().error).toBe('Install the Logbook plugin from Settings → Plugins');

    useLogbookPluginStore.setState({ installed: true, live: true, probed: true });
    await expect(useLoggerStore.getState().addLogEntry(makeRequest('K1ABC'))).resolves.toMatchObject({
      callsign: 'K1ABC',
    });
    expect(h.createLogEntry).toHaveBeenCalledTimes(1);
    expect(useLoggerStore.getState().error).toBeNull();

    useLogbookPluginStore.setState({ installed: false, live: false, probed: true });
    await expect(useLoggerStore.getState().addLogEntry(makeRequest('N9WAR'))).resolves.toBeNull();
    expect(h.createLogEntry).toHaveBeenCalledTimes(1);
    expect(useLoggerStore.getState().error).toBe('Install the Logbook plugin from Settings → Plugins');
  });
});
