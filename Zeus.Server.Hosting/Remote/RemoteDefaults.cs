// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// THE one place the remote-operation infrastructure endpoints live. This
/// build ships G2 users pointed at Apache Labs infrastructure so the entire
/// end-user setup is: enter callsign, set password, scan QR. Sovereignty is
/// preserved for self-hosters: ZEUS_REMOTE_BROKER_URL and ZEUS_REMOTE_ORIGIN
/// env vars override these at runtime, and the broker/relay sources ship in
/// this tree (cloud/zeus-remote-broker/, tools/zeus-remote-relay/).
///
/// ⚠ SET BEFORE SHIPPING: point both constants at the production hostname
/// once DNS + TLS + coturn are live. Until then the broker is unreachable,
/// which costs nothing — no connection is attempted until a session
/// password is set, and the reconnect loop backs off quietly.
/// </summary>
public static class RemoteDefaults
{
    /// <summary>Host-role signaling socket the radio keeps on the broker.</summary>
    public const string BrokerSignalUrl = "wss://ananremote.com/signal?role=host";

    /// <summary>Origin serving the remote client page and /go/&lt;callsign&gt; QR addresses.</summary>
    public const string BrokerOrigin = "https://ananremote.com";
}
