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
// Panadapter overlay toggles in the Display settings tab. Currently the single
// chat-roster overlay switch (also reachable from the Chat panel header); a
// natural home for future panadapter-overlay preferences.

import type { CSSProperties } from 'react';
import {
  SPOT_LABEL_FONT_PX_MAX,
  SPOT_LABEL_FONT_PX_MIN,
  useDisplaySettingsStore,
} from '../state/display-settings-store';
import { DECODE_TAGS_AVAILABLE, useDecodeTagPrefs, type DecodeTagPrefs } from '../decode-tags/decode-tag-prefs';
import { useHoverListenStore } from '../hover-listen/hover-listen';
import { TIME_SHIFT_LENGTHS, useTimeShiftStore } from '../time-shift/time-shift';
import { useEffect } from 'react';
import { CHAT_AVAILABLE } from '../withheld-features';

export function PanadapterOverlaySettingsPanel() {
  const showChatRoster = useDisplaySettingsStore((s) => s.showChatRosterOverlay);
  const setShowChatRoster = useDisplaySettingsStore((s) => s.setShowChatRosterOverlay);
  const spotLabelFontPx = useDisplaySettingsStore((s) => s.spotLabelFontPx);
  const setSpotLabelFontPx = useDisplaySettingsStore((s) => s.setSpotLabelFontPx);
  const tags = useDecodeTagPrefs();
  const hoverListen = useHoverListenStore((s) => s.enabled);
  const setHoverListen = useHoverListenStore((s) => s.setEnabled);
  useEffect(() => {
    if (DECODE_TAGS_AVAILABLE) void useDecodeTagPrefs.getState().hydrate();
    void useHoverListenStore.getState().hydrate();
    void useTimeShiftStore.getState().hydrate();
  }, []);
  const rewind = useTimeShiftStore((s) => s.status);
  const configureRewind = useTimeShiftStore((s) => s.configure);
  const tagSwitch = (key: keyof DecodeTagPrefs, label: string, hint: string, disabled = false) => (
    <div style={autoRow}>
      <label style={{ ...switchLabel, opacity: disabled ? 0.5 : 1 }}>
        <input
          type="checkbox"
          checked={tags[key]}
          disabled={disabled}
          onChange={(event) => tags.set({ [key]: event.currentTarget.checked })}
          style={{ accentColor: 'var(--accent)' }}
        />
        {label}
      </label>
      <span style={autoHint}>{hint}</span>
    </div>
  );

  return (
    <section>
      <div style={sectionHead}>
        <h3 style={sectionH3}>Panadapter Overlays</h3>
        <p style={sectionP}>
          What gets painted over the spectrum on top of the trace and waterfall.
        </p>
      </div>

      <div style={card}>
        <div style={autoRow}>
          <label style={switchLabel}>
            <input
              type="checkbox"
              checked={hoverListen}
              onChange={(event) => setHoverListen(event.currentTarget.checked)}
              style={{ accentColor: 'var(--accent)' }}
            />
            Hover to Listen
          </label>
          <span style={autoHint}>
            Rest the pointer on RX1’s panadapter or waterfall — or hold the touch tuning loupe — and
            you hear that frequency in RX1’s mode and filter, with the normal audio turned down under
            it, without moving the dial. Click (or release the loupe) to tune there. Works across the
            spectrum RX1 is receiving; silent while transmitting. Also the LISTEN button beside SNAP.
            Kept on the radio.
          </span>
        </div>

        <div style={autoRow}>
          <label style={switchLabel}>
            <input
              type="checkbox"
              checked={rewind.enabled}
              onChange={(event) => void configureRewind(event.currentTarget.checked)}
              style={{ accentColor: 'var(--accent)' }}
            />
            Rewind
          </label>
          <span style={autoHint}>
            The radio keeps the last stretch of everything RX1 is receiving. A bar on RX1’s panadapter
            lets you go back and hear it again — and tune around inside it, so you can hear what was
            said on a frequency you were not on. Replays replace RX1’s audio and say so; LIVE, or
            keying up, returns to the present. Also the REWIND button beside LISTEN. Kept on the radio.{' '}
            <label style={{ whiteSpace: 'nowrap' }}>
              Keep{' '}
              <select
                value={rewind.requestedSeconds}
                disabled={!rewind.enabled}
                onChange={(event) => void configureRewind(true, Number(event.currentTarget.value))}
              >
                {TIME_SHIFT_LENGTHS.map((s) => (
                  <option key={s} value={s}>
                    {s >= 60 ? `${s / 60} min` : `${s} s`}
                  </option>
                ))}
              </select>
            </label>
            {rewind.enabled && rewind.rateHz > 0 && (
              <>
                {' '}
                — holding up to {rewind.capacitySeconds.toFixed(0)} s at {(rewind.rateHz / 1000).toFixed(0)} kHz (
                {(rewind.memoryBytes / (1024 * 1024)).toFixed(0)} MB
                {rewind.capacitySeconds + 0.5 < rewind.requestedSeconds ? '; limited by the 128 MB ceiling' : ''})
              </>
            )}
          </span>
        </div>

        {DECODE_TAGS_AVAILABLE && (
          <>
        {tagSwitch(
          'enabled',
          'Decodes on Spectrum',
          'Tags every call the radio hears at its frequency on RX1’s panadapter, coloured by your log: magenta new DXCC, orange new on this band, green needed, grey worked, yellow calling you. Also the TAGS button beside SNAP. Kept on the radio.',
        )}
        {tagSwitch('ft8', '— FT8 / FT4 decodes', 'Senders from the last two slots, at dial + audio offset.', !tags.enabled)}
        {tagSwitch('cw', '— CW skimmer calls', 'Calls the skimmer copies (after DE or CQ, or sent twice). Needs SKIM on in the CW decoder.', !tags.enabled)}
        {tagSwitch('spots', '— Cluster and TCI spots', 'Drawn as status-coloured tags instead of the plain spot labels.', !tags.enabled)}
        {tagSwitch(
          'classifier',
          '— Signal type hints',
          'A best guess for signals nobody has named — CW, SSB, Carrier, FT8, AM, Wide (radar / broadband) — from width, steadiness and keying. Hints, not decodes.',
          !tags.enabled,
        )}
          </>
        )}

        {CHAT_AVAILABLE && (
        <div style={autoRow}>
          <label style={switchLabel}>
            <input
              type="checkbox"
              checked={showChatRoster}
              onChange={(event) => setShowChatRoster(event.currentTarget.checked)}
              style={{ accentColor: 'var(--accent)' }}
            />
            Show Chat Operators
          </label>
          <span style={autoHint}>
            Paints connected ZeusChat operators — callsign + transmit dot — onto
            the panadapter at the frequency they're tuned to. Only operators who
            share their frequency (friends) appear. Also toggleable from the Chat
            panel header.
          </span>
        </div>
        )}

        <div style={autoRow}>
          <label style={switchLabel}>
            Spot Label Size
            <input
              type="range"
              min={SPOT_LABEL_FONT_PX_MIN}
              max={SPOT_LABEL_FONT_PX_MAX}
              step={1}
              value={spotLabelFontPx}
              onChange={(event) => setSpotLabelFontPx(Number(event.currentTarget.value))}
              style={{ accentColor: 'var(--accent)' }}
            />
            <span style={{ fontFamily: 'var(--font-mono)', minWidth: 32, textAlign: 'right' }}>
              {spotLabelFontPx}px
            </span>
          </label>
          <span style={autoHint}>
            Font size for TCI and DX-cluster callsign labels painted at the top of
            the panadapter. Range {SPOT_LABEL_FONT_PX_MIN}–{SPOT_LABEL_FONT_PX_MAX}px.
            Larger sizes are easier to read at operating distance; smaller sizes
            fit more spots side-by-side on a busy band.
          </span>
        </div>
      </div>
    </section>
  );
}

const sectionHead: CSSProperties = {
  display: 'flex',
  alignItems: 'baseline',
  flexWrap: 'wrap',
  gap: 10,
  marginBottom: 10,
};

const sectionH3: CSSProperties = {
  margin: 0,
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.18em',
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};

const sectionP: CSSProperties = {
  margin: 0,
  flex: '1 1 260px',
  fontSize: 12,
  lineHeight: 1.5,
  color: 'var(--fg-2)',
};

const card: CSSProperties = {
  display: 'grid',
  gap: 8,
  padding: 10,
  border: '1px solid var(--line)',
  borderRadius: 'var(--r-md)',
  background: 'linear-gradient(180deg, var(--bg-1), var(--bg-0))',
};

const autoRow: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 10,
  flexWrap: 'wrap',
};

const switchLabel: CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 8,
  fontSize: 11,
  fontWeight: 800,
  letterSpacing: '0.08em',
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};

const autoHint: CSSProperties = {
  flex: '1 1 260px',
  fontSize: 11,
  lineHeight: 1.35,
  color: 'var(--fg-3)',
};
