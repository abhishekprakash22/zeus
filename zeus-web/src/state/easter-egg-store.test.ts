// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — verify the hidden HARDWARE folder unlock (brand-mark easter egg):
// locked by default, unlocks only after HARDWARE_UNLOCK_CLICKS bolt taps.

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import {
  HARDWARE_UNLOCK_CLICKS,
  useEasterEggStore,
} from './easter-egg-store';

function reset() {
  useEasterEggStore.setState({
    hardwareUnlocked: false,
    signalJammerPopoverOpen: false,
    boltClicks: 0,
  });
}

describe('easter-egg-store — hidden HARDWARE and QRM unlock', () => {
  beforeEach(reset);
  afterEach(reset);

  it('starts locked', () => {
    expect(useEasterEggStore.getState().hardwareUnlocked).toBe(false);
    expect(useEasterEggStore.getState().signalJammerPopoverOpen).toBe(false);
  });

  it('stays locked until the click threshold is reached', () => {
    const { registerBoltClick } = useEasterEggStore.getState();
    for (let i = 1; i < HARDWARE_UNLOCK_CLICKS; i++) {
      registerBoltClick();
      expect(useEasterEggStore.getState().hardwareUnlocked).toBe(false);
    }
    // The threshold-th click unlocks.
    registerBoltClick();
    expect(useEasterEggStore.getState().hardwareUnlocked).toBe(true);
    expect(useEasterEggStore.getState().signalJammerPopoverOpen).toBe(true);
  });

  it('stays unlocked and toggles the QRM popout on further clicks', () => {
    const { registerBoltClick } = useEasterEggStore.getState();
    for (let i = 0; i < HARDWARE_UNLOCK_CLICKS; i++) registerBoltClick();
    expect(useEasterEggStore.getState().hardwareUnlocked).toBe(true);
    expect(useEasterEggStore.getState().signalJammerPopoverOpen).toBe(true);

    registerBoltClick();
    expect(useEasterEggStore.getState().hardwareUnlocked).toBe(true);
    expect(useEasterEggStore.getState().signalJammerPopoverOpen).toBe(false);
  });

  it('lets the top-bar QRM control reopen the popout after it was closed', () => {
    const s = useEasterEggStore.getState();
    s.registerBoltClick();
    s.registerBoltClick();
    s.registerBoltClick();
    s.registerBoltClick();

    useEasterEggStore.getState().closeSignalJammerPopover();
    expect(useEasterEggStore.getState().signalJammerPopoverOpen).toBe(false);

    useEasterEggStore.getState().toggleSignalJammerPopover();
    expect(useEasterEggStore.getState().signalJammerPopoverOpen).toBe(true);
  });
});
