// SPDX-License-Identifier: GPL-2.0-or-later
//
// Mobile shell viewport gate. The desktop host can also be reached from a
// normal browser on loopback; that surface should stay in the desktop shell
// unless the operator explicitly asks for a mobile preview.

import { useEffect, useState } from 'react';

const MOBILE_QUERY = '(max-width: 900px)';

type ViewportOverride = 'desktop' | 'mobile' | null;

export type MobileViewportOptions = {
  forceDesktop?: boolean;
};

function viewportOverride(): ViewportOverride {
  if (typeof window === 'undefined') return null;
  const params = new URLSearchParams(window.location.search);
  if (params.get('desktop') === '1') return 'desktop';
  if (params.get('mobile') === '1') return 'mobile';
  return null;
}

function shouldForceDesktop(forceDesktop: boolean): boolean {
  const override = viewportOverride();
  if (override === 'desktop') return true;
  if (override === 'mobile') return false;
  return forceDesktop;
}

function computeMobile(forceDesktop: boolean): boolean {
  if (typeof window === 'undefined') return false;
  const override = viewportOverride();
  if (override === 'desktop') return false;
  if (override === 'mobile') return true;
  if (forceDesktop) return false;
  return window.matchMedia(MOBILE_QUERY).matches;
}

export function useDesktopViewportLock(forceDesktop: boolean): void {
  useEffect(() => {
    if (typeof document === 'undefined') return;
    if (shouldForceDesktop(forceDesktop)) {
      document.documentElement.setAttribute('data-force-desktop', '1');
    } else {
      document.documentElement.removeAttribute('data-force-desktop');
    }
  }, [forceDesktop]);
}

// Reactive viewport check. `?mobile=1` forces the mobile shell on for desktop
// previews; `?desktop=1` forces it off so the desktop layout survives narrow
// windows. The desktop host also forces the desktop shell for loopback browser
// views, while LAN clients still follow the breakpoint. `?mobile=1` remains an
// explicit override for previewing the mobile shell.
export function useIsMobileViewport(
  options: MobileViewportOptions = {},
): boolean {
  const forceDesktop = options.forceDesktop === true;
  const [mobile, setMobile] = useState<boolean>(() => computeMobile(forceDesktop));

  useEffect(() => {
    if (typeof window === 'undefined') return;

    if (shouldForceDesktop(forceDesktop)) {
      setMobile(false);
      return;
    }

    const override = viewportOverride();
    if (override === 'mobile') {
      setMobile(true);
      return;
    }

    const mq = window.matchMedia(MOBILE_QUERY);
    const sync = (matches: boolean) => setMobile(matches);
    sync(mq.matches);

    const onChange = (e: MediaQueryListEvent) => sync(e.matches);
    mq.addEventListener('change', onChange);
    return () => mq.removeEventListener('change', onChange);
  }, [forceDesktop]);

  return mobile;
}
