// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  answerSupportAgreement,
  getSupportAvailability,
  getSupportStatus,
  setSupportAvailability,
  approveSupportRequest,
} from './support';

function mockFetchOnce(body: unknown, ok = true, status = 200) {
  const res = {
    ok,
    status,
    statusText: ok ? 'OK' : 'Error',
    json: async () => body,
  } as Response;
  return vi.spyOn(globalThis, 'fetch').mockResolvedValue(res);
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('getSupportAvailability', () => {
  it('coerces a well-formed response', async () => {
    mockFetchOnce({
      available: true,
      autoShareCrashes: true,
      agreementVersion: 1,
      currentAgreementVersion: 2,
    });
    const s = await getSupportAvailability();
    expect(s).toEqual({
      available: true,
      autoShareCrashes: true,
      agreementVersion: 1,
      currentAgreementVersion: 2,
    });
  });

  it('defaults missing flags and versions to false/zero', async () => {
    mockFetchOnce({});
    const s = await getSupportAvailability();
    expect(s).toEqual({
      available: false,
      autoShareCrashes: false,
      agreementVersion: 0,
      currentAgreementVersion: 0,
    });
  });

  it('normalizes garbage agreement versions to zero', async () => {
    mockFetchOnce({
      agreementVersion: '1',
      currentAgreementVersion: -3,
    });
    const s = await getSupportAvailability();
    expect(s.agreementVersion).toBe(0);
    expect(s.currentAgreementVersion).toBe(0);
  });
});

describe('setSupportAvailability', () => {
  it('PUTs the body and returns the new state', async () => {
    const spy = mockFetchOnce({
      available: true,
      autoShareCrashes: false,
      agreementVersion: 1,
      currentAgreementVersion: 1,
    });
    const s = await setSupportAvailability({ available: true, autoShareCrashes: false });
    expect(s).toEqual({
      available: true,
      autoShareCrashes: false,
      agreementVersion: 1,
      currentAgreementVersion: 1,
    });
    const call = spy.mock.calls[0];
    expect(call?.[1]?.method).toBe('PUT');
  });
});

describe('answerSupportAgreement', () => {
  it('POSTs optIn and returns the normalized state', async () => {
    const spy = mockFetchOnce({
      available: true,
      autoShareCrashes: true,
      agreementVersion: 1,
      currentAgreementVersion: 1,
    });

    const s = await answerSupportAgreement(true);

    expect(s).toEqual({
      available: true,
      autoShareCrashes: true,
      agreementVersion: 1,
      currentAgreementVersion: 1,
    });
    const call = spy.mock.calls[0];
    expect(call?.[0]).toBe('/api/support/agreement');
    expect(call?.[1]?.method).toBe('POST');
    expect(call?.[1]?.body).toBe(JSON.stringify({ optIn: true }));
  });
});

describe('getSupportStatus', () => {
  it('normalizes pending requests and drops malformed entries', async () => {
    mockFetchOnce({
      available: true,
      autoShareCrashes: false,
      activeSessions: 2,
      pending: [
        {
          requestId: 'r1',
          adminCallsign: 'KB2UKA',
          createdAt: '2026-06-27T12:00:00Z',
          expiresAt: '2026-06-27T12:01:30Z',
        },
        { adminCallsign: 'no-id' }, // dropped (no requestId)
        42, // dropped (not an object)
      ],
    });
    const s = await getSupportStatus();
    expect(s.available).toBe(true);
    expect(s.activeSessions).toBe(2);
    expect(s.pending).toHaveLength(1);
    expect(s.pending[0]?.requestId).toBe('r1');
    expect(s.pending[0]?.adminCallsign).toBe('KB2UKA');
  });

  it('falls back to empty defaults on garbage', async () => {
    mockFetchOnce({});
    const s = await getSupportStatus();
    expect(s).toEqual({
      available: false,
      autoShareCrashes: false,
      pending: [],
      activeSessions: 0,
    });
  });
});

describe('approveSupportRequest', () => {
  it('returns true on a 200', async () => {
    mockFetchOnce({ ok: true });
    expect(await approveSupportRequest('r1')).toBe(true);
  });

  it('returns false on a 404', async () => {
    mockFetchOnce({ ok: false }, false, 404);
    expect(await approveSupportRequest('ghost')).toBe(false);
  });
});
