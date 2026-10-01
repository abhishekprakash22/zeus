// SPDX-License-Identifier: GPL-2.0-or-later
//
// Touch keys that act on finger-lift instead of the browser's synthetic click.
//
// The kiosk (WebKitGTK) can hold a touch's click while it decides whether the
// tap is a double-tap or gesture, so MOX/TUNE/2TON lit and keyed late from the
// screen while the front panel was instant. A touch/pen tap now acts on
// pointerup (finger within TAP_SLOP_PX of where it landed, no pointercancel);
// the late synthetic click that follows is swallowed so the key never toggles
// twice. Mouse and keyboard keep the ordinary click path.

import { useMemo, useRef } from 'react';

export const TAP_SLOP_PX = 12;
// Backstop: a swallow owed for longer than this is forgotten, so a click the
// engine never delivered can't eat a later keyboard activation.
export const SWALLOW_WINDOW_MS = 2000;

export type TapPointer = {
  pointerId: number;
  pointerType: string;
  isPrimary: boolean;
  clientX: number;
  clientY: number;
};

export type FastTap = {
  pointerDown(e: TapPointer): void;
  pointerUp(e: TapPointer, disabled: boolean): void;
  pointerCancel(): void;
  click(): void;
};

export function createFastTap(action: () => void, now: () => number = () => performance.now()): FastTap {
  let down: { id: number; x: number; y: number } | null = null;
  let owedSwallows = 0;
  let lastTouchActionAt = 0;

  return {
    pointerDown(e) {
      if (e.pointerType === 'mouse' || !e.isPrimary) return;
      down = { id: e.pointerId, x: e.clientX, y: e.clientY };
    },
    pointerUp(e, disabled) {
      const d = down;
      down = null;
      if (!d || d.id !== e.pointerId || disabled) return;
      if (Math.hypot(e.clientX - d.x, e.clientY - d.y) > TAP_SLOP_PX) return;
      const t = now();
      if (t - lastTouchActionAt > SWALLOW_WINDOW_MS) owedSwallows = 0;
      owedSwallows += 1;
      lastTouchActionAt = t;
      action();
    },
    pointerCancel() {
      down = null;
    },
    click() {
      if (owedSwallows > 0 && now() - lastTouchActionAt <= SWALLOW_WINDOW_MS) {
        owedSwallows -= 1;
        return;
      }
      owedSwallows = 0;
      action();
    },
  };
}

type ButtonPointerEvent = React.PointerEvent<HTMLButtonElement>;

/** Spread onto a <button>: replaces onClick for keys that must feel instant on touch. */
export function useFastTap(action: () => void) {
  const actionRef = useRef(action);
  actionRef.current = action;
  return useMemo(() => {
    const tap = createFastTap(() => actionRef.current());
    return {
      onPointerDown: (e: ButtonPointerEvent) => tap.pointerDown(e),
      onPointerUp: (e: ButtonPointerEvent) => tap.pointerUp(e, e.currentTarget.disabled),
      onPointerCancel: () => tap.pointerCancel(),
      onClick: () => tap.click(),
    };
  }, []);
}
