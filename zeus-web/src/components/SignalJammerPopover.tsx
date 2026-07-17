// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { useMemo, useState } from 'react';
import { Play, X } from 'lucide-react';
import { sendSignalJammerText } from '../api/signal-jammer';
import {
  estimateTextSpectrogramSampleCount,
  estimateTextSpectrogramDurationSec,
  playTextSpectrogram,
  renderTextSpectrogram,
  synthesizeTextSpectrogramSamples,
} from '../audio/text-spectrogram';
import { useAudioDeviceStore } from '../state/audio-device-store';
import { useEasterEggStore } from '../state/easter-egg-store';
import {
  SIGNAL_JAMMER_PRESETS,
  type SignalJammerPreset,
  useSignalJammerStore,
} from '../state/signal-jammer-store';
import {
  SIGNAL_JAMMER_TEXT_MAX_CHARS,
  SIGNAL_JAMMER_TEXT_MAX_PIXELS_PER_SECOND,
  SIGNAL_JAMMER_TEXT_MIN_PIXELS_PER_SECOND,
  SIGNAL_JAMMER_TEXT_TX_MAX_SAMPLES,
  SIGNAL_JAMMER_TEXT_TX_SAMPLE_RATE,
} from '../state/signal-jammer-limits';
import './SignalJammerPopover.css';

const PRESET_LABELS: Record<SignalJammerPreset, string> = {
  hash: 'Barrage',
  heterodyne: 'Rake',
  pulse: 'Burst',
};

