// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// FreeDvSettingsStore — persists the FreeDV modem preferences (submode,
// auto-detect, SNR squelch, TX text) and the FreeDV Reporter opt-in settings
// in single-row LiteDB collections sharing zeus-prefs.db, mirroring
// OperatorIdentityStore / Ft8SettingsStore. First run returns defaults that
// match freedv-gui's out-of-box behaviour (700D, squelch on at -2 dB).
// Reporter settings are STRICTLY opt-in: reportEnabled defaults false.

using LiteDB;
using Zeus.Server;   // PrefsDbPath

namespace Zeus.Server.Hosting.FreeDv;

public sealed record FreeDvModemSettings(
    FreeDvSubmode Submode,
    bool AutoDetect,
    bool SquelchEnabled,
    double SnrSquelchThreshDb,
    string TxText)
{
    public static FreeDvModemSettings Default { get; } =
        new(FreeDvSubmode.Mode700D, AutoDetect: false,
            SquelchEnabled: true, SnrSquelchThreshDb: -2.0, TxText: "");
}

public sealed record FreeDvReporterSettings(
    bool ReportEnabled,
    string Callsign,
    string GridSquare,
    string Message)
{
    public static FreeDvReporterSettings Default { get; } = new(false, "", "", "");

    public FreeDvReporterSettings Normalized() => new(
        ReportEnabled,
        Callsign.Trim().ToUpperInvariant(),
        GridSquare.Trim().ToUpperInvariant(),
        Message.Trim().Length > 120 ? Message.Trim()[..120] : Message.Trim());
}

public sealed class FreeDvSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<FreeDvModemEntry> _modem;
    private readonly ILiteCollection<FreeDvReporterEntry> _reporter;
    private readonly ILogger<FreeDvSettingsStore> _log;
    private readonly object _sync = new();

    public FreeDvSettingsStore(ILogger<FreeDvSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _modem = _db.GetCollection<FreeDvModemEntry>("freedv_modem_settings");
        _reporter = _db.GetCollection<FreeDvReporterEntry>("freedv_reporter_settings");

        _log.LogInformation("FreeDvSettingsStore initialized at {Path}", dbPath);
    }

    public FreeDvModemSettings GetModem()
    {
        lock (_sync)
        {
            var e = _modem.FindAll().FirstOrDefault();
            if (e is null) return FreeDvModemSettings.Default;
            return new FreeDvModemSettings(
                Enum.IsDefined((FreeDvSubmode)e.Submode)
                    ? (FreeDvSubmode)e.Submode : FreeDvSubmode.Mode700D,
                e.AutoDetect,
                e.SquelchEnabled,
                Math.Clamp(e.SnrSquelchThreshDb, -5.0, 15.0),
                e.TxText ?? "");
        }
    }

    public void SetModem(FreeDvModemSettings s)
    {
        lock (_sync)
        {
            var e = _modem.FindAll().FirstOrDefault() ?? new FreeDvModemEntry();
            e.Submode = (byte)s.Submode;
            e.AutoDetect = s.AutoDetect;
            e.SquelchEnabled = s.SquelchEnabled;
            e.SnrSquelchThreshDb = s.SnrSquelchThreshDb;
            e.TxText = s.TxText;
            e.UpdatedUtc = DateTime.UtcNow;
            _modem.Upsert(e);
        }
    }

    public FreeDvReporterSettings GetReporter()
    {
        lock (_sync)
        {
            var e = _reporter.FindAll().FirstOrDefault();
            if (e is null) return FreeDvReporterSettings.Default;
            return new FreeDvReporterSettings(
                e.ReportEnabled, e.Callsign ?? "", e.GridSquare ?? "", e.Message ?? "")
                .Normalized();
        }
    }

    public FreeDvReporterSettings SetReporter(FreeDvReporterSettings s)
    {
        var n = s.Normalized();
        lock (_sync)
        {
            var e = _reporter.FindAll().FirstOrDefault() ?? new FreeDvReporterEntry();
            e.ReportEnabled = n.ReportEnabled;
            e.Callsign = n.Callsign;
            e.GridSquare = n.GridSquare;
            e.Message = n.Message;
            e.UpdatedUtc = DateTime.UtcNow;
            _reporter.Upsert(e);
        }
        return n;
    }

    public void Dispose() => _dbLease.Dispose();

    private sealed class FreeDvModemEntry
    {
        public int Id { get; set; } = 1;
        public byte Submode { get; set; }
        public bool AutoDetect { get; set; }
        public bool SquelchEnabled { get; set; } = true;
        public double SnrSquelchThreshDb { get; set; } = -2.0;
        public string? TxText { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class FreeDvReporterEntry
    {
        public int Id { get; set; } = 1;
        public bool ReportEnabled { get; set; }
        public string? Callsign { get; set; }
        public string? GridSquare { get; set; }
        public string? Message { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
