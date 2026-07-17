// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { useCallback } from 'react';
import { SlidersHorizontal } from 'lucide-react';
import { useEasterEggStore } from '../state/easter-egg-store';
import { useSignalJammerStore } from '../state/signal-jammer-store';

export function QrmButton() {
  const enabled = useSignalJammerStore((s) => s.enabled);
  const active = useSignalJammerStore((s) => s.active);
  const toggleActive = useSignalJammerStore((s) => s.toggleActive);

  const click = useCallback(() => {
    toggleActive();
  }, [toggleActive]);

  if (!enabled) return null;

  return (
    <button
      type="button"
      onClick={click}
      className={`btn sm ${active ? 'active' : ''}`}
      title={active ? 'TX testing audio on' : 'TX testing audio off'}
      aria-pressed={active}
      aria-label={active ? 'Disable TX testing audio' : 'Enable TX testing audio'}
    >
      <span className={`led ${active ? 'on' : ''}`} style={{ marginRight: 6 }} />
      {active ? 'ON' : 'OFF'}
    </button>
  );
}

export function QrmPanelToggleButton() {
  const enabled = useSignalJammerStore((s) => s.enabled);
  const open = useEasterEggStore((s) => s.signalJammerPopoverOpen);
  const toggle = useEasterEggStore((s) => s.toggleSignalJammerPopover);

  const click = useCallback(() => {
    toggle();
  }, [toggle]);

  if (!enabled) return null;

  return (
    <button
      type="button"
      onClick={click}
      className={`btn sm ${open ? 'active' : ''}`}
      title={open ? 'Close TX Testing Tools' : 'Open TX Testing Tools'}
      aria-expanded={open}
      aria-controls="tx-testing-tools-popout"
      aria-label={open ? 'Close TX Testing Tools' : 'Open TX Testing Tools'}
    >
      <SlidersHorizontal size={14} strokeWidth={2.25} aria-hidden />
    </button>
  );
}
