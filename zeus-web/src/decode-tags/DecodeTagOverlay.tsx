// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Decodes on the spectrum (RX1 panadapter): every call the radio has heard —
// FT8/FT4 decodes, CW skimmer channels, cluster/TCI spots — tagged at its
// frequency and coloured by logbook status (new DXCC, new on band, needed,
// worked; yellow when it is calling you), plus a hint for signals nobody has
// named (CW, SSB, Carrier, FT8, AM, Wide). Tags sit in three lanes; when a
// band is crowded the least useful give way. Click a CW or spot tag to tune.

import { useEffect, useMemo, useRef, useState } from 'react';
import { setVfo } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useCwSkimStore } from '../state/cw-skim-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { useDisplayStore } from '../state/display-store';
import { useFt8Store } from '../state/ft8-store';
import { useOperatorStore } from '../state/operator-store';
import { useSpotStore } from '../state/spot-store';
import { useVfoLockStore } from '../state/vfo-lock-store';
import { SignalClassifier, SIGNAL_KIND_LABEL, type ClassifiedSignal } from '../dsp/signal-classifier';
import { requestCallStatus, statusFor, useCallStatusStore } from './call-status-store';
import {
  cwTags,
  ft8Tags,
  isCallsign,
  layoutLanes,
  mergeTags,
  spotTags,
  tagPriority,
  type DecodeTag,
} from './decode-tags';
import { useDecodeTagPrefs } from './decode-tag-prefs';
import { observeSignals } from './observe';

const TAG_TOP_PX = 84;
const LANE_H_PX = 16;
const LANES = 3;
const CLASSIFY_MS = 500;

type Item =
  | { kind: 'tag'; tag: DecodeTag; statusClass: string; title: string }
  | { kind: 'sig'; sig: ClassifiedSignal };

