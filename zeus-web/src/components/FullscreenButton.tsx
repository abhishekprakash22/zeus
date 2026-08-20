// SPDX-License-Identifier: GPL-2.0-or-later
//
// FullscreenButton — topbar toggle for browser full-screen (edge-to-edge, no
// tabs / address bar) via the Fullscreen API, WITH PERSISTENCE.
//
// The preference survives restarts on two layers:
//   1. localStorage (`zeus.fullscreen.preferred`) — in any browser, if the
//      operator was in full screen last session, the FIRST user gesture
//      (click/keypress anywhere) re-enters it. A silent restore at load is
//      impossible by design: browsers gate requestFullscreen() behind a user
//      gesture, so first-gesture restore is the strongest legal form.
//   2. A backend kiosk marker (POST /api/ui/kiosk-fullscreen) — the AppImage
//      kiosk launcher reads it and starts Chromium with --start-fullscreen,
//      giving a ZERO-gesture restore on the appliance. Fire-and-forget: in
//      plain-browser deployments the endpoint may be absent and that's fine.
//
// Esc / F11 exits count as the operator's decision and update the preference —
// the button label stays truthful via the fullscreenchange event either way.

import { useCallback, useEffect, useRef, useState } from 'react';
import { G2_FRAME_H, G2_FRAME_W, useG2WorkspaceStore } from '../state/g2-workspace-store';

const PREF_KEY = 'zeus.fullscreen.preferred';

function readPref(): boolean {
  try { return localStorage.getItem(PREF_KEY) === '1'; } catch { return false; }
}

function writePref(on: boolean) {
  try { localStorage.setItem(PREF_KEY, on ? '1' : '0'); } catch { /* private mode */ }
  // Kiosk marker for the AppImage launcher (zero-gesture restore).
  void fetch('/api/ui/kiosk-fullscreen', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ on }),
  }).catch(() => {});
}

