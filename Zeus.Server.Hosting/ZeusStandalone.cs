// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Standalone (offline) mode — local fork addition.

using System;

namespace Zeus.Server.Hosting;

/// <summary>
/// Single switch for "this build does not depend on Zeus project infrastructure".
///
/// Upstream Zeus reaches out to a small family of project-operated hosts:
///
///   remote.openhpsdrzeus.com   /users/session, /billing/checkout  (access + billing)
///                              /users/heartbeat                   (user directory)
///                              /signal?role=host                  (remote-access broker)
///   downloads.openhpsdrzeus.com/latest.json                       (update manifest)
///                              /vst-engine/latest.json            (VST engine manifest)
///   openhpsdrzeus.com          /download, /go/CALLSIGN            (links, remote QR)
///
/// None of these are needed to operate a radio. When the project's
/// infrastructure is unavailable, the background clients above still spin —
/// reconnect loops, heartbeats, and update probes against hosts that no longer
/// answer — producing log noise, wakeups, and startup latency for no benefit.
///
/// This build therefore defaults to STANDALONE: those clients are simply never
/// registered, and remote user management is forced off. Set ZEUS_STANDALONE=0
/// to restore the upstream cloud integration if the infrastructure returns.
///
/// Third-party ham services (QRZ, ClubLog, LoTW, POTA/SOTA, DXSummit, cluster)
/// are INDEPENDENT of Zeus infrastructure and are deliberately NOT touched here.
/// They keep working exactly as upstream.
/// </summary>
public static class ZeusStandalone
{
    /// <summary>
    /// True unless explicitly disabled with ZEUS_STANDALONE=0 (or "off").
    /// </summary>
    public static bool Enabled
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("ZEUS_STANDALONE")?.Trim();
            if (string.IsNullOrEmpty(v)) return true;
            return !(string.Equals(v, "0", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(v, "off", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(v, "false", StringComparison.OrdinalIgnoreCase));
        }
    }
}