export function DecodeTagOverlay() {
  const prefs = useDecodeTagPrefs();
  const centerHz = Number(useDisplayStore((s) => s.centerHz));
  const hzPerPixel = useDisplayStore((s) => s.hzPerPixel);
  const bins = useDisplayStore((s) => s.panDb?.length ?? 0);
  const fontPx = useDisplaySettingsStore((s) => s.spotLabelFontPx);
  const rows = useFt8Store((s) => s.rows);
  const slots = useFt8Store((s) => s.slots);
  const skimOn = useCwSkimStore((s) => s.enabled);
  const channels = useCwSkimStore((s) => s.channels);
  const spots = useSpotStore((s) => s.spots);
  const mode = useConnectionStore((s) => s.mode);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const cwPitchHz = useConnectionStore((s) => s.cwPitchHz);
  const myCall = useOperatorStore((s) => s.resolvedCall);
  const byKey = useCallStatusStore((s) => s.byKey);
  const rootRef = useRef<HTMLDivElement | null>(null);
  const [widthPx, setWidthPx] = useState(0);
  const [now, setNow] = useState(() => Date.now());
  const [signals, setSignals] = useState<ClassifiedSignal[]>([]);
  const classifierRef = useRef<SignalClassifier | null>(null);

  useEffect(() => {
    void useDecodeTagPrefs.getState().hydrate();
  }, []);

  // Width of the pan in CSS px, for the lane layout.
  useEffect(() => {
    const el = rootRef.current;
    if (!el) return;
    const measure = () => setWidthPx(el.getBoundingClientRect().width);
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // Tags age out on their own clock.
  useEffect(() => {
    if (!prefs.enabled) return;
    const id = window.setInterval(() => setNow(Date.now()), 1_000);
    return () => window.clearInterval(id);
  }, [prefs.enabled]);

  // Classifier: sample the live RX1 frame twice a second.
  const classify = prefs.enabled && prefs.classifier;
  useEffect(() => {
    if (!classify) {
      classifierRef.current = null;
      setSignals([]);
      return;
    }
    classifierRef.current ??= new SignalClassifier();
    const id = window.setInterval(() => {
      const d = useDisplayStore.getState();
      if (!d.panDb || d.hzPerPixel <= 0) return;
      const observed = observeSignals(d.panDb, Number(d.centerHz), d.hzPerPixel);
      setSignals(classifierRef.current?.update(observed, Date.now(), d.hzPerPixel) ?? []);
    }, CLASSIFY_MS);
    return () => window.clearInterval(id);
  }, [classify]);

  const tags = useMemo(() => {
    if (!prefs.enabled) return [];
    return mergeTags(
      prefs.ft8 ? ft8Tags(rows, slots, now, myCall, 0) : [],
      prefs.cw && skimOn ? cwTags(channels, now, mode, vfoHz, cwPitchHz) : [],
      prefs.spots ? spotTags(spots, now) : [],
    );
  }, [prefs.enabled, prefs.ft8, prefs.cw, prefs.spots, rows, slots, now, myCall, skimOn, channels, mode, vfoHz, cwPitchHz, spots]);

  useEffect(() => {
    const ask = tags.filter((t) => isCallsign(t.call)).map((t) => ({ call: t.call, hz: t.hz }));
    if (ask.length > 0) requestCallStatus(ask);
  }, [tags]);

  if (!prefs.enabled || bins === 0 || hzPerPixel <= 0) return <div ref={rootRef} className="dtag-root" aria-hidden />;

  const spanHz = bins * hzPerPixel;
  const startHz = centerHz - spanHz / 2;
  const xOf = (hz: number) => ((hz - startHz) / spanHz) * widthPx;
  const charPx = fontPx * 0.62;

  const candidates: { item: Item; x: number; width: number; priority: number }[] = [];
  for (const t of tags) {
    if (t.hz < startHz || t.hz > startHz + spanHz) continue;
    const info = statusFor(byKey, t.call, t.hz);
    const status = info?.status ?? null;
    const statusClass = t.me ? 'dtag--me' : status ? `dtag--${status.toLowerCase()}` : 'dtag--unknown';
    const statusText = t.me
      ? 'calling you'
      : status === 'NewDxcc'
        ? 'new DXCC'
        : status === 'NewBand'
          ? 'new on this band'
          : status === 'Needed'
            ? info?.workedAnyBand
              ? 'needed on this band (worked elsewhere)'
              : 'needed'
            : status === 'Worked'
              ? 'worked on this band'
              : '';
    const title = [
      `${t.call}${info?.entity ? ` — ${info.entity}` : ''}`,
      `${(t.hz / 1e6).toFixed(4)} MHz${statusText ? ` · ${statusText}` : ''}`,
      t.detail,
    ].join('\n');
    const label = (t.cq ? 'CQ ' : '') + t.call;
    candidates.push({
      item: { kind: 'tag', tag: t, statusClass, title },
      x: xOf(t.hz),
      width: label.length * charPx + 10,
      priority: tagPriority(t, status),
    });
  }
  for (const s of signals) {
    if (s.hz < startHz || s.hz > startHz + spanHz) continue;
    // A named signal needs no guess.
    const reach = s.widthHz / 2 + 150;
    if (tags.some((t) => Math.abs(t.hz - s.hz) <= reach)) continue;
    candidates.push({
      item: { kind: 'sig', sig: s },
      x: xOf(s.hz),
      width: SIGNAL_KIND_LABEL[s.kind].length * charPx + 8,
      priority: 9,
    });
  }
  const placed = widthPx > 0 ? layoutLanes(candidates, LANES) : [];

  const tune = (hz: number) => {
    if (useVfoLockStore.getState().locked) return;
    useConnectionStore.setState({ vfoHz: hz });
    setVfo(hz)
      .then((st) => useConnectionStore.getState().applyState(st))
      .catch(() => {});
  };

  return (
    <div ref={rootRef} className="dtag-root" aria-label="Decodes on spectrum">
      {placed.map((p) => {
        const top = TAG_TOP_PX + p.lane * LANE_H_PX;
        if (p.item.kind === 'sig') {
          const s = p.item.sig;
          return (
            <span
              key={`sig:${Math.round(s.hz / 50)}`}
              className="dtag dtag--sig"
              style={{ left: p.x, top, fontSize: fontPx - 1 }}
              title={`Looks like ${SIGNAL_KIND_LABEL[s.kind]} · ${(s.hz / 1e6).toFixed(4)} MHz · ~${Math.round(s.widthHz)} Hz wide`}
            >
              {SIGNAL_KIND_LABEL[s.kind]}
            </span>
          );
        }
        const { tag, statusClass, title } = p.item;
        const tunable = !tag.sources.includes('ft8');
        return (
          <button
            key={tag.key}
            type="button"
            className={`dtag ${statusClass}${tunable ? '' : ' dtag--static'}`}
            style={{ left: p.x, top, fontSize: fontPx }}
            title={tunable ? `${title}\nClick to tune` : title}
            onClick={tunable ? () => tune(tag.hz) : undefined}
            tabIndex={tunable ? 0 : -1}
          >
            {tag.cq && <span className="dtag-cq">CQ</span>}
            {tag.call}
            <span className="dtag-src" aria-hidden>
              {tag.sources.map((s) => (s === 'ft8' ? '▪' : s === 'cw' ? '∿' : '⚑')).join('')}
            </span>
          </button>
        );
      })}
    </div>
  );
}
