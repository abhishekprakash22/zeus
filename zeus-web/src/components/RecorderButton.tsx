// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// REC — transport control for the Recorder. Click toggles recording of the
// selected source (default RX); the ▾ opens the manager popover: source
// picker (RX / TX mic / TX on-air), live status, and the recordings list
// with in-browser playback (range requests), download, and delete.

import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  deleteRecording,
  fetchRecorderStatus,
  fetchRecordings,
  recordingUrl,
  startRecorder,
  stopRecorder,
  type RecorderStatusDto,
  type RecordingFileDto,
} from '../api/client';

const SOURCES = [
  { id: 'rx', label: 'RX audio', hint: 'what you are hearing' },
  { id: 'txmic', label: 'TX mic', hint: 'raw microphone' },
  { id: 'txair', label: 'TX on-air', hint: 'processed chain — flows while MON or MOX runs' },
] as const;

function fmtElapsed(sec: number): string {
  const m = Math.floor(sec / 60);
  const s = Math.floor(sec % 60);
  return `${m}:${s.toString().padStart(2, '0')}`;
}

function fmtBytes(b: number): string {
  const mb = b / (1024 * 1024);
  return mb >= 1 ? `${mb.toFixed(1)} MB` : `${Math.round(b / 1024)} kB`;
}

export function RecorderButton() {
  const [status, setStatus] = useState<RecorderStatusDto | null>(null);
  const [open, setOpen] = useState(false);
  const [source, setSource] = useState<(typeof SOURCES)[number]['id']>('rx');
  const [files, setFiles] = useState<RecordingFileDto[]>([]);
  const [playing, setPlaying] = useState<string | null>(null);
  const anchorRef = useRef<HTMLSpanElement | null>(null);

  const refreshStatus = useCallback(() => {
    void fetchRecorderStatus().then(setStatus).catch(() => undefined);
  }, []);
  const refreshFiles = useCallback(() => {
    void fetchRecordings().then(setFiles).catch(() => undefined);
  }, []);

  useEffect(() => {
    refreshStatus();
  }, [refreshStatus]);

  // Poll while recording (elapsed ticks) or while the popover is open.
  useEffect(() => {
    if (!status?.recording && !open) return;
    const id = window.setInterval(refreshStatus, 1000);
    return () => window.clearInterval(id);
  }, [status?.recording, open, refreshStatus]);

  useEffect(() => {
    if (open) refreshFiles();
  }, [open, refreshFiles]);

  const toggleRecord = () => {
    if (status?.recording) {
      void stopRecorder().then((r) => {
        setStatus(r.status);
        refreshFiles();
      });
    } else {
      void startRecorder(source).then((r) => setStatus(r.status));
    }
  };

  const rec = status?.recording === true;
  return (
    <span className="rec-cluster" ref={anchorRef}>
      <button
        type="button"
        className={`btn ghost rec-toggle ${rec ? 'engaged' : ''}`}
        title={
          rec
            ? `Recording ${status?.source ?? ''} — press to stop and save`
            : `Record ${SOURCES.find((s) => s.id === source)?.label ?? 'RX audio'} to a WAV on the radio`
        }
        aria-pressed={rec}
        onClick={toggleRecord}
      >
        {rec ? `REC ${fmtElapsed(status?.elapsedSec ?? 0)}` : 'REC'}
      </button>
      <button
        type="button"
        className="btn ghost rec-more"
        title="Recordings — source, files, playback"
        onClick={() => setOpen((v) => !v)}
      >
        ▾
      </button>
      {open &&
        createPortal(
          <div className="rec-popover" role="dialog" aria-label="Recorder">
            <div className="rec-pop-head">
              <span>RECORDER</span>
              <button type="button" className="cwdec-btn" onClick={() => setOpen(false)}>
                ✕
              </button>
            </div>
            <div className="rec-pop-src">
              {SOURCES.map((s) => (
                <label key={s.id} title={s.hint}>
                  <input
                    type="radio"
                    name="rec-src"
                    checked={source === s.id}
                    disabled={rec}
                    onChange={() => setSource(s.id)}
                  />
                  {s.label}
                </label>
              ))}
            </div>
            {status?.error ? <div className="rec-pop-err">{status.error}</div> : null}
            <div className="rec-pop-files">
              {files.length === 0 ? (
                <div className="rec-pop-empty">No recordings yet.</div>
              ) : (
                files.map((f) => (
                  <div key={f.name} className="rec-pop-file">
                    <div className="rec-pop-file-name" title={f.name}>
                      {f.name}
                    </div>
                    <div className="rec-pop-file-meta">
                      {fmtElapsed(f.durationSec)} · {fmtBytes(f.bytes)}
                    </div>
                    <div className="rec-pop-file-actions">
                      <button
                        type="button"
                        className="cwdec-btn"
                        onClick={() => setPlaying((p) => (p === f.name ? null : f.name))}
                      >
                        {playing === f.name ? 'STOP' : 'PLAY'}
                      </button>
                      <a className="cwdec-btn" href={recordingUrl(f.name)} download>
                        SAVE
                      </a>
                      <button
                        type="button"
                        className="cwdec-btn"
                        onClick={() =>
                          void deleteRecording(f.name).then(() => {
                            if (playing === f.name) setPlaying(null);
                            refreshFiles();
                          })
                        }
                      >
                        DEL
                      </button>
                    </div>
                    {playing === f.name && (
                      // eslint-disable-next-line jsx-a11y/media-has-caption
                      <audio src={recordingUrl(f.name)} autoPlay controls className="rec-pop-audio" />
                    )}
                  </div>
                ))
              )}
            </div>
          </div>,
          document.body,
        )}
    </span>
  );
}
