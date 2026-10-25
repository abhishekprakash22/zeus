// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// MON level — the operator's listening level for the TX monitor, persisted on
// the radio (prefs db) so it survives restarts. Attenuation only
// (DspPipelineService.MinTxMonitorLevelDb .. 0 dB, default 0 dB = unchanged):
// it scales what the operator HEARS, never the TX audio. Lowering it lowers
// the loop gain on boards whose headphone output couples into the mic input.

using LiteDB;
using Microsoft.Extensions.Hosting;

namespace Zeus.Server;

public sealed class TxMonitorLevelStore : IDisposable
{
    public const double DefaultLevelDb = 0.0;

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly ILiteCollection<TxMonitorLevelEntry> _docs;
    private readonly ILogger<TxMonitorLevelStore> _log;
    private readonly object _sync = new();
    private bool _cacheLoaded;
    private double _cachedDb = DefaultLevelDb;

    public event Action<double>? Changed;

    public TxMonitorLevelStore(ILogger<TxMonitorLevelStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _docs = _dbLease.Database.GetCollection<TxMonitorLevelEntry>("tx_monitor_level");
        _log.LogInformation("TxMonitorLevelStore initialized at {Path}", dbPath);
    }

    public static double Clamp(double db) =>
        double.IsFinite(db)
            ? Math.Clamp(db, DspPipelineService.MinTxMonitorLevelDb, 0.0)
            : DefaultLevelDb;

    public double LevelDb
    {
        get
        {
            lock (_sync)
            {
                EnsureCacheLocked();
                return _cachedDb;
            }
        }
    }

    public double Set(double db)
    {
        double clamped = Clamp(db);
        bool changed;
        lock (_sync)
        {
            EnsureCacheLocked();
            changed = _cachedDb != clamped;

            var e = _docs.FindAll().FirstOrDefault() ?? new TxMonitorLevelEntry();
            e.LevelDb = clamped;
            e.UpdatedUtc = DateTime.UtcNow;
            if (e.Id == 0) _docs.Insert(e);
            else _docs.Update(e);

            _cachedDb = clamped;
            _cacheLoaded = true;
        }

        if (changed) Changed?.Invoke(clamped);
        return clamped;
    }

    public void Dispose() => _dbLease.Dispose();

    private void EnsureCacheLocked()
    {
        if (_cacheLoaded) return;
        _cachedDb = Clamp(_docs.FindAll().FirstOrDefault()?.LevelDb ?? DefaultLevelDb);
        _cacheLoaded = true;
    }
}

public sealed class TxMonitorLevelEntry
{
    public int Id { get; set; }
    public double LevelDb { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Applies the persisted MON level to the pipeline at start and on
/// every change, so the pipeline needs no store dependency.</summary>
public sealed class TxMonitorLevelApplier : IHostedService
{
    private readonly TxMonitorLevelStore _store;
    private readonly DspPipelineService _pipe;

    public TxMonitorLevelApplier(TxMonitorLevelStore store, DspPipelineService pipe)
    {
        _store = store;
        _pipe = pipe;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _store.Changed += OnChanged;
        _pipe.SetTxMonitorLevelDb(_store.LevelDb);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _store.Changed -= OnChanged;
        return Task.CompletedTask;
    }

    private void OnChanged(double db) => _pipe.SetTxMonitorLevelDb(db);
}
