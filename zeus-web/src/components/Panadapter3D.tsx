// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// WebGPU 3D panadapter surface. It preserves the existing panadapter contract:
// same frame consumer, same multi-RX receiver key, same pan/tune gesture, same
// overlays. The only difference is the GPU renderer underneath.

import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { planForFrame } from '../gl/frame-plan';
import { hexToRgbFloats } from '../gl/panadapter';
import { probeWebGpu, resetWebGpuProbe } from '../gl/webgpu/caps';
import { createPanSurfaceRenderer, type PanSurfaceRenderer, type PanSurfaceRowDomain } from '../gl/webgpu/pan-surface';
import { cancelDrawBusFrame, requestDrawBusFrame } from '../realtime/draw-bus';
import { registerFrameConsumer, selectDisplaySlice, useDisplayStore } from '../state/display-store';
import { useDisplaySettingsStore, shouldTxAutoRange } from '../state/display-settings-store';
import { useConnectionStore } from '../state/connection-store';
import { enhanceInto, useSignalEnhanceStore } from '../dsp/signal-estimator';
import { normalizeStitchedBins, stitchFloorShiftDb } from '../dsp/stitch-normalizer';
import { getReceiverVfoHz, KIWI_RECEIVER_INDEX, receiverLabel, rxIndexOf, type ReceiverKey } from '../state/receiver-state';
import * as viewCenter from '../state/view-center';
import * as viewZoom from '../state/view-zoom';
import { useTxStore } from '../state/tx-store';
import { usePanTuneGesture, type PanTuneGestureOptions } from '../util/use-pan-tune-gesture';
import { isWidebandDisplayGeometry } from '../util/wideband-view';
import { BandOverlay } from './BandOverlay';
import { FilterCursorOverlay } from './FilterCursorOverlay';
import { FreqAxis } from './FreqAxis';
import { PassbandOverlay } from './PassbandOverlay';
import { ImdReadings } from './ImdReadings';
import { DbScale } from './DbScale';
import { SpotOverlay } from './SpotOverlay';
import { ChatRosterOverlay } from './ChatRosterOverlay';
import { PeakMarkerOverlay } from './PeakMarkerOverlay';
import { NotchOverlay } from './NotchOverlay';
import { spectrumReceiverFilterColor } from './spectrumReceiverColor';
import { WidebandViewportControls } from './WidebandViewportControls';

type Panadapter3DProps = {
  receiver?: ReceiverKey;
  touchMode?: PanTuneGestureOptions['touchMode'];
  tuneReceiver?: PanTuneGestureOptions['tuneReceiver'];
  stitched?: boolean;
  foreground?: boolean;
  multiRx?: boolean;
  onUnavailable?: () => void;
};

type Status = 'probing' | 'ready' | 'unsupported';

