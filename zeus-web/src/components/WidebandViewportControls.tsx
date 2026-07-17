// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';
import { Maximize2, ZoomIn, ZoomOut } from 'lucide-react';
import { selectDisplaySlice, useDisplayStore } from '../state/display-store';
import type { ReceiverKey } from '../state/receiver-state';
import * as viewCenter from '../state/view-center';
import * as viewZoom from '../state/view-zoom';
import {
  WIDEBAND_CENTER_HZ,
  WIDEBAND_LOCAL_ZOOM_MAX,
  clampPointerFraction,
  formatWidebandRangeLabel,
  formatWidebandZoomRatio,
  isWidebandDisplayGeometry,
  resolveSpectrumViewport,
  zoomWidebandViewport,
} from '../util/wideband-view';

type WidebandViewportControlsProps = {
  containerRef: RefObject<HTMLElement | null>;
  receiver?: ReceiverKey;
};

function readSource(receiver: ReceiverKey) {
  const slice = selectDisplaySlice(useDisplayStore.getState(), receiver);
  return {
    width: slice.width || slice.panDb?.length || slice.wfDb?.length || 0,
    centerHz: Number(slice.centerHz),
    hzPerPixel: slice.hzPerPixel,
  };
}

export function WidebandViewportControls({
  containerRef,
  receiver = 'A',
}: WidebandViewportControlsProps) {
  const controlsRef = useRef<HTMLDivElement | null>(null);
  const lastPointerFractionRef = useRef(0.5);
  const sourceWidth = useDisplayStore((s) => {
    const slice = selectDisplaySlice(s, receiver);
    return slice.width || slice.panDb?.length || slice.wfDb?.length || 0;
  });
  const sourceHzPerPixel = useDisplayStore((s) => selectDisplaySlice(s, receiver).hzPerPixel);
  const sourceCenterHz = useDisplayStore((s) => Number(selectDisplaySlice(s, receiver).centerHz));
  const [, forceRender] = useState(0);
  const wideband = isWidebandDisplayGeometry({
    width: sourceWidth,
    hzPerPixel: sourceHzPerPixel,
    centerHz: sourceCenterHz,
  });

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    const onMove = (e: PointerEvent) => {
      if (e.pointerType !== 'mouse') return;
      const targetNode = e.target instanceof Node ? e.target : null;
      if (targetNode && controlsRef.current?.contains(targetNode)) return;
      const rect = container.getBoundingClientRect();
      if (rect.width <= 0) return;
      lastPointerFractionRef.current = clampPointerFraction((e.clientX - rect.left) / rect.width);
    };
    container.addEventListener('pointermove', onMove);
    return () => container.removeEventListener('pointermove', onMove);
  }, [containerRef]);

  useEffect(() => {
    const vc = viewCenter.viewCenterFor(receiver);
    let lastCenterHz = vc.isInitialized() ? vc.getTargetCenterHz() : NaN;
    let lastHzPerPixel = viewZoom.getTargetHzPerPixel();
    const rerender = () => {
      const nextCenterHz = vc.isInitialized() ? vc.getTargetCenterHz() : NaN;
      const nextHzPerPixel = viewZoom.getTargetHzPerPixel();
      const centerEpsHz = Math.max(1, Math.abs(nextHzPerPixel) * 0.5);
      const zoomEps = Math.max(1e-9, Math.abs(nextHzPerPixel) * 0.002);
      const centerChanged =
        !Number.isFinite(lastCenterHz) ||
        !Number.isFinite(nextCenterHz) ||
        Math.abs(nextCenterHz - lastCenterHz) > centerEpsHz;
      const zoomChanged =
        !Number.isFinite(lastHzPerPixel) ||
        !Number.isFinite(nextHzPerPixel) ||
        Math.abs(nextHzPerPixel - lastHzPerPixel) > zoomEps;
      if (!centerChanged && !zoomChanged) return;
      lastCenterHz = nextCenterHz;
      lastHzPerPixel = nextHzPerPixel;
      forceRender((v) => (v + 1) & 0xffff);
    };
    const unsubVc = vc.subscribe(rerender);
    const unsubVz = viewZoom.subscribe(rerender);
    return () => {
      unsubVc();
      unsubVz();
    };
  }, [receiver]);

  const applyZoom = useCallback(
    (zoomDelta: number) => {
      const source = readSource(receiver);
      if (
        !isWidebandDisplayGeometry({
          width: source.width,
          hzPerPixel: source.hzPerPixel,
          centerHz: source.centerHz,
        })
      ) {
        return;
      }
      const vc = viewCenter.viewCenterFor(receiver);
      const currentCenterHz = vc.isInitialized() ? vc.getTargetCenterHz() : source.centerHz;
      const currentHzPerPixel = viewZoom.isInitialized()
        ? viewZoom.getTargetHzPerPixel()
        : source.hzPerPixel;
      const next = zoomWidebandViewport({
        width: source.width,
        sourceHzPerPixel: source.hzPerPixel,
        currentHzPerPixel,
        currentCenterHz,
        pointerFraction: lastPointerFractionRef.current,
        zoomDelta,
      });
      if (!next) return;
      viewZoom.setTarget(next.hzPerPixel);
      vc.nudgeTargetHz(next.centerHz - currentCenterHz);
      forceRender((v) => (v + 1) & 0xffff);
    },
    [receiver],
  );

  useEffect(() => {
    const el = controlsRef.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      const primary = e.deltaY !== 0 ? e.deltaY : e.deltaX;
      if (primary === 0) return;
      e.preventDefault();
      e.stopPropagation();
      applyZoom(primary > 0 ? 1 : -1);
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, [applyZoom, wideband]);

  const resetFullBand = useCallback(() => {
    const source = readSource(receiver);
    if (
      !isWidebandDisplayGeometry({
        width: source.width,
        hzPerPixel: source.hzPerPixel,
        centerHz: source.centerHz,
      })
    ) {
      return;
    }
    const vc = viewCenter.viewCenterFor(receiver);
    const currentCenterHz = vc.isInitialized() ? vc.getTargetCenterHz() : source.centerHz;
    viewZoom.setTarget(source.hzPerPixel);
    vc.nudgeTargetHz(WIDEBAND_CENTER_HZ - currentCenterHz);
    forceRender((v) => (v + 1) & 0xffff);
  }, [receiver]);

  if (!wideband) return null;

  const vc = viewCenter.viewCenterFor(receiver);
  const viewport = resolveSpectrumViewport({
    width: sourceWidth,
    sourceCenterHz,
    sourceHzPerPixel,
    viewCenterHz: vc.isInitialized() ? vc.getTargetCenterHz() : undefined,
    viewHzPerPixel: viewZoom.isInitialized() ? viewZoom.getTargetHzPerPixel() : undefined,
  });
  if (!viewport?.wideband) return null;

  const startHz = viewport.centerHz - viewport.spanHz / 2;
  const endHz = viewport.centerHz + viewport.spanHz / 2;
  const atFullSpan = viewport.hzPerPixel >= sourceHzPerPixel * 0.998;
  const atMaxZoom = viewport.hzPerPixel <= (sourceHzPerPixel / WIDEBAND_LOCAL_ZOOM_MAX) * 1.002;

  return (
    <div
      ref={controlsRef}
      className="wideband-view-controls"
      role="toolbar"
      aria-label="Wideband spectrum zoom"
    >
      <div className="wideband-view-controls__range" aria-live="polite">
        {formatWidebandRangeLabel(startHz, endHz)}
      </div>
      <div className="wideband-view-controls__zoom">
        {formatWidebandZoomRatio(sourceHzPerPixel, viewport.hzPerPixel)}
      </div>
      <button
        type="button"
        className="wideband-view-controls__button"
        onClick={() => applyZoom(-1)}
        disabled={atFullSpan}
        title="Zoom wideband view out"
        aria-label="Zoom wideband view out"
      >
        <ZoomOut size={14} strokeWidth={2.3} aria-hidden />
      </button>
      <button
        type="button"
        className="wideband-view-controls__button"
        onClick={() => applyZoom(1)}
        disabled={atMaxZoom}
        title="Zoom wideband view in at the last crosshair position"
        aria-label="Zoom wideband view in"
      >
        <ZoomIn size={14} strokeWidth={2.3} aria-hidden />
      </button>
      <button
        type="button"
        className="wideband-view-controls__button"
        onClick={resetFullBand}
        disabled={atFullSpan}
        title="Reset wideband view to 0-60 MHz"
        aria-label="Reset wideband view to 0-60 MHz"
      >
        <Maximize2 size={14} strokeWidth={2.3} aria-hidden />
      </button>
    </div>
  );
}
