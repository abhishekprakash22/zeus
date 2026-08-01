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

import { useEffect, useState } from 'react';

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
  useEffect(() => {
    if (!readPref() || document.fullscreenElement) return;
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
