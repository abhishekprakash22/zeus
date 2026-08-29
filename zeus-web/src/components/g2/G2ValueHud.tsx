// G2ValueHud — the encoder's visual answer. When any watched control value
// changes (AF, AGC-T, ATT, SQL, RIT/XIT, drive, mic — everything EXCEPT the
// tuning encoder, whose answer is the dial itself), a transient chip appears
// on the ACTIVE receiver's pane naming the control and its new value, then
// fades after a moment of stillness. Watching the STORES (not input devices)
// means the panel encoders, touch sliders, and keyboard all light it equally.
import { useEffect, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { useConnectionStore } from '../../state/connection-store';
import { useTxStore } from '../../state/tx-store';

type Watched = { label: string; value: number | undefined; fmt: (v: number) => string };

const db = (v: number) => `${v > 0 ? '+' : ''}${Math.round(v)} dB`;
const num = (v: number) => `${Math.round(v)}`;
const hz = (v: number) => `${v > 0 ? '+' : ''}${Math.round(v)} Hz`;
const pct = (v: number) => `${Math.round(v)}%`;

function snapshot(rxIndex: number): Watched[] {
  const s = useConnectionStore.getState();
  const t = useTxStore.getState();
  const rx = s.receivers[rxIndex];
  const af = rxIndex === 0 ? s.rxAfGainDb : rx?.afGainDb;
  return [
    { label: 'AF', value: af ?? undefined, fmt: db },
    { label: 'AGC-T', value: s.agcTopDb, fmt: num },
    { label: 'ATT', value: s.attenDb, fmt: (v) => `${Math.round(v)} dB` },
    { label: 'SQL', value: s.squelch?.level, fmt: num },
    { label: 'RIT', value: s.ritHz, fmt: hz },
    { label: 'DRIVE', value: t.drivePercent, fmt: pct },
    { label: 'MIC', value: t.micGainDb, fmt: db },
  ];
}

export function G2ValueHud({ rxIndex }: { rxIndex: number }) {
  // The chip stays MOUNTED once it has ever shown: visibility is pure
  // opacity, so appearing is a ~90 ms fade instead of a cold mount, turning
  // an encoder updates the text in place with no flicker, and release gives
  // a long readable hold before a soft fade-out.
  const HOLD_MS = 2500;
  const [text, setText] = useState<string | null>(null);
  const [visible, setVisible] = useState(false);
  const prev = useRef<(number | undefined)[] | null>(null);
  const timer = useRef<number | null>(null);

  useEffect(() => {
    prev.current = null; // re-baseline when the active pane changes
    const tick = () => {
      const now = snapshot(rxIndex);
      const before = prev.current;
      prev.current = now.map((w) => w.value);
      if (!before) return; // first snapshot is a baseline, never a flash
      for (let i = 0; i < now.length; i++) {
        const w = now[i];
        if (w?.value === undefined || before[i] === undefined) continue;
        if (w.value !== before[i]) {
          setText(`${w.label} ${w.fmt(w.value)}`);
          setVisible(true);
          if (timer.current) window.clearTimeout(timer.current);
          timer.current = window.setTimeout(() => setVisible(false), HOLD_MS);
        }
      }
    };
    const unsubA = useConnectionStore.subscribe(tick);
    const unsubB = useTxStore.subscribe(tick);
    return () => {
      unsubA();
      unsubB();
      if (timer.current) window.clearTimeout(timer.current);
    };
  }, [rxIndex]);

  if (!text) return null;
  return (
    <div
      style={{
        ...hud,
        opacity: visible ? 1 : 0,
        transform: `translateX(-50%) translateY(${visible ? 0 : -4}px)`,
        transition: visible
          ? 'opacity 90ms ease-out, transform 90ms ease-out'
          : 'opacity 280ms ease-in, transform 280ms ease-in',
      }}
    >
      {text}
    </div>
  );
}

const hud: CSSProperties = {
  position: 'absolute',
  top: 14,
  left: '50%',
  zIndex: 40,
  willChange: 'opacity, transform',
  padding: '6px 16px',
  borderRadius: 8,
  background: 'rgba(10,12,16,0.82)',
  border: '1px solid var(--accent, #4aa3df)',
  color: 'var(--accent, #4aa3df)',
  fontFamily: '"JetBrains Mono", Consolas, monospace',
  fontSize: 20,
  fontWeight: 700,
  letterSpacing: 1,
  pointerEvents: 'none',
};
