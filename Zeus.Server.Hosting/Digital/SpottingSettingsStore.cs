// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR),
//                         Ramón Martínez (EA5IUE), and contributors.
//
// Spotting opt-in, persisted. Uploading decodes to PSK Reporter / WSPRnet is
// network egress carrying the operator's callsign and grid, so it is strictly
// opt-in and survives restarts only because the operator asked for it: both
// flags default false and an empty callsign or grid keeps the uploaders off
// whatever the flags say (both networks attribute every spot to a station).

using LiteDB;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Hosting.Digital;

/// <summary>Operator-facing spotting configuration (the Spotting settings panel).</summary>
public sealed record SpottingSettings(
    bool PskReporterEnabled,
    bool WsprnetEnabled,
    string Callsign,
    string Grid)
{
    public static SpottingSettings Default { get; } = new(false, false, "", "");

    public SpottingSettings Normalized() => new(
        PskReporterEnabled,
        WsprnetEnabled,
        (Callsign ?? "").Trim().ToUpperInvariant(),
        (Grid ?? "").Trim().ToUpperInvariant());

    /// <summary>Both networks attribute spots to a station: no identity, no upload.</summary>
    public bool IdentityResolved =>
        !string.IsNullOrWhiteSpace(Callsign) && !string.IsNullOrWhiteSpace(Grid);
}

internal sealed class SpottingSettingsEntry
{
    [BsonId] public int Id { get; set; } = 1;
    public bool PskReporterEnabled { get; set; }
    public bool WsprnetEnabled { get; set; }
    public string Callsign { get; set; } = "";
    public string Grid { get; set; } = "";
}

public sealed class SpottingSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly ILiteCollection<SpottingSettingsEntry> _entries;
    private readonly ILogger<SpottingSettingsStore> _log;
    private readonly object _sync = new();

    public SpottingSettingsStore(ILogger<SpottingSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _entries = _dbLease.Database.GetCollection<SpottingSettingsEntry>("spotting_settings");
        _log.LogInformation("SpottingSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>Raised after a successful <see cref="Set"/> so the uploaders re-read.</summary>
    public event Action<SpottingSettings>? Changed;

    public SpottingSettings Get()
    {
        lock (_sync)
        {
            var e = _entries.FindById(1);
            if (e is null) return SpottingSettings.Default;
            return new SpottingSettings(
                e.PskReporterEnabled, e.WsprnetEnabled, e.Callsign, e.Grid).Normalized();
        }
    }

    public SpottingSettings Set(SpottingSettings settings)
    {
        var s = settings.Normalized();
        lock (_sync)
        {
            _entries.Upsert(new SpottingSettingsEntry
            {
                Id = 1,
                PskReporterEnabled = s.PskReporterEnabled,
                WsprnetEnabled = s.WsprnetEnabled,
                Callsign = s.Callsign,
                Grid = s.Grid,
            });
        }
        Changed?.Invoke(s);
        return s;
    }

    public void Dispose() => _dbLease.Dispose();
}
