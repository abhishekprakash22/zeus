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
// Per-install ANAN G2-Ultra front-panel mapping overrides: buttonId →
// ButtonAction and encoderId → EncoderAction, layered OVER the Thetis-derived
// defaults in G2PanelActionRouter. An empty store means "today's defaults" —
// byte-for-byte the historical behaviour. Rows are keyed by a composite string
// Id ("button:16" / "encoder:5"), which sidesteps the LiteDB Id=0
// always-inserts bug entirely (that bug is specific to int auto-ids, PR #387).
//
// Actions are stored as enum NAMES, not values, so a future reordering of the
// enums can never silently remap an operator's buttons. Unknown names (from a
// downgrade) are ignored at load with a warning — never guessed at.
//
// The MOX (7) and TUNE (6) buttons and the main VFO knob are NOT governed by
// this store: the router pins them unconditionally (operator decision — a
// transmitter key must always be where the panel legend says it is), and the
// VFO knob is a separate ANDROMEDA event type (ZZZU/ZZZD) that never consults
// the mapping layer. Writes for the pinned buttons are rejected at the API.

using LiteDB;

namespace Zeus.Server.FrontPanel;

public sealed class G2PanelMappingStore : IDisposable
{
    public const string KindButton = "button";
    public const string KindEncoder = "encoder";

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<G2PanelMappingEntry> _rows;
    private readonly ILogger<G2PanelMappingStore> _log;
    private readonly object _sync = new();

    // Fired on any write so the router rebuilds its override caches.
    public event Action? Changed;

    public G2PanelMappingStore(ILogger<G2PanelMappingStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _rows = _db.GetCollection<G2PanelMappingEntry>("g2_panel_mappings");

        _log.LogInformation("G2PanelMappingStore initialized at {Path}", dbPath);
    }

    private static string Key(string kind, int id) => $"{kind}:{id}";

    /// <summary>All stored overrides for one kind, as inputId → action name.
    /// A fresh install (no rows) returns an empty dictionary — defaults apply.</summary>
    public Dictionary<int, string> Overrides(string kind)
    {
        lock (_sync)
        {
            var result = new Dictionary<int, string>();
            foreach (var row in _rows.FindAll())
            {
                if (!string.Equals(row.Kind, kind, StringComparison.Ordinal)) continue;
                result[row.InputId] = row.Action;
            }
            return result;
        }
    }

    /// <summary>Set (or clear, with <paramref name="action"/> null) one
    /// override. Callers validate kind / id / action; the store persists
    /// whatever it is handed and fires <see cref="Changed"/>.</summary>
    public void SetOverride(string kind, int id, string? action)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(action))
            {
                _rows.Delete(Key(kind, id));
            }
            else
            {
                _rows.Upsert(new G2PanelMappingEntry
                {
                    Id = Key(kind, id),
                    Kind = kind,
                    InputId = id,
                    Action = action.Trim(),
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
        }
        _log.LogInformation("g2panel.mapping.set kind={Kind} id={Id} action={Action}",
            kind, id, action ?? "(default)");
        Changed?.Invoke();
    }

    /// <summary>Delete every override — the panel returns to the shipped
    /// defaults on the next event.</summary>
    public void ResetAll()
    {
        lock (_sync)
            _rows.DeleteAll();
        _log.LogInformation("g2panel.mapping.reset");
        Changed?.Invoke();
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class G2PanelMappingEntry
{
    // Composite string key "kind:id" — string ids don't hit LiteDB's int Id=0
    // upsert bug, so Upsert is safe here.
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public int InputId { get; set; }
    public string Action { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
}
