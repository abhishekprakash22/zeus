// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// update-apply-ride — the one poll loop that follows a server-side in-place
// update to its end. The server runs at most one apply (BeginApply joins an
// in-flight run), so an install started on ANY screen is the same run
// everywhere; every surface that shows update progress — Settings → Updates,
// the startup toast — rides it with this helper instead of owning a private
// copy of the loop. Field lesson behind it: the kiosk and a LAN PC each
// offered the same install, the second click raced the first's restart, and
// the second screen's UI hung on a phase it could never leave.

import { fetchUpdateStatus, getUpdateApplyStatus, type UpdateApplyStatusDto } from '../api/client';

/** True while the server-side apply is somewhere between "started" and
 * "process gone for the restart". These are the phases worth adopting —
 * idle/failed/unsupported are terminal and offer nothing to follow. */
export function isApplyActive(phase: UpdateApplyStatusDto['phase']): boolean {
  return (
    phase === 'downloading'
    || phase === 'verifying'
    || phase === 'swapping'
    || phase === 'restarting'
  );
}

export type RideApplyOptions = {
  /** Live phase/percent while the apply runs. Not called for terminal phases. */
  onStatus: (st: UpdateApplyStatusDto) => void;
  /** The apply ended in failure ('failed' / 'unsupported'). */
  onFailed: (st: UpdateApplyStatusDto) => void;
  /** The server stopped answering — the restart is underway. Called once. */
  onRestarting?: () => void;
  /** Test seam. Defaults to a hard reload so the SPA matches the new build. */
  reload?: () => void;
  /** Test seam. Defaults to real timers. */
  sleep?: (ms: number) => Promise<void>;
};

/**
 * Follow a running in-place update: poll its phase, and when the server dies
 * for the restart, keep probing until it's back — then hard-reload so the
 * SPA matches the new build. Two missed polls are read as "the restart", the
 * same tolerance the loop always had; a run that lands on 'idle' (finished
 * or never started under us) simply returns.
 *
 * Deliberately survives its caller: if the panel that started the ride
 * unmounts, the loop still reloads the page when the radio returns — after a
 * radio update every open client is stale, so the reload is the point.
 */
export async function rideApply(opts: RideApplyOptions): Promise<void> {
  const sleep = opts.sleep ?? ((ms: number) => new Promise<void>((r) => setTimeout(r, ms)));
  const reload = opts.reload ?? (() => window.location.reload());
  let missedPolls = 0;
  for (;;) {
    await sleep(800);
    try {
      const cur = await getUpdateApplyStatus();
      missedPolls = 0;
      if (cur.phase === 'failed' || cur.phase === 'unsupported') {
        opts.onFailed(cur);
        return;
      }
      if (cur.phase === 'idle') return;
      opts.onStatus(cur);
    } catch {
      // Server going away during 'restarting' is the plan working.
      missedPolls++;
      if (missedPolls >= 2) {
        opts.onRestarting?.();
        for (;;) {
          await sleep(1200);
          try {
            await fetchUpdateStatus(false);
            reload();
            return;
          } catch {
            /* still rebooting */
          }
        }
      }
    }
  }
}
