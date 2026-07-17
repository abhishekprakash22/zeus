import { DurableObject } from 'cloudflare:workers';
import type { Env } from './types';
import { CrashStoreCore } from './crash-store-core';

/**
 * Single Durable Object holding the most recent auto-shared crash records, keyed
 * by operator callsign.
 *
 * Data flow (Phase 3c "crash auto-share"):
 *   1. An operator opts in to BOTH the L1 "remote diagnostics" master switch AND
 *      the "auto-share crash reports" sub-toggle (default OFF).
 *   2. When the Zeus backend dies WITHOUT a clean-exit marker, the out-of-process
 *      sidecar (Zeus.SupportAgent) — which survives the crash — writes a
 *      SupportCrashRecord to disk and, only if auto-share is on, POSTs it to the
 *      broker's `/crash` endpoint (QRZ-authenticated as the operator's callsign).
 *   3. index.ts verifies the QRZ headers, derives the edge IP/country, then
 *      forwards the body here under the verified callsign. The record is ALREADY
 *      redacted server-side by the backend's diagnostics layer; the broker stores
 *      it verbatim and adds only receipt metadata (receivedAt, ip, country).
 *   4. A maintainer browses the crash-bearing operators via `GET /admin/crashes`
 *      (index) and drills into one via `GET /admin/crashes?callsign=<cs>` — both
 *      admin-Bearer-gated.
 *
 * Storage policy: a durable per-callsign index capped at 20, plus fixed-size
 * chunks for each raw record. The edge accepts crash bodies up to 256 KiB, larger
 * than a single Durable Object value slot, so chunking keeps every stored value
 * comfortably below the storage limit while preserving the external /put, /list,
 * and /index contract.
 */
export class CrashStore extends DurableObject<Env> {
  override async fetch(request: Request): Promise<Response> {
    return new CrashStoreCore(this.ctx.storage).fetch(request);
  }
}
