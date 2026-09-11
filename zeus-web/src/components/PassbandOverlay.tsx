// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useEffect, useRef } from 'react';
import { selectDisplaySlice, useDisplayStore } from '../state/display-store';
import { useConnectionStore } from '../state/connection-store';
import { cancelDrawBusFrame, requestDrawBusFrame } from '../realtime/draw-bus';
import {
  displayFilterEdgesHz,
  getReceiverFilterHighHz,
  getReceiverFilterLowHz,
  getReceiverMode,
  getReceiverVfoHz,
  type ReceiverKey,
} from '../state/receiver-state';
import * as viewCenter from '../state/view-center';
import { settledDialOffsetHz } from '../util/settled-dial-offset';
import * as viewZoom from '../state/view-zoom';
import { resolveSpectrumViewport } from '../util/wideband-view';

// Translucent rectangle drawn inside the panadapter container to show the
// active receive filter passband, mapped from [filterLowHz, filterHighHz]
// relative to the VFO centre. Asymmetric by design: USB lives to the right
// of carrier, LSB to the left, CW narrow around zero, AM symmetric.
// Positioned by percentage of the total span so it tracks resize and tune
// without measuring DOM width.
//
// DISPLAY-ONLY (2026-09-11, maintainer decision): the overlay used to grow
// grab-to-resize edge handles under a `resizable` prop — two full-height
// pointer-events strips at the passband walls. On the G2's touchscreen those
// strips sat exactly where operators tune, so a drag-tune that started over a
// wall silently resized the WDSP filter instead of tuning (field report).
// The handles, the drag machinery and the prop are gone: this overlay never
// captures a pointer and never writes the filter. Bandwidth is set through
// the deliberate controls (preset chips, the ribbon's CUSTOM inputs). Do not
// reintroduce pointer handlers here.
type PassbandOverlayProps = {
  receiver?: ReceiverKey;
};

