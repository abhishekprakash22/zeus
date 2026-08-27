// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Host claim key for the remote broker's callsign anti-squat (piece 3).
// A random 32-byte secret, generated once and persisted in zeus-prefs.db
// (same one-row LiteDB idiom as RemotePasswordStore / RemoteCallsignStore).
// The broker's SignalRoom binds the callsign's room to this key's SHA-256 on
// first connect; every later host connection must present the same key. The
// key never leaves the radio except in the X-Zeus-Host-Token header to the
// broker, and the broker stores only its hash. Losing the key (fresh SD card)
// is not fatal: an unrefreshed claim lapses after the broker's TTL and the
// room is reclaimable.

using LiteDB;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Hosting.Remote;

public sealed class RemoteHostKeyStore : IDisposable
{
    private sealed class Entry
    {
        public int Id { get; set; } = 1;
        public string Key { get; set; } = string.Empty;
    }

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<Entry> _col;
    private readonly ILogger<RemoteHostKeyStore> _log;
    private readonly object _sync = new();

    public RemoteHostKeyStore(ILogger<RemoteHostKeyStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _col = _db.GetCollection<Entry>("remote_host_key");
    }

    /// <summary>The persistent host key, generated on first use (64 hex chars).</summary>
    public string GetOrCreate()
    {
        lock (_sync)
        {
            var row = _col.FindById(1);
            if (row is not null && !string.IsNullOrEmpty(row.Key)) return row.Key;
            var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                .ToLowerInvariant();
            _col.Upsert(new Entry { Id = 1, Key = key });
            _log.LogInformation("remote host key generated (claims this radio's callsign at the broker)");
            return key;
        }
    }

    public void Dispose() => _dbLease.Dispose();
}
