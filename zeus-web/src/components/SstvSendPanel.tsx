// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// SSTV send panel — the SEND half of the SSTV window. The operator picks a
// picture (a file, or REPLY on a received one), a mode, and two text lines;
// the preview canvas IS the picture that goes out, at the mode's exact
// resolution, and SEND ships its pixels to SstvTransmitter. Nothing keys
// without that click; HALT (or MOX off anywhere) ends the picture.

import { useEffect, useMemo, useRef, useState } from 'react';
import { useSstvStore } from '../state/sstv-store';
import { useOperatorStore } from '../state/operator-store';
import { useTxStore } from '../state/tx-store';
import { composeSstvPicture, rgbaToRgbBase64, zeusHeaderVersion } from '../dsp/sstv-compose';

function mmss(ms: number): string {
  const s = Math.round(ms / 1000);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

export function SstvSendPanel() {
  const modeInfos = useSstvStore((s) => s.modeInfos);
  const tx = useSstvStore((s) => s.tx);
  const txError = useSstvStore((s) => s.txError);
  const txSource = useSstvStore((s) => s.txSource);
  const txMode = useSstvStore((s) => s.txMode);
  const txTop = useSstvStore((s) => s.txTop);
  const txBottom = useSstvStore((s) => s.txBottom);
  const txFsk = useSstvStore((s) => s.txFsk);
  const txHeader = useSstvStore((s) => s.txHeader);
  const setTxSource = useSstvStore((s) => s.setTxSource);
  const setTxOptions = useSstvStore((s) => s.setTxOptions);
  const send = useSstvStore((s) => s.send);
  const halt = useSstvStore((s) => s.halt);
  const myCall = useOperatorStore((s) => s.resolvedCall);
  const moxOn = useTxStore((s) => s.moxOn);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const fileRef = useRef<HTMLInputElement | null>(null);

  const info = useMemo(
    () => modeInfos.find((m) => m.name === txMode) ?? modeInfos[0],
    [modeInfos, txMode],
  );
  // With the header strip on, it already names the sender; don't repeat the
  // call as the big top line unless the operator typed one.
  const topText = txTop || (txHeader ? '' : myCall);
  const fskId = txFsk && myCall ? myCall : null;
  const [zeusVersion, setZeusVersion] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    fetch('/api/version')
      .then((r) => r.json() as Promise<{ version?: string }>)
      .then((d) => alive && setZeusVersion(d.version ?? null))
      .catch(() => undefined);
    return () => {
      alive = false;
    };
  }, []);
  const headerRight = txHeader ? zeusHeaderVersion(zeusVersion) : null;
  const headerLeft = myCall || '';
  const sending = tx?.transmitting === true;

  useEffect(() => {
    const c = canvasRef.current;
    const ctx = c?.getContext('2d');
    if (!c || !ctx || !info) return;
    c.width = info.width;
    c.height = info.height;
    const header = headerRight != null ? { left: headerLeft, right: headerRight } : null;
    composeSstvPicture(ctx, info.width, info.height, txSource, topText, txBottom, header);
  }, [info, txSource, topText, txBottom, headerLeft, headerRight]);

  const onFile = async (file: File | undefined) => {
    if (!file) return;
    try {
      setTxSource(await createImageBitmap(file));
    } catch {
      /* not an image the browser can decode — keep the previous one */
    }
  };

  const onSend = () => {
    const c = canvasRef.current;
    const ctx = c?.getContext('2d');
    if (!c || !ctx) return;
    void send(rgbaToRgbBase64(ctx.getImageData(0, 0, c.width, c.height).data), fskId);
  };

  const pct = Math.round((tx?.progress ?? 0) * 100);

  return (
    <div className="sstv-send">
      <div className="sstv-adjust-row">
        <span>Mode</span>
        <select
          className="sstv-select"
          value={info?.name ?? ''}
          disabled={sending}
          onChange={(e) => setTxOptions({ txMode: e.target.value })}
        >
          {modeInfos.map((m) => (
            <option key={m.name} value={m.name}>
              {`${m.name} · ${mmss(m.durationMs)}`}
            </option>
          ))}
        </select>
        <span className="sstv-spacer" />
        <input
          ref={fileRef}
          type="file"
          accept="image/*"
          hidden
          onChange={(e) => {
            void onFile(e.target.files?.[0]);
            e.target.value = '';
          }}
        />
        <button
          type="button"
          className="btn sm"
          disabled={sending}
          onClick={() => fileRef.current?.click()}
        >
          LOAD PICTURE…
        </button>
      </div>
      <div className="sstv-adjust-row">
        <span>Top</span>
        <input
          className="sstv-input sstv-grow"
          value={txTop}
          placeholder={txHeader ? 'optional — the header strip names you' : myCall || 'your call'}
          disabled={sending}
          onChange={(e) => setTxOptions({ txTop: e.target.value })}
        />
      </div>
      <div className="sstv-adjust-row">
        <span>Bottom</span>
        <input
          className="sstv-input sstv-grow"
          value={txBottom}
          placeholder="e.g. CQ SSTV / UR 595"
          disabled={sending}
          onChange={(e) => setTxOptions({ txBottom: e.target.value })}
        />
      </div>
      <label className="sstv-adjust-row">
        <span>FSK</span>
        <input
          type="checkbox"
          checked={txFsk}
          disabled={sending}
          onChange={(e) => setTxOptions({ txFsk: e.target.checked })}
        />
        <span className="sstv-hint">
          {myCall
            ? `send ${myCall} as FSK ID after the picture`
            : 'set your callsign to send an FSK ID'}
        </span>
      </label>
      <label className="sstv-adjust-row">
        <span>Header</span>
        <input
          type="checkbox"
          checked={txHeader}
          disabled={sending}
          onChange={(e) => setTxOptions({ txHeader: e.target.checked })}
        />
        <span className="sstv-hint">
          {`strip across the top: ${myCall || 'your call'} · ${zeusHeaderVersion(zeusVersion)}`}
        </span>
      </label>

      <div className="sstv-canvas-wrap">
        <canvas
          ref={canvasRef}
          className="sstv-canvas"
          style={info ? { aspectRatio: `${info.width} / ${info.height}` } : undefined}
        />
      </div>

      <div className="sstv-adjust-row sstv-send-row">
        {sending ? (
          <>
            <button type="button" className="btn sm sstv-danger" onClick={() => void halt()}>
              HALT
            </button>
            <div
              className="sstv-progress"
              role="progressbar"
              aria-valuenow={pct}
              aria-valuemin={0}
              aria-valuemax={100}
            >
              <div className="sstv-progress-fill" style={{ width: `${pct}%` }} />
            </div>
            <span className="sstv-adjust-val">{`${tx?.mode ?? ''} ${pct}%`}</span>
          </>
        ) : (
          <>
            <button
              type="button"
              className="btn sm sstv-send-btn"
              disabled={!info || moxOn}
              onClick={onSend}
              title={
                moxOn
                  ? 'The transmitter is already keyed'
                  : `Transmit this picture now (${info ? mmss(info.durationMs) : ''} at full duty cycle)`
              }
            >
              {info ? `SEND · ${mmss(info.durationMs)}` : 'SEND'}
            </button>
            <span className="sstv-hint">
              {txError ??
                (tx?.lastError
                  ? `last picture: ${tx.lastError}`
                  : '100 % duty cycle — mind your PA')}
            </span>
          </>
        )}
      </div>
    </div>
  );
}