export function Panadapter3D({
  receiver = 'A',
  touchMode = 'normal',
  tuneReceiver,
  stitched = false,
  foreground = true,
  multiRx = false,
  onUnavailable,
}: Panadapter3DProps = {}) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const onUnavailableRef = useRef(onUnavailable);
  onUnavailableRef.current = onUnavailable;
  const [status, setStatus] = useState<Status>('probing');
  const [reason, setReason] = useState('');
  const rxIndex = rxIndexOf(receiver);
  const vfoHz = useConnectionStore((s) => getReceiverVfoHz(s, receiver));
  const rxLabel = useConnectionStore((s) =>
    receiverLabel({ index: rxIndex, name: s.receivers.find((r) => r.index === rxIndex)?.name }),
  );
  const popEnabled = useSignalEnhanceStore((s) => s.popEnabled);
  const popRenderIntensity = useSignalEnhanceStore((s) => s.popRenderIntensity);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const popActive = popEnabled && !moxOn && !tunOn;
  const popIntensityCss = Math.max(0, Math.min(1, popRenderIntensity / 100)).toFixed(2);
  const receiverFilterColor = spectrumReceiverFilterColor(receiver);
  const displayWidth = useDisplayStore((s) => {
    const slice = selectDisplaySlice(s, receiver);
    return slice.width || slice.panDb?.length || slice.wfDb?.length || 0;
  });
  const displayHzPerPixel = useDisplayStore((s) => selectDisplaySlice(s, receiver).hzPerPixel);
  const displayCenterHz = useDisplayStore((s) => Number(selectDisplaySlice(s, receiver).centerHz));
  const widebandDisplay = isWidebandDisplayGeometry({
    width: displayWidth,
    hzPerPixel: displayHzPerPixel,
    centerHz: displayCenterHz,
  });

  useEffect(() => {
    const canvas = canvasRef.current;
    const container = containerRef.current;
    if (!canvas || !container) return;

    let renderer: PanSurfaceRenderer | null = null;
    let disposed = false;
    let lost = false;
    let lastSeqDrawn = -1;
    let wasWidebandDisplay = false;
    let lastPalette = '';
    let lastRawPan: Float32Array | null = null;
    let valueDomain = '';
    const enhScratch: Array<Float32Array | null> = [null, null];
    const stitchScratch: Array<Float32Array | null> = [null, null];
    let enhSlot = 0;
    let stitchSlot = 0;

    const domainNow = (): 'pop' | 'rx-db' | 'tx-db' => {
      const { moxOn, tunOn } = useTxStore.getState();
      if (moxOn || tunOn) return 'tx-db';
      return useSignalEnhanceStore.getState().popEnabled ? 'pop' : 'rx-db';
    };

    const buildRow = (raw: Float32Array): Float32Array => {
      const { moxOn, tunOn } = useTxStore.getState();
      const keyed = moxOn || tunOn;
      let source = raw;
      if (stitched && !keyed) {
        source = normalizeStitchedBins(
          raw,
          stitchScratch[stitchSlot] ?? null,
          stitchFloorShiftDb(receiver, 'pan'),
        );
        if (source !== raw) {
          stitchScratch[stitchSlot] = source;
          stitchSlot ^= 1;
        }
      }
      if (!useSignalEnhanceStore.getState().popEnabled || keyed) return source;
      let buf = enhScratch[enhSlot];
      if (!buf || buf.length !== source.length) {
        buf = new Float32Array(source.length);
        enhScratch[enhSlot] = buf;
      }
      enhanceInto(source, buf);
      enhSlot ^= 1;
      return buf;
    };

    const releaseFrameConsumer = registerFrameConsumer();
    const vc = viewCenter.viewCenterFor(receiver);
    const visualCenterHz = () =>
      vc.isInitialized()
        ? vc.getViewCenterHz()
        : Number(selectDisplaySlice(useDisplayStore.getState(), receiver).centerHz);

    const setRendererPalette = (id: string) => {
      if (!renderer || id === lastPalette) return;
      renderer.setColormap(id === 'pop' ? 'pop' : useDisplaySettingsStore.getState().colormap);
      lastPalette = id;
    };

    let pendingCanvasW = 0;
    let pendingCanvasH = 0;
    let appliedCanvasW = 0;
    let appliedCanvasH = 0;

    const measureCanvasSize = (entry?: ResizeObserverEntry) => {
      const rect = entry?.contentRect ?? container.getBoundingClientRect();
      const dpr = Math.min(1, window.devicePixelRatio || 1);
      return {
        w: Math.max(1, Math.round(rect.width * dpr)),
        h: Math.max(1, Math.round(rect.height * dpr)),
      };
    };

    const applyPendingResize = () => {
      if (!renderer) return;
      if (pendingCanvasW <= 0 || pendingCanvasH <= 0) {
        const next = measureCanvasSize();
        pendingCanvasW = next.w;
        pendingCanvasH = next.h;
      }
      if (pendingCanvasW === appliedCanvasW && pendingCanvasH === appliedCanvasH) return;
      appliedCanvasW = pendingCanvasW;
      appliedCanvasH = pendingCanvasH;
      canvas.width = appliedCanvasW;
      canvas.height = appliedCanvasH;
      renderer.resize(appliedCanvasW, appliedCanvasH);
    };

    const shouldClearForDomainChange = (prev: string, next: string): boolean => {
      if (!prev || prev === next) return false;
      if (prev === 'tx-db' || next === 'tx-db') return false;
      return true;
    };

    const ensureDomain = () => {
      if (!renderer) return;
      const dom = domainNow();
      if (dom === valueDomain) return;
      const shouldClear = shouldClearForDomainChange(valueDomain, dom);
      valueDomain = dom;
      if (shouldClear) renderer.clearHistory();
    };

    const pushFrame = (panDb: Float32Array, centerHz: bigint, hzPerPixel: number) => {
      if (!renderer) return;
      ensureDomain();
      const dom = domainNow();
      const rowDomain: PanSurfaceRowDomain = dom === 'tx-db' ? 'tx' : dom === 'pop' ? 'pop' : 'rx';
      lastRawPan = panDb;
      renderer.pushRow(buildRow(panDb), Number(centerHz), hzPerPixel, rowDomain);
    };

    const draw = () => {
      if (!renderer || lost) return;
      applyPendingResize();
      const s = useDisplaySettingsStore.getState();
      const tx = useTxStore.getState();
      const keyed = tx.moxOn || tx.tunOn;
      const pop = useSignalEnhanceStore.getState();
      const popOn = pop.popEnabled && !keyed;
      const dbMin = popOn ? 0 : keyed ? s.txDbMin : s.dbMin;
      const dbMax = popOn ? 1 : keyed ? s.txDbMax : s.dbMax;
      const { r, g, b } = hexToRgbFloats(s.rxTraceColor);
      renderer.setTraceColor(r, g, b);
      setRendererPalette(popOn ? 'pop' : s.colormap);
      renderer.setReliefDepth(Math.max(0, Math.min(1, pop.waterfallReliefDepth / 100)));
      renderer.setPopGlow(popOn ? Math.max(0, Math.min(1, pop.popRenderIntensity / 100)) : 0.18);

      const ownFrameHzPerPixel = selectDisplaySlice(useDisplayStore.getState(), receiver).hzPerPixel;
      const viewHzPerPixel =
        rxIndex === KIWI_RECEIVER_INDEX
          ? ownFrameHzPerPixel > 0
            ? viewZoom.displayedHzPerPixelFor(rxIndex, ownFrameHzPerPixel)
            : null
          : viewZoom.isInitialized()
            ? viewZoom.getDisplayedHzPerPixel()
            : ownFrameHzPerPixel > 0
              ? ownFrameHzPerPixel
              : null;
      renderer.draw(dbMin, dbMax, visualCenterHz(), viewHzPerPixel, {
        rxDbMin: s.dbMin,
        rxDbMax: s.dbMax,
        txDbMin: s.txDbMin,
        txDbMax: s.txDbMax,
      });
    };

    let inViewport = true;
    let pageVisible = !document.hidden;
    const isActive = () => inViewport && pageVisible;
    const requestRedraw = () => {
      if (!isActive()) return;
      requestDrawBusFrame(draw);
    };

    const queueResize = (entry?: ResizeObserverEntry) => {
      const next = measureCanvasSize(entry);
      pendingCanvasW = next.w;
      pendingCanvasH = next.h;
      requestRedraw();
    };
    const ro = new ResizeObserver((entries) => queueResize(entries[entries.length - 1]));
    const io = new IntersectionObserver(
      (entries) => {
        for (const e of entries) inViewport = e.isIntersecting;
        if (isActive()) requestRedraw();
      },
      { threshold: 0 },
    );
    const onVisibilityChange = () => {
      pageVisible = !document.hidden;
      if (isActive()) requestRedraw();
    };

    let unsub: (() => void) | null = null;
    let unsubSettings: (() => void) | null = null;
    let unsubViewCenter: (() => void) | null = null;
    let unsubViewZoom: (() => void) | null = null;
    let unsubConn: (() => void) | null = null;
    let unsubTx: (() => void) | null = null;
    let unsubEnhance: (() => void) | null = null;

    const fail = (why: string) => {
      setStatus('unsupported');
      setReason(why);
      onUnavailableRef.current?.();
    };

    void probeWebGpu().then((probe) => {
      if (disposed) return;
      if (!probe.supported || !probe.device) {
        fail(probe.reason);
        return;
      }
      const ctx = canvas.getContext('webgpu');
      if (!ctx) {
        fail('canvas.getContext("webgpu") returned null');
        return;
      }
      try {
        renderer = createPanSurfaceRenderer(probe.device, ctx, probe.format);
      } catch (err) {
        fail(`renderer init failed: ${err instanceof Error ? err.message : String(err)}`);
        return;
      }
      void probe.device.lost.then((info) => {
        if (disposed || info.reason === 'destroyed') return;
        lost = true;
        resetWebGpuProbe();
        fail(`device lost: ${info.message || info.reason}`);
      });

      setStatus('ready');
      queueResize();
      ro.observe(container);
      io.observe(container);
      document.addEventListener('visibilitychange', onVisibilityChange);

      const slice0 = selectDisplaySlice(useDisplayStore.getState(), receiver);
      if (slice0.panValid && slice0.panDb) {
        pushFrame(slice0.panDb, slice0.centerHz, slice0.hzPerPixel);
        lastSeqDrawn = slice0.lastSeq;
      }
      requestRedraw();

      unsub = useDisplayStore.subscribe((state) => {
        if (!renderer || lost) return;
        const slice = selectDisplaySlice(state, receiver);
        if (slice.lastSeq === 0 || slice.lastSeq === lastSeqDrawn) return;
        lastSeqDrawn = slice.lastSeq;
        const decision = planForFrame({
          seq: slice.lastSeq,
          centerHz: slice.centerHz,
          hzPerPixel: slice.hzPerPixel,
          width: slice.width,
          planKey: String(receiver),
        });
        const frameCenter = Number(slice.centerHz);
        const widebandDisplay = isWidebandDisplayGeometry({
          width: slice.width,
          hzPerPixel: slice.hzPerPixel,
          centerHz: frameCenter,
        });
        const enteringWidebandDisplay = widebandDisplay && !wasWidebandDisplay;

        if (rxIndex === 0 && slice.hzPerPixel > 0) {
          if (widebandDisplay) {
            if (enteringWidebandDisplay || decision.kind === 'reset') viewZoom.snapTo(slice.hzPerPixel);
          } else if (decision.kind === 'reset') viewZoom.snapTo(slice.hzPerPixel);
          else viewZoom.setTarget(slice.hzPerPixel);
        }

        if (decision.kind === 'reset' || enteringWidebandDisplay) {
          vc.snapTo(frameCenter, slice.hzPerPixel);
          renderer.clearHistory();
        } else if (!widebandDisplay) {
          vc.reconcileFrame(frameCenter, slice.hzPerPixel);
        }

        if (slice.panValid && slice.panDb) {
          pushFrame(slice.panDb, slice.centerHz, slice.hzPerPixel);
          if (rxIndex === 0) {
            const tx = useTxStore.getState();
            const ds = useDisplaySettingsStore.getState();
            if (shouldTxAutoRange(tx, ds.txAutoRange)) ds.updateTxAutoRange(slice.panDb);
          }
        }
        wasWidebandDisplay = widebandDisplay;
        requestRedraw();
      });

      unsubSettings = useDisplaySettingsStore.subscribe((state, prev) => {
        if (!renderer || lost) return;
        if (state.colormap !== prev.colormap) lastPalette = '';
        if (
          state.colormap !== prev.colormap ||
          state.dbMin !== prev.dbMin ||
          state.dbMax !== prev.dbMax ||
          state.txDbMin !== prev.txDbMin ||
          state.txDbMax !== prev.txDbMax ||
          state.rxTraceColor !== prev.rxTraceColor
        ) {
          requestRedraw();
        }
      });

      unsubViewCenter = vc.subscribe(requestRedraw);
      unsubViewZoom = viewZoom.subscribe(requestRedraw);
      unsubConn = useConnectionStore.subscribe((state, prev) => {
        if (rxIndex === 0) return;
        if (state.receivers !== prev.receivers) requestRedraw();
      });

    const clearDomainAndRedraw = () => {
      if (!renderer || lost) return;
      const prevDomain = valueDomain;
      const nextDomain = domainNow();
      const shouldClear = shouldClearForDomainChange(prevDomain, nextDomain);
      valueDomain = nextDomain;
      if (shouldClear) renderer.clearHistory();
      const shouldReplayLastFrame =
        !!lastRawPan &&
        prevDomain !== 'tx-db' &&
        nextDomain !== 'tx-db' &&
        (shouldClear || prevDomain === nextDomain);
      if (shouldReplayLastFrame && lastRawPan) {
        const slice = selectDisplaySlice(useDisplayStore.getState(), receiver);
        pushFrame(lastRawPan, slice.centerHz, slice.hzPerPixel);
      }
      requestRedraw();
    };
      unsubTx = useTxStore.subscribe((state, prev) => {
        if (
          state.moxOn !== prev.moxOn ||
          state.tunOn !== prev.tunOn ||
          state.twoToneOn !== prev.twoToneOn
        ) {
          const ds = useDisplaySettingsStore.getState();
          if (!shouldTxAutoRange(state, ds.txAutoRange)) ds.restoreSavedTxWindows();
          clearDomainAndRedraw();
        }
      });
      unsubEnhance = useSignalEnhanceStore.subscribe((state, prev) => {
        if (
          state.popEnabled !== prev.popEnabled ||
          state.popFloorDb !== prev.popFloorDb ||
          state.popSpanDb !== prev.popSpanDb ||
          state.popGamma !== prev.popGamma ||
          state.popRenderIntensity !== prev.popRenderIntensity ||
          state.coherenceHoldGate !== prev.coherenceHoldGate ||
          state.coherenceBoostDb !== prev.coherenceBoostDb ||
          state.ridgeBoost !== prev.ridgeBoost ||
          state.ridgeMaxBoostDb !== prev.ridgeMaxBoostDb ||
          state.visualAgcEnabled !== prev.visualAgcEnabled ||
          state.visualAgcStrength !== prev.visualAgcStrength ||
          state.impulseRejectEnabled !== prev.impulseRejectEnabled ||
          state.impulseRejectDb !== prev.impulseRejectDb ||
          state.waterfallReliefDepth !== prev.waterfallReliefDepth
        ) {
          clearDomainAndRedraw();
        }
      });
    });

    return () => {
      disposed = true;
      unsub?.();
      unsubSettings?.();
      unsubViewCenter?.();
      unsubViewZoom?.();
      unsubConn?.();
      unsubTx?.();
      unsubEnhance?.();
      cancelDrawBusFrame(draw);
      ro.disconnect();
      io.disconnect();
      document.removeEventListener('visibilitychange', onVisibilityChange);
      releaseFrameConsumer();
      renderer?.dispose();
    };
  }, [receiver, rxIndex, stitched]);

  usePanTuneGesture(canvasRef, receiver, { touchMode, tuneReceiver });

  return (
    <div
      ref={containerRef}
      className={`spectrum-canvas panadapter-3d${popActive ? ' pop-enhanced' : ''}`}
      style={{
        position: 'relative',
        minHeight: 0,
        width: '100%',
        height: '100%',
        background: popActive ? 'var(--pop-surface-bg)' : 'var(--spec-bg)',
        opacity: 1,
        overflow: 'hidden',
        ['--vfo-filter-color' as string]: receiverFilterColor,
        ...(popActive
          ? ({ ['--pop-intensity' as string]: popIntensityCss } as CSSProperties)
          : undefined),
      } as CSSProperties}
    >
      <canvas ref={canvasRef} style={{ position: 'absolute', inset: 0, width: '100%', height: '100%' }} />
      {rxIndex === 0 && !multiRx && !widebandDisplay && <BandOverlay receiver={receiver} />}
      {!widebandDisplay && (
        <div
          className="pointer-events-none absolute z-[25] rounded-sm px-2 py-0.5 font-mono text-[10px]"
          style={{
            top: 24,
            left: 8,
            background: 'rgba(8, 10, 14, 0.78)',
            color: receiverFilterColor,
            border: '1px solid rgba(255,255,255,0.16)',
          }}
        >
          {rxLabel} · {(vfoHz / 1e6).toFixed(6)}
          {stitched && foreground ? ' · FOCUS' : ''}
          {status === 'ready' ? ' · 3D' : ''}
        </div>
      )}
      {!widebandDisplay && <PassbandOverlay resizable containerRef={containerRef} receiver={receiver} />}
      <FilterCursorOverlay containerRef={containerRef} receiver={receiver} />
      {rxIndex === 0 && !widebandDisplay && (!stitched || foreground) && (
        <>
          <SpotOverlay />
          <ChatRosterOverlay />
          <PeakMarkerOverlay />
          <NotchOverlay interactive resizable containerRef={containerRef} />
          <ImdReadings />
        </>
      )}
      <FreqAxis receiver={receiver} stitched={stitched} />
      {widebandDisplay && <WidebandViewportControls containerRef={containerRef} receiver={receiver} />}
      {rxIndex === 0 && !widebandDisplay && <DbScale />}
      {status === 'unsupported' && (
        <div
          role="status"
          style={{
            position: 'absolute',
            top: 6,
            left: 6,
            right: 6,
            padding: '4px 6px',
            fontSize: 10,
            fontFamily: 'monospace',
            color: 'var(--fg-0)',
            background: 'rgba(0,0,0,0.62)',
            border: '1px solid rgba(255,255,255,0.22)',
            borderRadius: 3,
            pointerEvents: 'none',
            zIndex: 40,
          }}
          title={reason}
        >
          WebGPU 3D pan unavailable - {reason}
        </div>
      )}
    </div>
  );
}