export function FullscreenButton() {
  const [full, setFull] = useState<boolean>(!!document.fullscreenElement);

  // Page teardown flag. On reload / navigation (every INSTALL & RESTART
  // ritual included) Chromium exits fullscreen and fires fullscreenchange
  // while the page is still alive — the listener below used to record that
  // forced exit as operator intent and stomp the preference to 'off', so
  // fullscreen never survived a restart (field report: "no persistence").
  // Teardown exits are nobody's intent; the preference keeps its value.
  const unloadingRef = useRef(false);
  useEffect(() => {
    const markUnloading = () => {
      unloadingRef.current = true;
    };
    window.addEventListener('pagehide', markUnloading);
    window.addEventListener('beforeunload', markUnloading);
    return () => {
      window.removeEventListener('pagehide', markUnloading);
      window.removeEventListener('beforeunload', markUnloading);
    };
  }, []);

  useEffect(() => {
    const onChange = () => {
      const now = !!document.fullscreenElement;
      setFull(now);
      // A LIVE state change is operator intent (button, Esc, F11) —
      // remember it. An exit during page teardown is not (see above).
      if (unloadingRef.current) return;
      writePref(now);
    };
    document.addEventListener('fullscreenchange', onChange);
    return () => document.removeEventListener('fullscreenchange', onChange);
  }, []);

  // First-gesture restore: if the operator preferred full screen last session
  // and we're not in it (plain-browser launch, or the kiosk flag was
  // unavailable), the first click or keypress anywhere re-enters it, once.
  // armRestore is reusable: the stale-fullscreen watchdog below re-arms it
  // after an automatic exit.
  const armRestore = useCallback(() => {
    let armed = true;
    const restore = () => {
      if (!armed) return;
      // Field evidence (G2 kiosk): requestFullscreen from the oversized
      // windowed state IS the cure — chromium re-configures the surface
      // against the true output. The earlier stale-geometry refusal here
      // was defending against a loop this platform doesn't have, and it
      // blocked the recovery. Re-enter unconditionally; the watchdog's
      // cycle cap (below) is the loop protection.
      armed = false;
      cleanup();
      if (!document.fullscreenElement && readPref())
        void document.documentElement.requestFullscreen().catch((err) =>
          // A silent catch here hid the real failure for days: on the G2
          // touchscreen, taps were rejected for missing user activation and
          // nobody ever heard about it. Rejections now speak.
          console.info('[fullscreen] re-enter rejected: %s', (err as Error)?.message ?? err),
        );
    };
    const cleanup = () => {
      window.removeEventListener('pointerup', restore, true);
      window.removeEventListener('keydown', restore, true);
    };
    // pointerUP, not pointerdown: Chromium grants transient user activation
    // on keydown / mousedown / pointerup / touchend. A mouse tap worked only
    // because mousedown rode along; a TOUCHSCREEN tap delivers pointerdown
    // with no activation yet, so requestFullscreen was rejected — silently —
    // on every tap of the G2 panel ('tap does nothing after Esc').
    window.addEventListener('pointerup', restore, true);
    window.addEventListener('keydown', restore, true);
    return cleanup;
  }, []);

  useEffect(() => {
    if (!readPref() || document.fullscreenElement) return;
    return armRestore();
  }, [armRestore]);

  // Stale-fullscreen watchdog (G2 kiosk field report): fullscreen engaged
  // while the compositor was still settling the display mode leaves the
  // fullscreen surface LARGER than the physical screen — the window itself
  // overflows the panel, and no amount of workspace math can fix a window
  // that is bigger than the display. The browser keeps the stale geometry
  // until fullscreen is re-entered. We cannot re-enter programmatically
  // (requestFullscreen is gesture-gated by design), but we CAN detect the
  // impossible state (viewport larger than the screen while fullscreen),
  // exit automatically, and re-arm the first-gesture restore — so one tap
  // anywhere re-enters fullscreen at the settled, correct size.
  // Truth anchor for "the surface is bigger than the glass". Field evidence:
  // the boot-stale geometry is COHERENT inside the browser — viewport AND
  // window.screen both report the transient large mode, so comparing them
  // detects nothing. When the operator has declared the physical panel (the
  // G2 1280x800 frame option), compare against THAT; otherwise fall back to
  // window.screen (which still catches the incoherent variant).
  const g2Frame = useG2WorkspaceStore((s) => s.g2Frame);
  useEffect(() => {
    let strikes = 0;
    let cycles = 0; // loop protection: give up after a few auto-exits
    const id = window.setInterval(() => {
      if (!document.fullscreenElement) {
        strikes = 0;
        return;
      }
      const physW = g2Frame ? G2_FRAME_W : window.screen.width;
      const physH = g2Frame ? G2_FRAME_H : window.screen.height;
      const oversize =
        window.innerWidth > physW * 1.02 || window.innerHeight > physH * 1.02;
      if (!oversize) {
        strikes = 0;
        return;
      }
      strikes++;
      if (strikes < 2) return; // two consecutive seconds = not a transient
      strikes = 0;
      if (cycles >= 3) return; // stop cycling; the manual button remains
      cycles++;
      console.info(
        '[fullscreen] surface %dx%d exceeds physical %dx%d (screen reports %dx%d) — exiting stale fullscreen; next tap re-enters',
        window.innerWidth, window.innerHeight, physW, physH,
        window.screen.width, window.screen.height,
      );
      void document.exitFullscreen().catch(() => {});
      armRestore();
    }, 1000);
    return () => window.clearInterval(id);
  }, [armRestore, g2Frame]);

  // Manual Esc parity: any exit from fullscreen while the preference is
  // still 'on' arms the one-tap restore — so even a hand-pressed Esc is
  // followed by tap-to-re-enter, never drag-to-find-the-button.
  useEffect(() => {
    const onChange = () => {
      if (!document.fullscreenElement && readPref()) armRestore();
    };
    document.addEventListener('fullscreenchange', onChange);
    return () => document.removeEventListener('fullscreenchange', onChange);
  }, [armRestore]);

  const toggle = () => {
    if (document.fullscreenElement) {
      void document.exitFullscreen().catch(() => {});
    } else {
      void document.documentElement.requestFullscreen().catch(() => {});
    }
  };

  return (
    <button
      type="button"
      className={`btn sm${full ? ' active' : ''}`}
      onClick={toggle}
      title={full ? 'Exit full screen (Esc also works)' : 'Full screen — hide the browser chrome'}
      aria-pressed={full}
    >
      {full ? 'EXIT FS' : 'FULL SCR'}
    </button>
  );
}