export function PassbandOverlay({
  receiver = 'A',
}: PassbandOverlayProps = {}) {
  const centerHz = useDisplayStore((s) => selectDisplaySlice(s, receiver).centerHz);
  const hzPerPixel = useDisplayStore((s) => selectDisplaySlice(s, receiver).hzPerPixel);
  // Header width — survives frames whose pan payload is invalid.
  const width = useDisplayStore((s) => selectDisplaySlice(s, receiver).width);
  const filterLowHz = useConnectionStore((s) => getReceiverFilterLowHz(s, receiver));
  const filterHighHz = useConnectionStore((s) => getReceiverFilterHighHz(s, receiver));
  const selectedVfoHz = useConnectionStore((s) => getReceiverVfoHz(s, receiver));
  const mode = useConnectionStore((s) => getReceiverMode(s, receiver));
  const cwPitchHz = useConnectionStore((s) => s.cwPitchHz);

  const rectRef = useRef<HTMLDivElement | null>(null);
  const vc = viewCenter.viewCenterFor(receiver);

  // Smooth motion (issue #597): the rect is positioned against the animated
  // view-center by a draw-bus callback — same clock as the trace, waterfall,
  // dial marker, and tick strip, zero React commits at display rate. The
  // passband rides the radio's center (filter edges are center-relative),
  // so during a glide it eases with the spectrum instead of teleporting at
  // 30 Hz frame arrival.
  useEffect(() => {
    const update = () => {
      const rect = rectRef.current;
      if (!rect) return;
      const s = selectDisplaySlice(useDisplayStore.getState(), receiver);
      if (!s.width || s.hzPerPixel <= 0) return;
      const viewport = resolveSpectrumViewport({
        width: s.width,
        sourceCenterHz: Number(s.centerHz),
        sourceHzPerPixel: s.hzPerPixel,
        viewCenterHz: vc.isInitialized() ? vc.getViewCenterHz() : undefined,
        viewHzPerPixel: viewZoom.displayedViewHzPerPixelFor(receiver),
      });
      if (!viewport) return;
      const spanHz = viewport.spanHz;
      const conn = useConnectionStore.getState();
      const view = viewport.centerHz;
      // The passband hangs off the VIEW center — which is, by definition,
      // always rendered at the screen center (the orange zero line). So
      // during a tuning glide the filter stays PINNED to the line while the
      // spectrum slides underneath it; anchoring to the commanded target
      // instead made it lead off the line and ease back (operator feedback,
      // 2026-06-12).
      // Hang the passband off the dial, expressed as the dial's settled offset
      // from the display center — the same (vfo − targetCenter) the FreqAxis
      // marker uses. Outside CTUN the dial sits on the view center so this is
      // ~0 and the filter stays pinned to the zero line during a glide; under
      // CTUN the dial roams off-centre and the passband tracks it.
      const vfoHz = getReceiverVfoHz(conn, receiver);
      const rxMode = getReceiverMode(conn, receiver);
      // FreeDV stores its passband USB-positive regardless of band; re-sign it
      // to the convention sideband (LSB < 10 MHz) so the rect lands where the
      // demod actually is. No-op for every other mode.
      const { lowHz: filterLowHz, highHz: filterHighHz } = displayFilterEdgesHz(
        rxMode,
        vfoHz,
        getReceiverFilterLowHz(conn, receiver),
        getReceiverFilterHighHz(conn, receiver),
      );
      // filterLow/HighHz are audio offsets from the hardware LO, not the VFO
      // dial. In CW modes the LO is shifted by ±cwPitchHz from VFO, so
      // passCenter must land on the LO (not VFO) to place the rect correctly.
      const cwOffset = rxMode === 'CWU' ? -conn.cwPitchHz : rxMode === 'CWL' ? conn.cwPitchHz : 0;
      const dialOffsetHz = settledDialOffsetHz(conn, receiver, vc);
      const passCenter = view + dialOffsetHz + cwOffset;
      const startHz = view - spanHz / 2;
      const leftPct = ((passCenter + filterLowHz - startHz) / spanHz) * 100;
      const rightPct = ((passCenter + filterHighHz - startHz) / spanHz) * 100;
      const widthPct = rightPct - leftPct;
      const visible = widthPct > 0 && leftPct <= 100 && rightPct >= 0;
      rect.style.display = visible ? '' : 'none';
      if (visible) {
        rect.style.left = `${leftPct}%`;
        rect.style.width = `${widthPct}%`;
      }
    };
    const schedule = () => requestDrawBusFrame(update);
    const unsubVc = vc.subscribe(schedule);
    const unsubVz = viewZoom.subscribe(schedule);
    const unsubConn = useConnectionStore.subscribe((s, prev) => {
      if (
        s.filterLowHz !== prev.filterLowHz ||
        s.filterHighHz !== prev.filterHighHz ||
        s.vfoHz !== prev.vfoHz ||
        s.mode !== prev.mode ||
        s.cwPitchHz !== prev.cwPitchHz ||
        // RX1 is the flat primary; every secondary (RX2 = index 1, RX3+) lives in
        // the receivers[] array — its reference changes on any filter/vfo/mode edit.
        s.receivers !== prev.receivers
      ) {
        schedule();
      }
    });
    const unsubFrame = useDisplayStore.subscribe((s, prev) => {
      if (selectDisplaySlice(s, receiver).lastSeq !== selectDisplaySlice(prev, receiver).lastSeq) schedule();
    });
    schedule();
    return () => {
      unsubVc();
      unsubVz();
      unsubConn();
      unsubFrame();
      cancelDrawBusFrame(update);
    };
  }, [receiver, vc]);

  if (!width || hzPerPixel <= 0) return null;

  const frameCenter = Number(centerHz);
  const initialViewport = resolveSpectrumViewport({
    width,
    sourceCenterHz: frameCenter,
    sourceHzPerPixel: hzPerPixel,
    viewCenterHz: vc.isInitialized() ? vc.getViewCenterHz() : undefined,
    viewHzPerPixel: viewZoom.displayedViewHzPerPixelFor(receiver),
  });
  if (!initialViewport) return null;
  const spanHz = initialViewport.spanHz;
  const initialView = initialViewport.centerHz;
  const initialTarget = vc.isInitialized() ? vc.getTargetCenterHz() : frameCenter;
  const startHz = initialView - spanHz / 2;

  // Initial (pre-draw-bus) geometry; the callback refines it next frame.
  const cwOffset = mode === 'CWU' ? -cwPitchHz : mode === 'CWL' ? cwPitchHz : 0;
  // Re-sign FreeDV's USB-positive stored passband to the convention sideband
  // (LSB < 10 MHz) for display — same as the draw-bus path. No-op otherwise.
  const { lowHz: dispLowHz, highHz: dispHighHz } = displayFilterEdgesHz(
    mode,
    selectedVfoHz,
    filterLowHz,
    filterHighHz,
  );
  const initialPassCenter = initialView + (selectedVfoHz - initialTarget) + cwOffset;
  const passLowHz = initialPassCenter + dispLowHz;
  const passHighHz = initialPassCenter + dispHighHz;
  const leftPct = ((passLowHz - startHz) / spanHz) * 100;
  const rightPct = ((passHighHz - startHz) / spanHz) * 100;
  const widthPct = rightPct - leftPct;

  if (widthPct <= 0) return null;

  return (
    <div
      ref={rectRef}
      aria-hidden
      className="passband-overlay pointer-events-none absolute inset-y-0 z-[5]"
      style={{
        left: `${leftPct}%`,
        width: `${widthPct}%`,
      }}
    />
  );
}
