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

import { useCallback, useEffect, useState } from 'react';

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

  useEffect(() => {
    const onChange = () => {
      const now = !!document.fullscreenElement;
      setFull(now);
      // Any state change after load is operator intent (button, Esc, F11) —
      // remember it.
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
      armed = false;
      cleanup();
      if (!document.fullscreenElement && readPref())
        void document.documentElement.requestFullscreen().catch(() => {});
    };
    const cleanup = () => {
      window.removeEventListener('pointerdown', restore, true);
      window.removeEventListener('keydown', restore, true);
    };
    window.addEventListener('pointerdown', restore, true);
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
  useEffect(() => {
    let strikes = 0;
    const id = window.setInterval(() => {
      if (!document.fullscreenElement) {
        strikes = 0;
        return;
      }
      const oversize =
        window.innerWidth > window.screen.width * 1.02 ||
        window.innerHeight > window.screen.height * 1.02;
      if (!oversize) {
        strikes = 0;
        return;
      }
      strikes++;
      if (strikes < 2) return; // two consecutive seconds = not a transient
      strikes = 0;
      console.info(
        '[fullscreen] surface larger than screen (%dx%d > %dx%d) — exiting stale fullscreen; next tap re-enters',
        window.innerWidth, window.innerHeight, window.screen.width, window.screen.height,
      );
      void document.exitFullscreen().catch(() => {});
      armRestore();
    }, 1000);
    return () => window.clearInterval(id);
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
