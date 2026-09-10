// Bare-root landing for the hosted remote client (app.ananremote.com and the
// pages.dev aliases). Reached when the hosted bundle is opened with no
// callsign — previously that path fell through to upstream's QRZ/subscription
// entitlement wall (QrzAccessGate), which the fork's access model has no use
// for (ADR-0008: the radio's SPAKE2+ session password is the sole
// authenticator). This screen asks the one question that context can answer:
// which radio? Entering a callsign navigates to /?remote=<CALL>, the same
// canonical form the printed /go/<CALL> links resolve to.

import { useState, type FormEvent } from 'react';
import { getLastRemoteCallsign } from '../remote/remote-client';

function go(callsign: string): void {
  const cs = callsign.trim().toUpperCase();
  if (!cs) return;
  window.location.href = `/?remote=${encodeURIComponent(cs)}`;
}

export function HostedLanding() {
  const [callsign, setCallsign] = useState('');
  const last = getLastRemoteCallsign();

  const submit = (e: FormEvent) => {
    e.preventDefault();
    go(callsign);
  };

  return (
    <div className="hosted-landing">
      <img
        className="hosted-landing-art"
        src="/branding/splash-pulse.svg"
        alt="ANAN Core"
      />
      <div className="hosted-landing-card">
        <h1>ANAN Core Remote</h1>
        <p>
          Enter the callsign of the radio you want to reach. You will be asked
          for that radio&apos;s session password — no account is needed.
        </p>
        <form onSubmit={submit}>
          <input
            type="text"
            autoFocus
            autoCapitalize="characters"
            autoCorrect="off"
            spellCheck={false}
            placeholder="CALLSIGN"
            value={callsign}
            onChange={(e) => setCallsign(e.target.value.toUpperCase())}
            aria-label="Radio callsign"
          />
          <button type="submit" className="btn" disabled={!callsign.trim()}>
            Connect
          </button>
        </form>
        {last ? (
          <button type="button" className="btn ghost hosted-landing-resume" onClick={() => go(last)}>
            Resume {last}
          </button>
        ) : null}
      </div>
      <style>{`
        .hosted-landing {
          min-height: 100vh; display: flex; flex-direction: column;
          align-items: center; justify-content: center; gap: 20px;
          padding: 24px; box-sizing: border-box;
        }
        .hosted-landing-art { width: min(520px, 86vw); height: auto; }
        .hosted-landing-card {
          width: min(420px, 92vw); padding: 22px;
          background: var(--bg-2, #1c2129);
          border: 1px solid var(--line, #2c333d); border-radius: 10px;
        }
        .hosted-landing-card h1 {
          margin: 0 0 6px; font-size: 20px; letter-spacing: 0.5px;
          color: var(--fg-0, #e8edf2);
        }
        .hosted-landing-card p {
          margin: 0 0 14px; font-size: 13px; line-height: 1.5;
          color: var(--fg-1, #c3cad3);
        }
        .hosted-landing-card form { display: flex; gap: 8px; }
        .hosted-landing-card input {
          flex: 1; min-width: 0; padding: 10px 12px; font-size: 16px;
          letter-spacing: 2px; text-transform: uppercase;
          background: var(--bg-1, #14181e); color: var(--fg-0, #e8edf2);
          border: 1px solid var(--line, #2c333d); border-radius: 6px;
        }
        .hosted-landing-resume { margin-top: 10px; width: 100%; }
      `}</style>
    </div>
  );
}
