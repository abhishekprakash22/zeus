// SPDX-License-Identifier: GPL-2.0-or-later
//
// Screen LOCK keys (G2 drawer, desktop VFO panel, mobile) toggle the RADIO's
// VFO lock, the same one the G2 front-panel LOCK, TCI and MIDI set. The
// screen key used to flip a browser-only flag: it stopped this browser's own
// tuning gestures but not the panel's VFO knob, and the panel's LOCK never
// showed on screen. The flag now mirrors the radio (applyState syncs it).

import { setVfoLock } from '../api/client';
import { useConnectionStore } from './connection-store';
import { useVfoLockStore } from './vfo-lock-store';

export function toggleRadioVfoLock(): void {
  const lock = useVfoLockStore.getState();
  const next = !lock.locked;
  lock.setLocked(next); // optimistic; the reply's state confirms or corrects it
  setVfoLock(next)
    .then((s) => useConnectionStore.getState().applyState(s))
    .catch(() => useVfoLockStore.getState().setLocked(!next));
}
