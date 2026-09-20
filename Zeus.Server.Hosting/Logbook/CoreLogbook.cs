// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR),
//                         Ramón Martínez (EA5IUE), and contributors.

using System.Globalization;
using System.Text;
using LiteDB;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Server;

/// <summary>
/// The logbook that ships with Zeus itself, stored in its own LiteDB file
/// (zeus-core-logbook.db, see <see cref="PrefsDbPath.CoreLogbookPath"/>).
///
/// The /api/log/* surface, QRZ publishing, LoTW sync and Cloudlog upload were
/// all written against <see cref="ILogbookPlugin"/>, but nothing in the tree
/// implements it — so on a stock install every QSO the operator logs is
/// dropped on the floor and /api/log/capabilities answers
/// pluginInstalled:false. This is that implementation, attached by
/// <see cref="LogbookPluginBridge"/> as a FALLBACK: install a real logbook
/// plugin and it takes over, exactly as before.
/// </summary>
public sealed class CoreLogbook : ILogbookPluginV2, IDisposable
{
    private const string CollectionName = "core_logbook";

    // FT8/FT4 and friends. GetDigitalWorkedCallsignsAsync feeds the digital
    // pop-out's worked-B4 highlight, which must never light up off a phone or
    // CW contact.
    private static readonly HashSet<string> DigitalModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FT8", "FT4", "JT65", "JT9", "JT4", "FST4", "FST4W", "Q65", "MSK144",
        "JS8", "WSPR", "T10", "FSK441", "ISCAT",
    };

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<CoreLogbookDocument> _docs;
    private readonly ILogger<CoreLogbook> _log;
    private readonly object _sync = new();

    public CoreLogbook(ILogger<CoreLogbook> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _docs = _db.GetCollection<CoreLogbookDocument>(CollectionName);
        _docs.EnsureIndex(d => d.CallsignUpper);
        _docs.EnsureIndex(d => d.QsoDateTimeUtc);
        _log.LogInformation("CoreLogbook initialized at {Path} ({Count} QSOs)", dbPath, _docs.Count());
    }

    public Task<LogbookEntrySnapshot> CreateAsync(LogbookNewEntry entry, CancellationToken ct = default)
    {
        var call = Normalize(entry.Callsign);
        var doc = new CoreLogbookDocument
        {
            Id = Guid.NewGuid().ToString("N"),
            QsoDateTimeUtc = Utc(entry.QsoDateTimeUtc ?? DateTime.UtcNow),
            Callsign = call,
            CallsignUpper = call,
            Name = entry.Name,
            FrequencyMhz = entry.FrequencyMhz > 0 ? entry.FrequencyMhz : null,
            Band = entry.Band ?? string.Empty,
            Mode = (entry.Mode ?? string.Empty).ToUpperInvariant(),
            RstSent = entry.RstSent ?? string.Empty,
            RstRcvd = entry.RstRcvd ?? string.Empty,
            Grid = entry.Grid,
            Country = entry.Country,
            Dxcc = entry.Dxcc,
            CqZone = entry.CqZone,
            ItuZone = entry.ItuZone,
            State = entry.State,
            Comment = entry.Comment,
            CreatedUtc = DateTime.UtcNow,
            AdifFields = entry.AdifFields is { Count: > 0 }
                ? new Dictionary<string, string>(entry.AdifFields)
                : null,
        };

        lock (_sync) _docs.Insert(doc);
        _log.LogInformation(
            "logbook: stored {Call} {Band} {Mode} at {Utc:yyyy-MM-dd HH:mm:ss}Z",
            doc.Callsign, doc.Band, doc.Mode, doc.QsoDateTimeUtc);
        return Task.FromResult(ToSnapshot(doc));
    }

    public Task<LogbookPage> GetEntriesAsync(int skip, int take, CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 50;

        lock (_sync)
        {
            var total = _docs.Count();
            var page = _docs.FindAll()
                .OrderByDescending(d => d.QsoDateTimeUtc)
                .ThenByDescending(d => d.CreatedUtc)
                .Skip(skip)
                .Take(take)
                .Select(ToSnapshot)
                .ToList();
            return Task.FromResult(new LogbookPage(page, total));
        }
    }

    public Task<IReadOnlyList<LogbookEntrySnapshot>> GetByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var wanted = IdSet(ids);
        if (wanted.Count == 0)
            return Task.FromResult<IReadOnlyList<LogbookEntrySnapshot>>([]);

        lock (_sync)
        {
            IReadOnlyList<LogbookEntrySnapshot> found = _docs.FindAll()
                .Where(d => wanted.Contains(d.Id))
                .OrderByDescending(d => d.QsoDateTimeUtc)
                .Select(ToSnapshot)
                .ToList();
            return Task.FromResult(found);
        }
    }

    public Task<LogbookWorkedSummary?> GetWorkedSummaryAsync(string callsign, int recentTake, CancellationToken ct = default)
    {
        var call = Normalize(callsign);
        if (call.Length == 0) return Task.FromResult<LogbookWorkedSummary?>(null);
        if (recentTake <= 0) recentTake = 5;

        lock (_sync)
        {
            var qsos = _docs.Find(d => d.CallsignUpper == call)
                .OrderByDescending(d => d.QsoDateTimeUtc)
                .ToList();

            if (qsos.Count == 0)
                return Task.FromResult<LogbookWorkedSummary?>(new LogbookWorkedSummary(
                    call, false, 0, null, null, null, null, null, null, null, null, null, null, null, [], [], []));

            var last = qsos[0];
            var summary = new LogbookWorkedSummary(
                Callsign: call,
                WorkedBefore: true,
                TotalCount: qsos.Count,
                LastWorkedUtc: Utc(last.QsoDateTimeUtc),
                LastBand: last.Band,
                LastMode: last.Mode,
                LastFrequencyMhz: last.FrequencyMhz,
                LastRstSent: last.RstSent,
                LastRstRcvd: last.RstRcvd,
                LastName: last.Name,
                LastGrid: last.Grid,
                LastCountry: last.Country,
                LastState: last.State,
                LastComment: last.Comment,
                Bands: qsos.Select(q => q.Band ?? string.Empty).Where(b => b.Length > 0)
                           .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Modes: qsos.Select(q => q.Mode ?? string.Empty).Where(m => m.Length > 0)
                           .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                RecentQsos: qsos.Take(recentTake).Select(q => new LogbookWorkedRecentQso(
                    QsoDateTimeUtc: Utc(q.QsoDateTimeUtc),
                    Band: q.Band,
                    Mode: q.Mode,
                    FrequencyMhz: q.FrequencyMhz,
                    RstSent: q.RstSent,
                    RstRcvd: q.RstRcvd,
                    Name: q.Name,
                    Grid: q.Grid,
                    Country: q.Country,
                    State: q.State,
                    Comment: q.Comment,
                    QrzLogId: q.QrzLogId)).ToList());

            return Task.FromResult<LogbookWorkedSummary?>(summary);
        }
    }

    public Task<IReadOnlyList<string>> GetDigitalWorkedCallsignsAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            IReadOnlyList<string> calls = _docs.FindAll()
                .Where(d => DigitalModes.Contains(d.Mode ?? string.Empty))
                .Select(d => d.CallsignUpper)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(calls);
        }
    }

    public Task<bool> UpdateQrzUploadStatusAsync(string id, string qrzLogId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var doc = _docs.FindById(id);
            if (doc is null) return Task.FromResult(false);
            doc.QrzLogId = qrzLogId;
            doc.QrzUploadedUtc = DateTime.UtcNow;
            _docs.Update(doc);
            return Task.FromResult(true);
        }
    }

    public Task<int> DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var wanted = IdSet(ids);
        if (wanted.Count == 0) return Task.FromResult(0);

        lock (_sync)
        {
            var deleted = wanted.Count(id => _docs.Delete(id));
            return Task.FromResult(deleted);
        }
    }

    public Task<string> ExportAdifAsync(IEnumerable<string>? ids = null, CancellationToken ct = default)
    {
        List<CoreLogbookDocument> rows;
        var wanted = ids is null ? null : IdSet(ids);
        lock (_sync)
        {
            rows = _docs.FindAll()
                .Where(d => wanted is null || wanted.Contains(d.Id))
                .OrderBy(d => d.QsoDateTimeUtc)
                .ToList();
        }

        return Task.FromResult(BuildAdif(rows));
    }

    public async Task<LogbookExportFileResult> ExportAdifToFileAsync(
        string? directory = null, IEnumerable<string>? ids = null, CancellationToken ct = default)
    {
        var adif = await ExportAdifAsync(ids, ct).ConfigureAwait(false);

        var dir = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : directory.Trim();
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"zeus-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}.adi");
        await File.WriteAllTextAsync(path, adif, new UTF8Encoding(false), ct).ConfigureAwait(false);

        var bytes = new FileInfo(path).Length;
        var count = CountRecords(adif);
        _log.LogInformation("logbook: exported {Count} QSOs to {Path}", count, path);
        return new LogbookExportFileResult(path, count, bytes);
    }

    public Task<LogbookImportResult> ImportAdifAsync(string adifText, CancellationToken ct = default)
    {
        var errors = new List<LogbookImportError>();
        var imported = 0;
        var duplicates = 0;
        var skipped = 0;

        var report = LotwService.ParseAdif(adifText ?? string.Empty, requireEoh: false);

        lock (_sync)
        {
            var existing = _docs.FindAll().ToList();

            for (var i = 0; i < report.Records.Count; i++)
            {
                var record = report.Records[i];
                var number = i + 1;
                try
                {
                    var call = Normalize(record.GetValueOrDefault("CALL"));
                    if (call.Length == 0)
                    {
                        skipped++;
                        errors.Add(new LogbookImportError(number, "record has no CALL"));
                        continue;
                    }

                    var when = ParseQsoTime(record);
                    if (when is null)
                    {
                        skipped++;
                        errors.Add(new LogbookImportError(number, $"{call}: record has no usable QSO_DATE/TIME_ON"));
                        continue;
                    }

                    var freq = ParseDouble(record.GetValueOrDefault("FREQ"));
                    var band = (record.GetValueOrDefault("BAND") ?? string.Empty).Trim();
                    if (band.Length == 0 && freq is > 0)
                        band = BandUtils.FreqToBand((long)Math.Round(freq.Value * 1_000_000)) ?? string.Empty;
                    var mode = (record.GetValueOrDefault("MODE") ?? string.Empty).Trim().ToUpperInvariant();

                    if (existing.Any(d => IsSameQso(d, call, when.Value, band, mode)))
                    {
                        duplicates++;
                        continue;
                    }

                    var doc = new CoreLogbookDocument
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        QsoDateTimeUtc = when.Value,
                        Callsign = call,
                        CallsignUpper = call,
                        Name = Blank(record.GetValueOrDefault("NAME")),
                        FrequencyMhz = freq is > 0 ? freq : null,
                        Band = band,
                        Mode = mode,
                        RstSent = record.GetValueOrDefault("RST_SENT") ?? string.Empty,
                        RstRcvd = record.GetValueOrDefault("RST_RCVD") ?? string.Empty,
                        Grid = Blank(record.GetValueOrDefault("GRIDSQUARE")),
                        Country = Blank(record.GetValueOrDefault("COUNTRY")),
                        Dxcc = ParseInt(record.GetValueOrDefault("DXCC")),
                        CqZone = ParseInt(record.GetValueOrDefault("CQZ")),
                        ItuZone = ParseInt(record.GetValueOrDefault("ITUZ")),
                        State = Blank(record.GetValueOrDefault("STATE")),
                        Comment = Blank(record.GetValueOrDefault("COMMENT")),
                        CreatedUtc = DateTime.UtcNow,
                        QslSent = Blank(record.GetValueOrDefault("QSL_SENT")),
                        QslRcvd = Blank(record.GetValueOrDefault("QSL_RCVD")),
                        QslSentDate = ParseAdifDate(record.GetValueOrDefault("QSLSDATE")),
                        QslRcvdDate = ParseAdifDate(record.GetValueOrDefault("QSLRDATE")),
                        LotwQslSentUtc = ParseAdifDate(record.GetValueOrDefault("LOTW_QSLSDATE")),
                        LotwQslRcvdUtc = ParseAdifDate(record.GetValueOrDefault("LOTW_QSLRDATE")),
                        Rig = Blank(record.GetValueOrDefault("MY_RIG")),
                        Antenna = Blank(record.GetValueOrDefault("MY_ANTENNA")),
                        TxPowerW = ParseDouble(record.GetValueOrDefault("TX_PWR")),
                    };

                    _docs.Insert(doc);
                    existing.Add(doc);
                    imported++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    errors.Add(new LogbookImportError(number, ex.Message));
                }
            }
        }

        _log.LogInformation(
            "logbook: ADIF import — {Imported} added, {Duplicates} duplicates, {Skipped} skipped of {Total}",
            imported, duplicates, skipped, report.Records.Count);

        return Task.FromResult(new LogbookImportResult(
            report.Records.Count, imported, duplicates, skipped, errors));
    }

    public Task<LogbookEntrySnapshot?> UpdateAsync(string id, LogbookEntryUpdate update, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var doc = _docs.FindById(id);
            if (doc is null) return Task.FromResult<LogbookEntrySnapshot?>(null);

            if (update.Name is not null) doc.Name = Blank(update.Name);
            if (update.Grid is not null) doc.Grid = Blank(update.Grid);
            if (update.Country is not null) doc.Country = Blank(update.Country);
            if (update.State is not null) doc.State = Blank(update.State);
            if (update.Comment is not null) doc.Comment = Blank(update.Comment);
            if (update.Tags is not null) doc.Tags = CleanTags(update.Tags);
            if (update.QslSent is not null) doc.QslSent = Blank(update.QslSent);
            if (update.QslRcvd is not null) doc.QslRcvd = Blank(update.QslRcvd);
            if (update.Rig is not null) doc.Rig = Blank(update.Rig);
            if (update.Antenna is not null) doc.Antenna = Blank(update.Antenna);
            if (update.RstSent is not null) doc.RstSent = update.RstSent;
            if (update.RstRcvd is not null) doc.RstRcvd = update.RstRcvd;
            if (update.Mode is not null) doc.Mode = update.Mode.ToUpperInvariant();
            if (update.Band is not null) doc.Band = update.Band;
            if (update.QsoDateTimeUtc is { } when) doc.QsoDateTimeUtc = Utc(when);

            // Nullable value fields: a null update means "leave alone", so the
            // caller clears them through the explicit Clear* flags.
            if (update.ClearQslSentDate) doc.QslSentDate = null;
            else if (update.QslSentDate is { } qsd) doc.QslSentDate = Utc(qsd);
            if (update.ClearQslRcvdDate) doc.QslRcvdDate = null;
            else if (update.QslRcvdDate is { } qrd) doc.QslRcvdDate = Utc(qrd);
            if (update.ClearTxPowerW) doc.TxPowerW = null;
            else if (update.TxPowerW is { } pwr) doc.TxPowerW = pwr;
            if (update.ClearFrequencyMhz) doc.FrequencyMhz = null;
            else if (update.FrequencyMhz is { } mhz) doc.FrequencyMhz = mhz;

            _docs.Update(doc);
            return Task.FromResult<LogbookEntrySnapshot?>(ToSnapshot(doc));
        }
    }

    public Task<int> UpdateQslStatusAsync(IReadOnlyList<LogbookQslStatusUpdate> updates, CancellationToken ct = default)
    {
        if (updates is null || updates.Count == 0) return Task.FromResult(0);

        var changed = 0;
        lock (_sync)
        {
            foreach (var u in updates)
            {
                var doc = _docs.FindById(u.Id);
                if (doc is null) continue;

                if (u.LotwQslRcvdUtc is { } lr) doc.LotwQslRcvdUtc = Utc(lr);
                if (u.LotwQslSentUtc is { } ls) doc.LotwQslSentUtc = Utc(ls);
                if (u.QrzQslRcvdUtc is { } qr) doc.QrzQslRcvdUtc = Utc(qr);
                if (u.QslRcvd is not null) doc.QslRcvd = Blank(u.QslRcvd);
                if (u.QslRcvdDate is { } qd) doc.QslRcvdDate = Utc(qd);

                _docs.Update(doc);
                changed++;
            }
        }
        return Task.FromResult(changed);
    }

    public Task<IReadOnlyList<string>> GetAllTagsAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            IReadOnlyList<string> tags = _docs.FindAll()
                .Where(d => d.Tags is { Count: > 0 })
                .SelectMany(d => d.Tags!)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.FromResult(tags);
        }
    }

    public void Dispose() => _dbLease.Dispose();

    // ---- ADIF ----------------------------------------------------------

    internal static string BuildAdif(IReadOnlyList<CoreLogbookDocument> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ADIF Export from Zeus");
        Field(sb, "ADIF_VER", "3.1.4");
        Field(sb, "PROGRAMID", "Zeus");
        Field(sb, "CREATED_TIMESTAMP", DateTime.UtcNow.ToString("yyyyMMdd HHmmss", CultureInfo.InvariantCulture));
        sb.AppendLine();
        sb.AppendLine("<EOH>");

        foreach (var d in rows)
        {
            var when = Utc(d.QsoDateTimeUtc);
            Field(sb, "CALL", d.Callsign);
            Field(sb, "QSO_DATE", when.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Field(sb, "TIME_ON", when.ToString("HHmmss", CultureInfo.InvariantCulture));
            if (d.FrequencyMhz is { } mhz)
                Field(sb, "FREQ", mhz.ToString("F6", CultureInfo.InvariantCulture));
            Field(sb, "BAND", d.Band);
            Field(sb, "MODE", d.Mode);
            Field(sb, "RST_SENT", d.RstSent);
            Field(sb, "RST_RCVD", d.RstRcvd);
            Field(sb, "NAME", d.Name);
            Field(sb, "GRIDSQUARE", d.Grid);
            Field(sb, "COUNTRY", d.Country);
            if (d.Dxcc is { } dxcc) Field(sb, "DXCC", dxcc.ToString(CultureInfo.InvariantCulture));
            if (d.CqZone is { } cq) Field(sb, "CQZ", cq.ToString(CultureInfo.InvariantCulture));
            if (d.ItuZone is { } itu) Field(sb, "ITUZ", itu.ToString(CultureInfo.InvariantCulture));
            Field(sb, "STATE", d.State);
            Field(sb, "COMMENT", d.Comment);
            Field(sb, "QSL_SENT", d.QslSent);
            Field(sb, "QSL_RCVD", d.QslRcvd);
            if (d.QslSentDate is { } qsd) Field(sb, "QSLSDATE", Utc(qsd).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            if (d.QslRcvdDate is { } qrd) Field(sb, "QSLRDATE", Utc(qrd).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            if (d.LotwQslSentUtc is { } ls) Field(sb, "LOTW_QSLSDATE", Utc(ls).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            if (d.LotwQslRcvdUtc is { } lr) Field(sb, "LOTW_QSLRDATE", Utc(lr).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Field(sb, "MY_RIG", d.Rig);
            Field(sb, "MY_ANTENNA", d.Antenna);
            if (d.TxPowerW is { } pwr) Field(sb, "TX_PWR", pwr.ToString("0.###", CultureInfo.InvariantCulture));
            if (d.Tags is { Count: > 0 }) Field(sb, "APP_ZEUS_TAGS", string.Join(",", d.Tags));
            sb.AppendLine();
            sb.AppendLine("<EOR>");
        }

        return sb.ToString();
    }

    // ADIF lengths are UTF-8 OCTET counts, not UTF-16 code units — an accented
    // name or QTH counted with value.Length truncates the field for whoever
    // reads the file back.
    private static void Field(StringBuilder sb, string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append('<').Append(name).Append(':').Append(Encoding.UTF8.GetByteCount(value)).Append('>').Append(value).Append(' ');
    }

    private static int CountRecords(string adif)
    {
        var count = 0;
        var i = 0;
        while ((i = adif.IndexOf("<EOR>", i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            i += 5;
        }
        return count;
    }

    // ---- helpers -------------------------------------------------------

    /// <summary>
    /// ADIF dupe rule as the rest of the ecosystem applies it: same callsign,
    /// band and mode within a couple of minutes. TIME_ON is often logged to the
    /// minute (or dropped to whole minutes by the exporting program), so an
    /// exact-timestamp match would re-import half a file.
    /// </summary>
    private static bool IsSameQso(CoreLogbookDocument d, string call, DateTime when, string band, string mode) =>
        string.Equals(d.CallsignUpper, call, StringComparison.Ordinal)
        && string.Equals(d.Band ?? string.Empty, band, StringComparison.OrdinalIgnoreCase)
        && string.Equals(d.Mode ?? string.Empty, mode, StringComparison.OrdinalIgnoreCase)
        && Math.Abs((Utc(d.QsoDateTimeUtc) - when).TotalMinutes) <= 2.0;

    private static HashSet<string> IdSet(IEnumerable<string>? ids) =>
        new((ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()), StringComparer.Ordinal);

    private static string Normalize(string? callsign) =>
        (callsign ?? string.Empty).Trim().ToUpperInvariant();

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> CleanTags(IReadOnlyList<string> tags) =>
        tags.Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // LiteDB hands DateTimes back as Local (or Unspecified) on some platforms;
    // everything downstream — QRZ, LoTW, ADIF — is UTC-only, so pin the kind on
    // the way out rather than shipping the operator's wall clock.
    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    private static DateTime? ParseQsoTime(IReadOnlyDictionary<string, string> record)
    {
        var date = (record.GetValueOrDefault("QSO_DATE") ?? string.Empty).Trim();
        if (date.Length < 8) return null;
        var time = (record.GetValueOrDefault("TIME_ON") ?? record.GetValueOrDefault("TIME_OFF") ?? "0000").Trim();
        if (time.Length == 4) time += "00";
        if (time.Length != 6) time = "000000";

        return DateTime.TryParseExact(
            date + time, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }

    private static DateTime? ParseAdifDate(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length != 8) return null;
        return DateTime.TryParseExact(
            v, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }

    private static double? ParseDouble(string? value) =>
        double.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : null;

    private static int? ParseInt(string? value) =>
        int.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i : null;

    private static LogbookEntrySnapshot ToSnapshot(CoreLogbookDocument d) => new(
        Id: d.Id,
        QsoDateTimeUtc: Utc(d.QsoDateTimeUtc),
        Callsign: d.Callsign,
        Name: d.Name,
        FrequencyMhz: d.FrequencyMhz,
        Band: d.Band ?? string.Empty,
        Mode: d.Mode ?? string.Empty,
        RstSent: d.RstSent ?? string.Empty,
        RstRcvd: d.RstRcvd ?? string.Empty,
        Grid: d.Grid,
        Country: d.Country,
        Dxcc: d.Dxcc,
        CqZone: d.CqZone,
        ItuZone: d.ItuZone,
        State: d.State,
        Comment: d.Comment,
        CreatedUtc: Utc(d.CreatedUtc),
        QrzLogId: d.QrzLogId,
        QrzUploadedUtc: Utc(d.QrzUploadedUtc),
        AdifFields: d.AdifFields is { Count: > 0 } ? new Dictionary<string, string>(d.AdifFields) : null)
    {
        Tags = d.Tags is { Count: > 0 } ? d.Tags.ToList() : null,
        QslSent = d.QslSent,
        QslRcvd = d.QslRcvd,
        QslSentDate = Utc(d.QslSentDate),
        QslRcvdDate = Utc(d.QslRcvdDate),
        LotwQslSentUtc = Utc(d.LotwQslSentUtc),
        LotwQslRcvdUtc = Utc(d.LotwQslRcvdUtc),
        QrzQslRcvdUtc = Utc(d.QrzQslRcvdUtc),
        Rig = d.Rig,
        Antenna = d.Antenna,
        TxPowerW = d.TxPowerW,
    };
}

/// <summary>
/// Hands the built-in logbook to <see cref="LogbookPluginBridge"/> once the
/// host is up. Registered AFTER the bridge so any installed logbook plugin has
/// already claimed the seam and keeps it.
/// </summary>
public sealed class CoreLogbookInstaller : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly LogbookPluginBridge _bridge;
    private readonly CoreLogbook _logbook;

    public CoreLogbookInstaller(LogbookPluginBridge bridge, CoreLogbook logbook)
    {
        _bridge = bridge;
        _logbook = logbook;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _bridge.AttachFallback(_logbook);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>LiteDB document for <see cref="CoreLogbook"/>.</summary>
public sealed class CoreLogbookDocument
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public DateTime QsoDateTimeUtc { get; set; }
    public string Callsign { get; set; } = string.Empty;
    public string CallsignUpper { get; set; } = string.Empty;
    public string? Name { get; set; }
    public double? FrequencyMhz { get; set; }
    public string? Band { get; set; }
    public string? Mode { get; set; }
    public string? RstSent { get; set; }
    public string? RstRcvd { get; set; }
    public string? Grid { get; set; }
    public string? Country { get; set; }
    public int? Dxcc { get; set; }
    public int? CqZone { get; set; }
    public int? ItuZone { get; set; }
    public string? State { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string? QrzLogId { get; set; }
    public DateTime? QrzUploadedUtc { get; set; }
    public List<string>? Tags { get; set; }
    public string? QslSent { get; set; }
    public string? QslRcvd { get; set; }
    public DateTime? QslSentDate { get; set; }
    public DateTime? QslRcvdDate { get; set; }
    public DateTime? LotwQslSentUtc { get; set; }
    public DateTime? LotwQslRcvdUtc { get; set; }
    public DateTime? QrzQslRcvdUtc { get; set; }
    public string? Rig { get; set; }
    public string? Antenna { get; set; }
    public double? TxPowerW { get; set; }
    public Dictionary<string, string>? AdifFields { get; set; }
}
