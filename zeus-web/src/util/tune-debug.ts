// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Drag-tune gesture diagnostic overlay — `?tunedebug=1`.
//
// Field bug (iPad only, Safari AND Chrome — i.e. WebKit with the DESKTOP
// layout driven by touch; the laptop is desktop+mouse and the iPhone is the
// mobile shell, neither reproduces): on a fresh remote session a drag-tune
// either does nothing at all (the numerals stay put and the receiver never
// retunes, while tap-to-tune and typed entry work fine) or jumps by several
// kHz in a way that is NOT proportional to finger travel. It clears after a
// few minutes. Forcing a fresh view via zoom does not fix it, which rules out
// a stale spanHz snapshot — non-proportional jumps rule out any pure scale
// error, since a wrong scale is still a linear one.
//
// What's left is that the `dx` feeding the drag isn't one continuous gesture:
// onPointerMove computes `e.clientX - drag.startX` without checking that this
// event's pointerId is the pointer that STARTED the drag, so a second touch, a
// palm, or a pointer whose pointerup never arrived injects a foreign clientX
// into that subtraction. iPadOS desktop-mode can also deliver touch-derived
// and mouse-emulated pointer events for one gesture.
//
// Rather than guess a third time, this prints the raw inputs on screen so a
// single photographed drag settles it. Zero cost when the flag is absent: the
// overlay is never created and every log call returns immediately.
//
// REMOVE (or keep behind the flag) once the root cause is fixed.

let enabled: boolean | null = null;

export function tuneDebugEnabled(): boolean {
  if (enabled === null) {
    try {
      enabled = new URLSearchParams(window.location.search).get('tunedebug') === '1';
    } catch {
      enabled = false;
    }
  }
  return enabled;
}

let host: HTMLDivElement | null = null;
const lines: string[] = [];
const MAX_LINES = 14;

function ensureHost(): HTMLDivElement | null {
  if (host) return host;
  if (typeof document === 'undefined') return null;
  const el = document.createElement('div');
  el.style.cssText = [
    'position:fixed',
    'left:4px',
    'bottom:4px',
    'z-index:99999',
    'max-width:98vw',
    'max-height:46vh',
    'overflow:hidden',
    'background:rgba(0,0,0,0.82)',
    'color:#7CFC98',
    'font:11px/1.35 ui-monospace,Menlo,Consolas,monospace',
    'padding:6px 8px',
    'border:1px solid #2a3a52',
    'border-radius:4px',
    'white-space:pre-wrap',
    'word-break:break-all',
    'pointer-events:none',
  ].join(';');
  document.body.appendChild(el);
  host = el;
  return el;
}

/**
 * Append one diagnostic line. `fields` is printed as k=v pairs in order.
 * Numbers are rounded to keep the overlay readable on a tablet.
 */
export function tuneDebugLog(tag: string, fields: Record<string, unknown>): void {
  if (!tuneDebugEnabled()) return;
  const el = ensureHost();
  if (!el) return;
  const parts: string[] = [];
  for (const [k, v] of Object.entries(fields)) {
    let s: string;
    if (typeof v === 'number') {
      s = Number.isFinite(v) ? (Math.abs(v) >= 1000 ? v.toFixed(0) : v.toFixed(2)) : String(v);
    } else {
      s = String(v);
    }
    parts.push(`${k}=${s}`);
  }
  const t = new Date();
  const stamp = `${String(t.getSeconds()).padStart(2, '0')}.${String(t.getMilliseconds()).padStart(3, '0')}`;
  lines.push(`${stamp} ${tag} ${parts.join(' ')}`);
  while (lines.length > MAX_LINES) lines.shift();
  el.textContent = lines.join('\n');
}
