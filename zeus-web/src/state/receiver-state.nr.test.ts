// SPDX-License-Identifier: GPL-2.0-or-later
//
// Per-receiver NR: a receiver without its own NR follows RX1 (the
// pre-feature contract, and what an older server sends); one with its own
// stops tracking RX1. Pinned here because every consumer — the deck NR key,
// the flag chips, the pipeline's secondary apply — rests on this one rule.

import { beforeEach, describe, expect, it } from 'vitest';
import { useConnectionStore } from './connection-store';
import type { NrConfigDto, ReceiverDto } from '../api/client';
import { getReceiverNr } from './receiver-state';

function rx(index: number, patch: Partial<ReceiverDto> = {}): ReceiverDto {
  return {
    index, enabled: true, adcSource: 0, vfoHz: 14_000_000, mode: 'USB',
    filterLowHz: 100, filterHighHz: 2800, filterPresetName: 'VAR1',
    afGainDb: 0, sampleRateHz: 192_000, muted: false, ...patch,
  };
}

const rx1Nr = { nrMode: 'Emnr' } as unknown as NrConfigDto;
const rx2Nr = { nrMode: 'Off' } as unknown as NrConfigDto;

describe('getReceiverNr', () => {
  beforeEach(() => {
    useConnectionStore.setState({ nr: rx1Nr, receivers: [rx(0), rx(1)] } as never);
  });

  it('RX1 reads the flat field', () => {
    expect(getReceiverNr(useConnectionStore.getState(), 0)).toBe(rx1Nr);
  });

  it('a secondary without its own NR follows RX1', () => {
    expect(getReceiverNr(useConnectionStore.getState(), 1)).toBe(rx1Nr);
  });

  it('a secondary with its own NR stops following RX1', () => {
    useConnectionStore.setState({ receivers: [rx(0), rx(1, { nr: rx2Nr })] } as never);
    const s = useConnectionStore.getState();
    expect(getReceiverNr(s, 1)).toBe(rx2Nr);
    expect(getReceiverNr(s, 0)).toBe(rx1Nr);
  });

  it('a receiver missing from the array follows RX1 rather than crashing', () => {
    expect(getReceiverNr(useConnectionStore.getState(), 3)).toBe(rx1Nr);
  });
});
