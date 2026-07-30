// SPDX-License-Identifier: GPL-2.0-or-later
//
// FullscreenButton — topbar toggle for browser full-screen (edge-to-edge, no
// tabs / address bar) via the Fullscreen API. This is the operator-convenience
// sibling of the launcher-based kiosk (installers/pi-kiosk): same visual
// result, togglable at will, Esc also exits. For a LOCKED appliance (no
// escape) keep using the --kiosk launcher — pages cannot enter that mode.
// State tracks the fullscreenchange event so the label stays truthful when
// the operator exits with Esc or F11 instead of the button.

import { useEffect, useState } from 'react';

export function FullscreenButton() {
  const [full, setFull] = useState<boolean>(!!document.fullscreenElement);

  useEffect(() => {
    const onChange = () => setFull(!!document.fullscreenElement);
    document.addEventListener('fullscreenchange', onChange);
    return () => document.removeEventListener('fullscreenchange', onChange);
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
