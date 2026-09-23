// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR),
//                         Ramón Martínez (EA5IUE), and contributors.
//
// HL2 GATEWARE (Updates tab) — the face for HermesLite2FlashService. The
// Saturn section beside it writes over PCIe from the radio's own computer;
// this one writes over Ethernet from anywhere on the LAN.
//
// Same gate as the Saturn: not two presses but a TYPED word. Writing
// gateware deserves an action nobody performs by accident.
//
// Deliberately CHECK FIRST, then arm. The server refuses a pre-7.0 board, a
// streaming radio, or an image for another board revision — but it refuses
// at the moment of writing, and an operator should be able to see all of
// that before they are anywhere near the button. So the check runs on its
// own and the arm row only appears once it has come back clean.
//
// Reuses the .fpga-* classes the Saturn section already defines: this is the
// same kind of panel doing the same kind of thing, and it should not invent
// a second look for it.

import { useCallback, useEffect, useState } from 'react';
import {
  compareHl2Image,
  fetchHl2FlashStatus,
  fetchHl2Images,
  startHl2Flash,
  type Hl2CompareDto,
  type Hl2FlashStatusDto,
  type Hl2ImageDto,
} from '../api/client';

const PHASE_LABEL: Record<string, string> = {
  downloading: 'downloading gateware',
  erasing: 'erasing the application slot',
  writing: 'writing blocks',
};

export function Hl2FlashSection() {
  const [status, setStatus] = useState<Hl2FlashStatusDto | null>(null);
  const [images, setImages] = useState<Hl2ImageDto[] | null>(null);
  const [release, setRelease] = useState('');
  const [chosen, setChosen] = useState('');
  const [confirm, setConfirm] = useState('');
  const [err, setErr] = useState('');
  const [cmp, setCmp] = useState<Hl2CompareDto | null>(null);
  const [checking, setChecking] = useState(false);

  const refresh = useCallback(() => {
    void fetchHl2FlashStatus().then(setStatus).catch(() => undefined);
  }, []);

  useEffect(() => {
    refresh();
  }, [refresh]);

  const busy = status !== null && !['idle', 'done', 'error'].includes(status.phase);

  useEffect(() => {
    if (!busy) return;
    const id = window.setInterval(refresh, 1500);
    return () => window.clearInterval(id);
  }, [busy, refresh]);

  const loadImages = () => {
    setErr('');
    void fetchHl2Images()
      .then((shelf) => {
        setImages(shelf.images);
        setRelease(shelf.release);
      })
      .catch(() => setErr('could not reach the gateware shelf (github.com)'));
  };

  const check = (url: string) => {
    setChecking(true);
    setCmp(null);
    void compareHl2Image(url)
      .then(setCmp)
      .catch(() => setCmp({ ok: false, error: 'check failed' }))
      .finally(() => setChecking(false));
  };

  const fire = () => {
    setConfirm('');
    setErr('');
    void startHl2Flash(chosen)
      .then((r) => {
        if (!r.ok) setErr(r.error ?? 'refused');
        refresh();
      })
      .catch((e) => setErr((e as Error)?.message ?? 'request failed'));
  };

  // The arm row appears only behind a clean check, so the refusals are read
  // before the button exists rather than after it is pressed.
  const armed = cmp?.ok === true;

  return (
    <div className="fpga-flash">
      <div className="fpga-head">HL2 GATEWARE</div>
      <div className="fpga-note">
        Updates a Hermes-Lite 2 over Ethernet, writing the application image in slot2. The factory
        image in slot1 is never written — if an update fails, the HL2 falls back to it at power-up
        and you can simply try again. The radio must be disconnected: it cannot be streaming while
        its gateware is rewritten.
      </div>

      {!busy && (
        <>
          {images === null ? (
            <button type="button" className="cwdec-btn" onClick={loadImages}>
              LOAD AVAILABLE GATEWARE
            </button>
          ) : (
            <>
              <div className="fpga-note fpga-dim">stable release {release}</div>
              <div className="fpga-list">
                {images.map((img) => (
                  <label key={img.url} className="fpga-img">
                    <input
                      type="radio"
                      name="hl2-img"
                      checked={chosen === img.url}
                      onChange={() => {
                        setChosen(img.url);
                        setConfirm('');
                        setCmp(null);
                      }}
                    />
                    {img.name}
                  </label>
                ))}
              </div>
            </>
          )}

          {chosen && (
            <div className="fpga-cmp-row">
              <button
                type="button"
                className="cwdec-btn"
                disabled={checking}
                title="Find the HL2, read what it runs, and test this image against it — writes nothing"
                onClick={() => check(chosen)}
              >
                {checking ? 'CHECKING…' : 'CHECK THIS RADIO'}
              </button>
              {cmp &&
                (cmp.ok ? (
                  <span className="fpga-done">
                    ✓ board {cmp.boardId} at {cmp.ip} · running {cmp.runningGateware}
                  </span>
                ) : (
                  <span className="fpga-err">· {cmp.error}</span>
                ))}
            </div>
          )}

          {armed && (
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
                WRITE SLOT2
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
            <div
              className="fpga-bar-fill"
              style={{ width: `${Math.round(status.progress * 100)}%` }}
            />
          </div>
          <div className="fpga-note">
            Keep the HL2 powered and on the network. It restarts on its own when the write finishes.
          </div>
        </div>
      )}

      {status?.phase === 'done' && <div className="fpga-done">✓ {status.detail}</div>}
      {status?.phase === 'error' && (
        <div className="fpga-err">
          ✗ {status.error}
          {status.detail ? ` — ${status.detail}` : ''}
        </div>
      )}
      {err && <div className="fpga-err">· {err}</div>}
    </div>
  );
}
