// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR),
//                         Ramón Martínez (EA5IUE), and contributors.
//
// Publish a QSO to the operator's QRZ.com logbook the moment it is logged.
//
// Publishing already existed, but only as a deliberate act: select entries in
// the logbook, press publish. That is fine for a contest log typed up later
// and wrong for FT8, where the sequencer logs a QSO every couple of minutes
// and the operator is not going to keep coming back to push them out.
//
// This is the fourth target on the logged-QSO fan-out in ZeusEndpoints, beside
// the WSJT-X and N1MM UDP broadcasts and Cloudlog/Club Log: OFF by default,
// a no-op when off, and fire-and-forget so QRZ being slow or down can never
// delay — let alone fail — the operator's own log entry. ADIF bulk import
// never comes through here, so importing a year of QSOs does not upload them.

using LiteDB;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Operator preference: publish each QSO to QRZ.com as it is logged.</summary>
public sealed record QrzPublishSettings(bool AutoPublishOnLog)
{
    public static QrzPublishSettings Default { get; } = new(false);
}

internal sealed class QrzPublishSettingsEntry
{
    [BsonId] public int Id { get; set; } = 1;
    public bool AutoPublishOnLog { get; set; }
}

public sealed class QrzPublishSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly ILiteCollection<QrzPublishSettingsEntry> _entries;
    private readonly object _sync = new();

    public QrzPublishSettingsStore(ILogger<QrzPublishSettingsStore> log, string? dbPathOverride = null)
    {
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _entries = _dbLease.Database.GetCollection<QrzPublishSettingsEntry>("qrz_publish_settings");
        log.LogInformation("QrzPublishSettingsStore initialized at {Path}", dbPath);
    }

    public QrzPublishSettings Get()
    {
        lock (_sync)
        {
            var e = _entries.FindById(1);
            return e is null ? QrzPublishSettings.Default : new QrzPublishSettings(e.AutoPublishOnLog);
        }
    }

    public QrzPublishSettings Set(QrzPublishSettings settings)
    {
        lock (_sync)
        {
            _entries.Upsert(new QrzPublishSettingsEntry { Id = 1, AutoPublishOnLog = settings.AutoPublishOnLog });
        }
        return settings;
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class QrzAutoPublishService
{
    private readonly QrzPublishSettingsStore _settings;
    private readonly QrzService _qrz;
    private readonly LogbookPluginBridge _logbook;
    private readonly ILogger<QrzAutoPublishService> _log;

    public QrzAutoPublishService(
        QrzPublishSettingsStore settings,
        QrzService qrz,
        LogbookPluginBridge logbook,
        ILogger<QrzAutoPublishService> log)
    {
        _settings = settings;
        _qrz = qrz;
        _logbook = logbook;
        _log = log;
    }

    /// <summary>Why a QSO was (not) sent to QRZ — the whole gate, in one place.</summary>
    internal enum PublishDecision { Off, NoApiKey, Publish }

    /// <summary>
    /// Publishing needs BOTH the operator's opt-in and a logbook API key. The
    /// key alone must never start uploading (it is also what a manual publish
    /// uses), and the opt-in alone cannot upload anything.
    /// </summary>
    internal static PublishDecision Decide(bool enabled, bool hasApiKey) =>
        !enabled ? PublishDecision.Off
        : !hasApiKey ? PublishDecision.NoApiKey
        : PublishDecision.Publish;

    public QrzPublishSettings Settings => _settings.Get();

    public QrzPublishSettings Configure(QrzPublishSettings settings) => _settings.Set(settings);

    /// <summary>
    /// Publishes <paramref name="entry"/> when the operator asked for it. Never
    /// throws: the QSO is already in the local log, and a failed upload must
    /// leave it there, unpublished, rather than take anything down with it. The
    /// entry keeps its QRZ id on success, so the logbook shows it as synced and
    /// a later manual publish does not duplicate it.
    /// </summary>
    public async Task PublishIfEnabledAsync(LogEntry entry, CancellationToken ct)
    {
        try
        {
            var decision = Decide(_settings.Get().AutoPublishOnLog, _qrz.GetStatus().HasApiKey);
            if (decision == PublishDecision.Off) return;
            if (decision == PublishDecision.NoApiKey)
            {
                _log.LogInformation(
                    "qrz.autopublish: {Call} not published — no logbook API key configured", entry.Callsign);
                return;
            }

            var result = await _qrz.PublishLogEntryAsync(entry, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                _log.LogInformation(
                    "qrz.autopublish: {Call} rejected by QRZ ({Reason}) — the QSO stays in the local log",
                    entry.Callsign, result.Message ?? "no reason given");
                return;
            }

            if (!string.IsNullOrEmpty(result.QrzLogId) && _logbook.Current is { } plugin)
                await plugin.UpdateQrzUploadStatusAsync(entry.Id, result.QrzLogId, ct).ConfigureAwait(false);

            _log.LogInformation("qrz.autopublish: {Call} published (QRZ id {Id})",
                entry.Callsign, result.QrzLogId ?? "-");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "qrz.autopublish: publishing {Call} failed", entry.Callsign);
        }
    }
}
