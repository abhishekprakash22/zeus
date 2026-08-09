// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// FPGA FIRMWARE (Updates tab) — Phase B face for the Saturn flash writer.
// The shelf is the official one (laurencebarker/Saturn FPGA folder via the
// server); the target is the PRIMARY slot only, golden untouched. Writing
// gateware gets the sternest gate in the app: not two presses but a TYPED
// word — the operator writes FLASH before the button will fire.

import { useCallback, useEffect, useState } from 'react';
import {
  fetchFpgaImages,
  fetchFpgaStatus,
  startFpgaFlash,
  type FpgaFlashStatusDto,
  type FpgaImageDto,
} from '../api/client';

const PHASE_LABEL: Record<string, string> = {
  downloading: 'downloading bitstream',
  erasing: 'erasing primary slot',
  writing: 'writing pages',
  verifying: 'verifying (readback compare)',
};

export function FpgaFlashSection() {
  const [status, setStatus] = useState<FpgaFlashStatusDto | null>(null);
  const [images, setImages] = useState<FpgaImageDto[] | null>(null);
  const [chosen, setChosen] = useState<string>('');
  const [confirm, setConfirm] = useState('');
  const [err, setErr] = useState('');

  const refresh = useCallback(() => {
    void fetchFpgaStatus().then(setStatus).catch(() => undefined);
  }, []);

  useEffect(() => {
    refresh();
  }, [refresh]);

  const busy =
    status !== null && !['idle', 'done', 'error'].includes(status.phase);

  // Poll while a job runs so the progress bar walks.
  useEffect(() => {
    if (!busy) return;
    const id = window.setInterval(refresh, 1500);
    return () => window.clearInterval(id);
  }, [busy, refresh]);

  const loadImages = () => {
    setErr('');
    void fetchFpgaImages()
      .then((list) => setImages(list))
      .catch(() => setErr('could not reach the bitstream shelf (github.com)'));
  };

  const fire = () => {
    setConfirm('');
    setErr('');
    void startFpgaFlash(chosen)
      .then((r) => {
        if (!r.ok) setErr(r.error ?? 'refused');
        refresh();
      })
      .catch((e) => setErr((e as Error)?.message ?? 'request failed'));
  };

  const present = status?.saturnPresent === true;

  return (
    <div className="fpga-flash">
      <div className="fpga-head">FPGA FIRMWARE</div>
      <div className="fpga-note">
        Rewrites the Saturn's PRIMARY gateware slot ({status?.primaryAddrHex ?? '0x980000'}) from
        the official Saturn repository, then verifies every byte by readback. The golden fallback
        image is never touched — a bad primary falls back to golden at power-up.
      </div>

      {!present && (
        <div className="fpga-note fpga-dim">
          No Saturn on this computer's PCIe bus — the FPGA updates from the radio's own screen,
          not from a remote client.
        </div>
      )}

      {present && !busy && (
        <>
          {images === null ? (
            <button type="button" className="cwdec-btn" onClick={loadImages}>
              LOAD AVAILABLE IMAGES
            </button>
          ) : (
            <div className="fpga-list">
              {images.map((img) => (
                <label key={img.url} className="fpga-img">
                  <input
                    type="radio"
                    name="fpga-img"
                    checked={chosen === img.url}
                    onChange={() => {
                      setChosen(img.url);
                      setConfirm('');
                    }}
                  />
                  {img.name}
                  <span className="fpga-size">{(img.size / (1024 * 1024)).toFixed(1)} MB</span>
                </label>
              ))}
            </div>
          )}

          {chosen && (
            <div className="fpga-arm">
              <span>
                Type <b>FLASH</b> to arm:
              </span>
              <input
                type="text"
                value={confirm}
                onChange={(e) => setConfirm(e.currentTarget.value)}
                placeholder="FLASH"
                autoCapitalize="characters"
              />
              <button
                type="button"
                className="cwdec-btn fpga-fire"
                disabled={confirm !== 'FLASH'}
                onClick={fire}
              >
                FLASH PRIMARY
              </button>
            </div>
          )}
        </>
      )}

      {busy && status && (
        <div className="fpga-progress">
          <div className="fpga-phase">
            {PHASE_LABEL[status.phase] ?? status.phase}
            {status.detail ? ` — ${status.detail}` : ''}
          </div>
          <div className="fpga-bar">
            <div className="fpga-bar-fill" style={{ width: `${Math.round(status.progress * 100)}%` }} />
          </div>
          <div className="fpga-note">
            Keep power on. The radio keeps operating; the new image loads at the next power-cycle.
          </div>
        </div>
      )}

      {status?.phase === 'done' && (
        <div className="fpga-done">✓ {status.detail}</div>
      )}
      {status?.phase === 'error' && (
        <div className="fpga-err">✗ {status.error}</div>
      )}
      {err && <div className="fpga-err">· {err}</div>}
    </div>
  );
}
