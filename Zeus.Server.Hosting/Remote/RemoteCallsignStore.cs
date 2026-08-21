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

using System.Text.RegularExpressions;
using LiteDB;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// Persists the operator's remote-access callsign in a one-row LiteDB
/// collection sharing zeus-prefs.db, mirroring RemotePasswordStore. This is
/// the UI-first home for the identity the broker announces; the
/// ZEUS_REMOTE_CALLSIGN env var still overrides it (power users, fleet
/// provisioning), and with neither set the QRZ-session identity path is the
/// fallback — exactly the pre-existing behaviour. The broker is untrusted by
/// design (ADR-0007); a locally declared callsign is sufficient because
/// SPAKE2+ is the real authenticator.
/// </summary>
public sealed class RemoteCallsignStore : IDisposable
{
    private static readonly Regex ValidCallsign = new("^[A-Z0-9/]{3,12}$", RegexOptions.Compiled);

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<RemoteCallsignEntry> _col;
    private readonly ILogger<RemoteCallsignStore> _log;
    private readonly object _sync = new();

    public RemoteCallsignStore(ILogger<RemoteCallsignStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _col = _db.GetCollection<RemoteCallsignEntry>("remote_callsign");
    }

    /// <summary>Normalize an operator-entered callsign; null when invalid.</summary>
    public static string? Normalize(string? raw)
    {
        var c = raw?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(c)) return null;
        return ValidCallsign.IsMatch(c) ? c : null;
    }

    /// <summary>The stored callsign, or null when unset.</summary>
    public string? Get()
    {
        lock (_sync)
            return _col.FindAll().FirstOrDefault()?.Callsign;
    }

    /// <summary>Set or replace the stored callsign (pre-normalized).</summary>
    public void Set(string callsign)
    {
        lock (_sync)
        {
            _col.DeleteAll();
            _col.Insert(new RemoteCallsignEntry { Callsign = callsign });
        }
        _log.LogInformation("remote-access callsign set to {Callsign}", callsign);
    }

    /// <summary>Remove the stored callsign — identity falls back to env/QRZ.</summary>
    public void Clear()
    {
        lock (_sync)
            _col.DeleteAll();
        _log.LogInformation("remote-access callsign cleared");
    }

    public void Dispose() => _dbLease.Dispose();

    private sealed class RemoteCallsignEntry
    {
        public int Id { get; set; }
        public string Callsign { get; set; } = "";
    }
}

public sealed record RemoteCallsignRequest(string Callsign);
