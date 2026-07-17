// SPDX-License-Identifier: GPL-2.0-or-later

import {
  useCallback,
  useEffect,
  useRef,
  type KeyboardEvent as ReactKeyboardEvent,
  type PointerEvent as ReactPointerEvent,
} from 'react';
import { useConnectionStore } from '../state/connection-store';
import { useToolbarFavoritesStore } from '../state/toolbar-favorites-store';
import { createVfoNudgeController } from '../util/use-pan-tune-gesture';

const TRACKPAD_STEP_PX = 44;

function formatStepHz(hz: number): string {
  if (hz >= 1_000 && hz % 1_000 === 0) return `${hz / 1_000} kHz`;
  return `${hz} Hz`;
}

export function MobileFrequencyTrackpad() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const stepHz = useToolbarFavoritesStore((s) => s.stepHz);
  const padRef = useRef<HTMLDivElement | null>(null);
  const controllerRef = useRef<ReturnType<typeof createVfoNudgeController> | null>(null);
  const dragRef = useRef<{
    pointerId: number;
    lastX: number;
    accumPx: number;
  } | null>(null);

  useEffect(() => {
    const controller = createVfoNudgeController('A');
    controllerRef.current = controller;
    return () => {
      controller.cancel();
      if (controllerRef.current === controller) controllerRef.current = null;
    };
  }, []);

  const nudgeSteps = useCallback(
    (steps: number) => {
      if (!connected || steps === 0) return;
      controllerRef.current?.nudgeVfo(steps * useToolbarFavoritesStore.getState().stepHz);
    },
    [connected],
  );

  const resetPad = useCallback(() => {
    padRef.current?.classList.remove('is-dragging');
  }, []);

  const onPointerDown = useCallback(
    (event: ReactPointerEvent<HTMLDivElement>) => {
      if (!connected || event.button !== 0) return;
      event.preventDefault();
      const target = event.currentTarget;
      dragRef.current = {
        pointerId: event.pointerId,
        lastX: event.clientX,
        accumPx: 0,
      };
      target.classList.add('is-dragging');
      try { target.setPointerCapture(event.pointerId); } catch { /* ok */ }
    },
    [connected],
  );

  const onPointerMove = useCallback(
    (event: ReactPointerEvent<HTMLDivElement>) => {
      const drag = dragRef.current;
      if (!drag || drag.pointerId !== event.pointerId) return;
      event.preventDefault();
      const dx = event.clientX - drag.lastX;
      drag.lastX = event.clientX;
      drag.accumPx += dx;

      const steps = Math.trunc(drag.accumPx / TRACKPAD_STEP_PX);
      if (steps === 0) return;
      drag.accumPx -= steps * TRACKPAD_STEP_PX;
      nudgeSteps(steps);
    },
    [nudgeSteps],
  );

  const onPointerUp = useCallback(
    (event: ReactPointerEvent<HTMLDivElement>) => {
      const drag = dragRef.current;
      if (!drag || drag.pointerId !== event.pointerId) return;
      dragRef.current = null;
      try {
        if (event.currentTarget.hasPointerCapture(event.pointerId)) {
          event.currentTarget.releasePointerCapture(event.pointerId);
        }
      } catch {
        /* ok */
      }
      resetPad();
    },
    [resetPad],
  );

  const onKeyDown = useCallback(
    (event: ReactKeyboardEvent<HTMLDivElement>) => {
      if (event.key === 'ArrowLeft') {
        event.preventDefault();
        nudgeSteps(-1);
      } else if (event.key === 'ArrowRight') {
        event.preventDefault();
        nudgeSteps(1);
      }
    },
    [nudgeSteps],
  );

  return (
    <div className="m-frequency-trackpad" aria-label="Frequency trackpad">
      <div
        ref={padRef}
        className="m-trackpad-pad"
        role="button"
        tabIndex={connected ? 0 : -1}
        aria-disabled={!connected}
        aria-label={`Tune frequency by ${formatStepHz(stepHz)} steps`}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerUp}
        onKeyDown={onKeyDown}
      >
        <span className="m-trackpad-label">TUNE</span>
        <span className="m-trackpad-value">{formatStepHz(stepHz)}</span>
      </div>
    </div>
  );
}