export function SignalJammerPopover() {
  const open = useEasterEggStore((s) => s.signalJammerPopoverOpen);
  const close = useEasterEggStore((s) => s.closeSignalJammerPopover);
  const enabled = useSignalJammerStore((s) => s.enabled);
  const active = useSignalJammerStore((s) => s.active);
  const preset = useSignalJammerStore((s) => s.preset);
  const level = useSignalJammerStore((s) => s.level);
  const toneHz = useSignalJammerStore((s) => s.toneHz);
  const driftHz = useSignalJammerStore((s) => s.driftHz);
  const pulseRateHz = useSignalJammerStore((s) => s.pulseRateHz);
  const textSoundText = useSignalJammerStore((s) => s.textSoundText);
  const textPixelsPerSecond = useSignalJammerStore((s) => s.textPixelsPerSecond);
  const runtimeStatus = useSignalJammerStore((s) => s.runtimeStatus);
  const runtimeMessage = useSignalJammerStore((s) => s.runtimeMessage);
  const setEnabled = useSignalJammerStore((s) => s.setEnabled);
  const setActive = useSignalJammerStore((s) => s.setActive);
  const setPreset = useSignalJammerStore((s) => s.setPreset);
  const setLevel = useSignalJammerStore((s) => s.setLevel);
  const setToneHz = useSignalJammerStore((s) => s.setToneHz);
  const setDriftHz = useSignalJammerStore((s) => s.setDriftHz);
  const setPulseRateHz = useSignalJammerStore((s) => s.setPulseRateHz);
  const setTextSoundText = useSignalJammerStore((s) => s.setTextSoundText);
  const setTextPixelsPerSecond = useSignalJammerStore((s) => s.setTextPixelsPerSecond);
  const outputDeviceId = useAudioDeviceStore((s) => s.browserOutputDeviceId);
  const [textPlayStatus, setTextPlayStatus] =
    useState<'idle' | 'playing' | 'too-long' | 'unavailable'>('idle');
  const textSpectrogram = useMemo(
    () => renderTextSpectrogram(textSoundText),
    [textSoundText],
  );
  const textDurationSec = useMemo(
    () =>
      estimateTextSpectrogramDurationSec(textSoundText, {
        pixelsPerSecond: textPixelsPerSecond,
      }),
    [textPixelsPerSecond, textSoundText],
  );
  const textSampleCount = useMemo(
    () =>
      estimateTextSpectrogramSampleCount(
        textSoundText,
        { pixelsPerSecond: textPixelsPerSecond },
        SIGNAL_JAMMER_TEXT_TX_SAMPLE_RATE,
      ),
    [textPixelsPerSecond, textSoundText],
  );
  const textTooLong = textSampleCount > SIGNAL_JAMMER_TEXT_TX_MAX_SAMPLES;

  if (!open) return null;

  const statusText = !enabled
    ? 'Locked'
    : active && runtimeStatus === 'running'
      ? 'Running'
      : runtimeStatus === 'unavailable'
        ? runtimeMessage ?? 'Unavailable'
        : 'Ready';

  return (
    <section
      id="tx-testing-tools-popout"
      className="tx-testing-tools-popout"
      role="dialog"
      aria-label="TX Testing Tools"
    >
      <div className="sj-head">
        <div>
          <div className="sj-title">TX Testing Tools</div>
          <div className="sj-status" data-status={runtimeStatus}>
            {statusText}
          </div>
        </div>
        <button type="button" className="sj-icon-btn" onClick={close} aria-label="Close TX Testing Tools">
          <X size={15} strokeWidth={2.25} aria-hidden />
        </button>
      </div>

      <div className="sj-toggle-row">
        <label className="sj-switch">
          <input
            type="checkbox"
            checked={enabled}
            onChange={(e) => setEnabled(e.currentTarget.checked)}
          />
          <span>{enabled ? 'Enabled' : 'Enable'}</span>
        </label>
        <button
          type="button"
          className={`btn sm ${active ? 'active' : ''}`}
          disabled={!enabled}
          onClick={() => setActive(!active)}
          aria-pressed={active}
        >
          {active ? 'ON' : 'OFF'}
        </button>
      </div>

      <div className="sj-segments" role="group" aria-label="TX test mode">
        {SIGNAL_JAMMER_PRESETS.map((item) => (
          <button
            key={item}
            type="button"
            className={item === preset ? 'is-on' : ''}
            onClick={() => setPreset(item)}
            aria-pressed={item === preset}
          >
            {PRESET_LABELS[item]}
          </button>
        ))}
      </div>

      <SliderRow
        label="Level"
        value={`${Math.round(level)}%`}
        min={0}
        max={100}
        step={1}
        sliderValue={level}
        onChange={setLevel}
      />
      <SliderRow
        label="Tone"
        value={`${toneHz} Hz`}
        min={250}
        max={3200}
        step={10}
        sliderValue={toneHz}
        onChange={setToneHz}
      />
      <SliderRow
        label="Drift"
        value={`${driftHz} Hz`}
        min={0}
        max={500}
        step={5}
        sliderValue={driftHz}
        onChange={setDriftHz}
      />
      <SliderRow
        label="Pulse"
        value={`${pulseRateHz.toFixed(1)} Hz`}
        min={0.5}
        max={12}
        step={0.1}
        sliderValue={pulseRateHz}
        onChange={setPulseRateHz}
      />

      <div className="sj-text-sound">
        <div className="sj-section-title">Text</div>
        <label className="sj-text-row">
          <input
            type="text"
            value={textSoundText}
            maxLength={SIGNAL_JAMMER_TEXT_MAX_CHARS}
            onChange={(e) => {
              setTextSoundText(e.currentTarget.value);
              setTextPlayStatus('idle');
            }}
            aria-label="Text to write as sound"
          />
        </label>
        <div className="sj-text-preview-window" aria-hidden>
          <div
            className="sj-text-preview"
            style={{
              width: `${textSpectrogram.columns * 3}px`,
              height: `${textSpectrogram.rows * 3}px`,
              gridTemplateColumns: `repeat(${textSpectrogram.columns}, 3px)`,
              gridTemplateRows: `repeat(${textSpectrogram.rows}, 3px)`,
            }}
          >
            {Array.from(textSpectrogram.pixels).map((pixel, index) => (
              <span key={`${textSpectrogram.text}-${index}`} className={pixel ? 'is-on' : ''} />
            ))}
          </div>
        </div>
        <div className="sj-text-actions">
          <button
            type="button"
            className="btn sm"
            disabled={textTooLong}
            title={textTooLong ? 'Increase speed or shorten text' : undefined}
            onClick={() => {
              if (textTooLong) {
                setTextPlayStatus('too-long');
                return;
              }
              const options = {
                pixelsPerSecond: textPixelsPerSecond,
                minHz: 300,
                maxHz: 2600,
                level,
              };
              const rendered = synthesizeTextSpectrogramSamples(
                textSoundText,
                options,
                SIGNAL_JAMMER_TEXT_TX_SAMPLE_RATE,
              );
              const ok = playTextSpectrogram(textSoundText, options, outputDeviceId);
              setTextPlayStatus(ok ? 'playing' : 'unavailable');
              void sendSignalJammerText(rendered.samples, rendered.sampleRate, {
                autoTransmit: true,
              })
                .then(() => setTextPlayStatus('playing'))
                .catch((err) => {
                  console.warn('signal-jammer.text.tx failed', err);
                  setTextPlayStatus('unavailable');
                });
            }}
          >
            <Play size={13} strokeWidth={2.4} aria-hidden />
            WRITE
          </button>
          <span className="sj-text-meta" data-status={textTooLong ? 'too-long' : textPlayStatus}>
            {textTooLong || textPlayStatus === 'too-long'
              ? 'Too long'
              : textPlayStatus === 'unavailable'
              ? 'Unavailable'
              : textPlayStatus === 'playing'
                ? 'Playing'
                : `${textDurationSec.toFixed(1)} s`}
          </span>
        </div>
        <SliderRow
          label="Speed"
          value={`${Math.round(textPixelsPerSecond)} px/s`}
          min={SIGNAL_JAMMER_TEXT_MIN_PIXELS_PER_SECOND}
          max={SIGNAL_JAMMER_TEXT_MAX_PIXELS_PER_SECOND}
          step={1}
          sliderValue={textPixelsPerSecond}
          onChange={setTextPixelsPerSecond}
        />
      </div>
    </section>
  );
}

function SliderRow({
  label,
  value,
  min,
  max,
  step,
  sliderValue,
  onChange,
}: {
  label: string;
  value: string;
  min: number;
  max: number;
  step: number;
  sliderValue: number;
  onChange: (value: number) => void;
}) {
  return (
    <label className="sj-slider-row">
      <span>{label}</span>
      <input
        type="range"
        min={min}
        max={max}
        step={step}
        value={sliderValue}
        onChange={(e) => onChange(Number(e.currentTarget.value))}
      />
      <span className="sj-value">{value}</span>
    </label>
  );
}
