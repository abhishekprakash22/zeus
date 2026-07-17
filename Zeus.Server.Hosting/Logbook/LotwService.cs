// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using LiteDB;
using Zeus.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Server;

public sealed record LotwStationDefaults(
    string? Rig = null,
    string? Antenna = null,
    double? TxPowerW = null);

public sealed record LotwStatus(
    bool Configured,
    string? StationLocation,
    DateTime? LastSyncUtc,
    DateTime? LastQslSince,
    string? LastResult,
    string? TqslPath,
    LotwStationDefaults StationDefaults);

public sealed record LotwCredentialsRequest(
    string? Username,
    string? Password,
    string? StationLocation);

public sealed record LotwSettingsRequest(
    bool? AutoSync = null,
    string? StationLocation = null,
    LotwStationDefaults? StationDefaults = null);

public sealed record LotwConfirmationCounts(
    int Fetched,
    int Matched,
    int Unmatched);

public sealed record LotwSyncResponse(
    LotwConfirmationCounts Lotw,
    LotwConfirmationCounts Qrz,
    DateTime? LastQslSince,
    DateTime? LastSyncUtc,
    string Message);

public sealed record LotwUploadRequest(IEnumerable<string>? LogEntryIds = null);

public sealed record LotwUploadResponse(
    bool Success,
    string Message,
    int UploadedCount,
    string? ExportedPath,
    string? TqslPath,
    int? ExitCode,
    string? ErrorTail,
    bool TqslMissing);

public sealed record LotwProcessResult(int ExitCode, string StandardOutput, string StandardError);

file static class LotwTempFiles
{
    // Best-effort cleanup of the signed-and-uploaded ADIF; the file is kept on
    // failure and when TQSL is missing so the operator can sign it manually.
    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public interface ILotwProcessRunner
{
    Task<LotwProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct);
}

public interface ILotwTqslLocator
{
    string? Locate();
}

public sealed class LotwTqslLocator : ILotwTqslLocator
{
    public string? Locate() => LotwService.LocateTqsl();
}

public sealed class LotwProcessRunner : ILotwProcessRunner
{
    public async Task<LotwProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new LotwProcessResult(-1, "", "TQSL timed out");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new LotwProcessResult(process.ExitCode, stdout, stderr);
    }
}

public sealed class LotwSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<LotwSettingsEntry> _entries;
    private readonly object _sync = new();

    public LotwSettingsStore(ILogger<LotwSettingsStore> log, string? dbPathOverride = null)
    {
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _entries = _db.GetCollection<LotwSettingsEntry>("lotw_settings");
        log.LogInformation("LotwSettingsStore initialized at {Path}", dbPath);
    }

    public LotwSettingsEntry Get()
    {
        lock (_sync)
            return _entries.FindAll().FirstOrDefault() ?? new LotwSettingsEntry();
    }

    public void Save(Action<LotwSettingsEntry> edit)
    {
        lock (_sync)
        {
            var entry = _entries.FindAll().FirstOrDefault() ?? new LotwSettingsEntry();
            edit(entry);
            entry.UpdatedUtc = DateTime.UtcNow;
            if (entry.Id == 0) _entries.Insert(entry);
            else _entries.Update(entry);
        }
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class LotwSettingsEntry
{
    public int Id { get; set; }
    public string? StationLocation { get; set; }
    public DateTime? LastQslSince { get; set; }
    public DateTime? LastSyncUtc { get; set; }
    public string? LastResult { get; set; }
    public bool AutoSync { get; set; }
    public string? Rig { get; set; }
    public string? Antenna { get; set; }
    public double? TxPowerW { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class LotwBadRequestException(string message) : Exception(message);

public sealed class LotwAuthException(string message) : Exception(message);

public sealed class LotwService
{
    internal const string HttpClientName = "Lotw";
    private const string CredentialServiceName = "lotw";
    private const int PageSize = 500;
    private static readonly TimeSpan SyncWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TqslTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly CredentialStore _credentials;
    private readonly LotwSettingsStore _settings;
    private readonly LogbookPluginBridge _logbook;
    private readonly QrzService _qrz;
    private readonly ILotwProcessRunner _processRunner;
    private readonly ILotwTqslLocator _tqslLocator;
    private readonly ILogger<LotwService> _log;

    public LotwService(
        IHttpClientFactory httpClientFactory,
        CredentialStore credentials,
        LotwSettingsStore settings,
        LogbookPluginBridge logbook,
        QrzService qrz,
        ILotwProcessRunner processRunner,
        ILotwTqslLocator tqslLocator,
        ILogger<LotwService> log)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _credentials = credentials;
        _settings = settings;
        _logbook = logbook;
        _qrz = qrz;
        _processRunner = processRunner;
        _tqslLocator = tqslLocator;
        _log = log;
    }

    public async Task<LotwStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var cfg = _settings.Get();
        var cred = await _credentials.GetAsync(CredentialServiceName, ct).ConfigureAwait(false);
        return new LotwStatus(
            Configured: !string.IsNullOrWhiteSpace(cred?.Username) && !string.IsNullOrWhiteSpace(cred.Password),
            StationLocation: NullIfWhite(cfg.StationLocation),
            LastSyncUtc: AsUtc(cfg.LastSyncUtc),
            LastQslSince: AsUtc(cfg.LastQslSince),
            LastResult: NullIfWhite(cfg.LastResult),
            TqslPath: _tqslLocator.Locate(),
            StationDefaults: GetStationDefaults());
    }

    public LotwStationDefaults GetStationDefaults()
    {
        var cfg = _settings.Get();
        return new LotwStationDefaults(
            Rig: NullIfWhite(cfg.Rig),
            Antenna: NullIfWhite(cfg.Antenna),
            TxPowerW: cfg.TxPowerW > 0 ? cfg.TxPowerW : null);
    }

    public async Task<LotwStatus> SetCredentialsAsync(LotwCredentialsRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Username))
        {
            await _credentials.DeleteAsync(CredentialServiceName, ct).ConfigureAwait(false);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(req.Password))
                throw new LotwBadRequestException("LoTW password required");
            await _credentials.SetAsync(
                CredentialServiceName,
                req.Username.Trim(),
                req.Password,
                ct).ConfigureAwait(false);
        }

        _settings.Save(e => e.StationLocation = NullIfWhite(req.StationLocation));
        return await GetStatusAsync(ct).ConfigureAwait(false);
    }

    public async Task<LotwStatus> SetSettingsAsync(LotwSettingsRequest req, CancellationToken ct = default)
    {
        _settings.Save(e =>
        {
            if (req.AutoSync.HasValue) e.AutoSync = req.AutoSync.Value;
            if (req.StationLocation is not null)
                e.StationLocation = NullIfWhite(req.StationLocation);
            if (req.StationDefaults is { } d)
            {
                e.Rig = NullIfWhite(d.Rig);
                e.Antenna = NullIfWhite(d.Antenna);
                e.TxPowerW = d.TxPowerW is > 0 ? d.TxPowerW : null;
            }
        });
        return await GetStatusAsync(ct).ConfigureAwait(false);
    }

    public LogbookEntryUpdate BuildDefaultUpdate()
    {
        var d = GetStationDefaults();
        return new LogbookEntryUpdate(
            Rig: d.Rig,
            Antenna: d.Antenna,
            TxPowerW: d.TxPowerW);
    }

    public bool HasStationDefaults()
    {
        var d = GetStationDefaults();
        return !string.IsNullOrWhiteSpace(d.Rig)
            || !string.IsNullOrWhiteSpace(d.Antenna)
            || d.TxPowerW.HasValue;
    }

    public async Task<LotwSyncResponse> SyncAsync(CancellationToken ct = default)
    {
        var plugin = RequireV2();
        var entries = (await LoadAllEntriesAsync(plugin, ct).ConfigureAwait(false))
            .Select(LogbookPluginMappings.ToContract)
            .ToList();

        var lotw = await SyncLotwAsync(plugin, entries, ct).ConfigureAwait(false);
        var qrz = await SyncQrzAsync(plugin, entries, ct).ConfigureAwait(false);

        var cfg = _settings.Get();
        var now = DateTime.UtcNow;
        var message = $"LoTW {lotw.Matched} · QRZ {qrz.Matched} confirmations matched";
        _settings.Save(e =>
        {
            e.LastSyncUtc = now;
            e.LastResult = message;
        });

        cfg = _settings.Get();
        return new LotwSyncResponse(
            Lotw: lotw,
            Qrz: qrz,
            LastQslSince: AsUtc(cfg.LastQslSince),
            LastSyncUtc: AsUtc(cfg.LastSyncUtc),
            Message: message);
    }

    public async Task<LotwUploadResponse> UploadAsync(LotwUploadRequest req, CancellationToken ct = default)
    {
        var plugin = RequireV2();
        var stationLocation = NullIfWhite(_settings.Get().StationLocation);
        if (stationLocation is null)
            throw new LotwBadRequestException("Set a TQSL Station Location in Logbook Settings → LoTW before uploading.");

        var selectedIds = req.LogEntryIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var ids = selectedIds is { Count: > 0 }
            ? selectedIds
            : (await LoadAllEntriesAsync(plugin, ct).ConfigureAwait(false))
                .Where(e => e.LotwQslSentUtc is null)
                .Select(e => e.Id)
                .ToList();

        if (ids.Count == 0)
            return new LotwUploadResponse(true, "No QSOs pending LoTW upload.", 0, null, null, null, null, false);

        var adif = await plugin.ExportAdifAsync(ids, ct).ConfigureAwait(false);
        var exportPath = Path.Combine(
            Path.GetTempPath(),
            $"zeus-lotw-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.adi");
        await File.WriteAllTextAsync(exportPath, adif, Encoding.UTF8, ct).ConfigureAwait(false);

        var tqslPath = _tqslLocator.Locate();
        if (string.IsNullOrWhiteSpace(tqslPath))
        {
            var message = $"TQSL not found — exported ADIF to {exportPath}";
            return new LotwUploadResponse(false, message, 0, exportPath, null, null, null, TqslMissing: true);
        }

        var result = await _processRunner.RunAsync(
            tqslPath,
            ["-d", "-u", "-a", "compliant", "-x", "-l", stationLocation, exportPath],
            TqslTimeout,
            ct).ConfigureAwait(false);

        if (result.ExitCode is 0 or 9)
        {
            var sent = DateTime.UtcNow;
            var updates = ids.Select(id => new LogbookQslStatusUpdate(
                Id: id,
                LotwQslRcvdUtc: null,
                LotwQslSentUtc: sent,
                QrzQslRcvdUtc: null,
                QslRcvd: null,
                QslRcvdDate: null)).ToList();
            var marked = updates.Count == 0
                ? 0
                : await plugin.UpdateQslStatusAsync(updates, ct).ConfigureAwait(false);
            var msg = result.ExitCode == 9
                ? "TQSL reported all QSOs as duplicates; LoTW sent status was updated."
                : "TQSL upload completed.";
            LotwTempFiles.TryDelete(exportPath);
            return new LotwUploadResponse(true, msg, marked, null, tqslPath, result.ExitCode, Tail(result.StandardError), false);
        }

        var failure = result.ExitCode switch
        {
            2 => "LoTW rejected the upload.",
            11 => "TQSL could not connect to LoTW.",
            -1 => "TQSL timed out.",
            _ => "TQSL upload failed.",
        };
        return new LotwUploadResponse(false, $"{failure} Exit code {result.ExitCode}.", 0, exportPath, tqslPath, result.ExitCode, Tail(result.StandardError), false);
    }

    private async Task<LotwConfirmationCounts> SyncLotwAsync(
        ILogbookPluginV2 plugin,
        IReadOnlyList<LogEntry> localEntries,
        CancellationToken ct)
    {
        var cred = await _credentials.GetAsync(CredentialServiceName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(cred?.Username) || string.IsNullOrWhiteSpace(cred.Password))
            return new LotwConfirmationCounts(0, 0, 0);

        var cfg = _settings.Get();
        var url = BuildLotwReportUrl(cred.Username, cred.Password, AsUtc(cfg.LastQslSince));
        using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var adif = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!adif.Contains("<eoh", StringComparison.OrdinalIgnoreCase))
            throw new LotwAuthException("LoTW login failed; check the username and password in Logbook Settings → LoTW.");

        var report = ParseAdif(adif, requireEoh: true);
        var matches = BuildQslUpdates(report.Records, localEntries, network: "lotw");
        var matched = matches.Count == 0
            ? 0
            : await plugin.UpdateQslStatusAsync(matches.Select(m => m.Update).ToList(), ct).ConfigureAwait(false);
        var fetched = report.Records.Count;
        var nextSince = ParseAdifDateTime(report.Header.GetValueOrDefault("APP_LOTW_LASTQSL"))
            ?? matches.Select(m => m.QslDate).Where(d => d.HasValue).MaxOrNull();

        if (nextSince.HasValue)
            _settings.Save(e => e.LastQslSince = nextSince.Value);

        return new LotwConfirmationCounts(fetched, matched, Math.Max(0, fetched - matched));
    }

    private async Task<LotwConfirmationCounts> SyncQrzAsync(
        ILogbookPluginV2 plugin,
        IReadOnlyList<LogEntry> localEntries,
        CancellationToken ct)
    {
        var key = await _qrz.GetLogbookApiKeyAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
            return new LotwConfirmationCounts(0, 0, 0);

        var cfg = _settings.Get();
        var since = AsUtc(cfg.LastSyncUtc);
        var afterLogId = 0L;
        var fetched = 0;
        var updates = new List<MatchedQslUpdate>();

        // Bounded only as a runaway guard: 200 pages × 250 records covers a
        // 50k-QSO initial sync; incremental MODSINCE syncs stay tiny.
        for (var page = 0; page < 200; page++)
        {
            var optionParts = new List<string> { "STATUS:CONFIRMED" };
            if (since.HasValue)
                optionParts.Add($"MODSINCE:{since.Value:yyyy-MM-dd}");
            optionParts.Add("MAX:250");
            optionParts.Add($"AFTERLOGID:{afterLogId}");
            optionParts.Add("TYPE:ADIF");

            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("KEY", key),
                new KeyValuePair<string, string>("ACTION", "FETCH"),
                new KeyValuePair<string, string>("OPTION", string.Join(",", optionParts)),
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://logbook.qrz.com/api")
            {
                Content = content,
            };
            req.Headers.UserAgent.ParseAdd(ZeusUserAgent());

            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var form = ParseForm(text);
            if (form.TryGetValue("RESULT", out var result)
                && (result.Equals("AUTH", StringComparison.OrdinalIgnoreCase)
                    || result.Equals("FAIL", StringComparison.OrdinalIgnoreCase)))
            {
                _log.LogWarning("QRZ confirmation fetch failed: {Response}", text);
                break;
            }

            var adif = form.GetValueOrDefault("ADIF") ?? "";
            var report = ParseAdif(adif, requireEoh: false);
            fetched += report.Records.Count;
            updates.AddRange(BuildQslUpdates(report.Records, localEntries, network: "qrz"));

            var logIds = ParseLogIds(form.GetValueOrDefault("LOGIDS"));
            if (logIds.Count > 0)
                afterLogId = logIds.Max() + 1;

            var count = ParseInt(form.GetValueOrDefault("COUNT")) ?? report.Records.Count;
            if (count < 250 || logIds.Count == 0)
                break;
        }

        var deduped = updates
            .GroupBy(m => m.Update.Id, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(x => x.QslDate).First())
            .ToList();
        var matched = deduped.Count == 0
            ? 0
            : await plugin.UpdateQslStatusAsync(deduped.Select(m => m.Update).ToList(), ct).ConfigureAwait(false);
        return new LotwConfirmationCounts(fetched, matched, Math.Max(0, fetched - matched));
    }

    private static List<MatchedQslUpdate> BuildQslUpdates(
        IReadOnlyList<IReadOnlyDictionary<string, string>> records,
        IReadOnlyList<LogEntry> localEntries,
        string network)
    {
        var updates = new List<MatchedQslUpdate>();
        foreach (var record in records)
        {
            var match = FindMatch(record, localEntries);
            if (match is null) continue;

            var qslDate = ParseAdifDate(record.GetValueOrDefault("QSLRDATE"))
                ?? ParseAdifDateTime(record.GetValueOrDefault("APP_LOTW_LASTQSL"))
                ?? ParseAdifDateTime(record.GetValueOrDefault("APP_LOTW_QSO_TIMESTAMP"))
                ?? DateTime.UtcNow.Date;
            var qslRcvd = NullIfWhite(record.GetValueOrDefault("QSL_RCVD")) ?? "Y";

            var update = network.Equals("qrz", StringComparison.OrdinalIgnoreCase)
                ? new LogbookQslStatusUpdate(match.Id, null, null, qslDate, qslRcvd, qslDate.Date)
                : new LogbookQslStatusUpdate(match.Id, qslDate, null, null, qslRcvd, qslDate.Date);
            updates.Add(new MatchedQslUpdate(update, qslDate));
        }

        return updates;
    }

    private static LogEntry? FindMatch(
        IReadOnlyDictionary<string, string> record,
        IReadOnlyList<LogEntry> localEntries)
    {
        var call = Normalize(record.GetValueOrDefault("CALL"));
        var band = Normalize(record.GetValueOrDefault("BAND"));
        var remoteMode = Normalize(record.GetValueOrDefault("MODE"));
        var remoteGroup = Normalize(record.GetValueOrDefault("APP_LOTW_MODEGROUP"));
        if (string.IsNullOrWhiteSpace(remoteGroup))
            remoteGroup = ModeGroup(remoteMode);
        var remoteTime = ParseQsoTime(record);
        if (string.IsNullOrWhiteSpace(call) || string.IsNullOrWhiteSpace(band) || remoteTime is null)
            return null;

        return localEntries
            .Where(e => Normalize(e.Callsign) == call)
            .Where(e => Normalize(e.Band) == band)
            .Where(e => ModeMatches(remoteMode, remoteGroup, e.Mode))
            .Where(e => (e.QsoDateTimeUtc.ToUniversalTime() - remoteTime.Value).Duration() <= SyncWindow)
            .OrderBy(e => (e.QsoDateTimeUtc.ToUniversalTime() - remoteTime.Value).Duration())
            .FirstOrDefault();
    }

    private static bool ModeMatches(string remoteMode, string remoteGroup, string localMode)
    {
        var local = Normalize(localMode);
        if (!string.IsNullOrWhiteSpace(remoteMode) && local == remoteMode)
            return true;
        return !string.IsNullOrWhiteSpace(remoteGroup) && ModeGroup(local) == remoteGroup;
    }

    private static string ModeGroup(string? mode)
    {
        var m = Normalize(mode);
        if (m is "CW" or "CWR" or "PCW") return "CW";
        if (m is "AM" or "FM" or "SSB" or "USB" or "LSB" or "PHONE" or "DIGITALVOICE" or "DV")
            return "PHONE";
        return string.IsNullOrWhiteSpace(m) ? "" : "DATA";
    }

    private async Task<IReadOnlyList<LogbookEntrySnapshot>> LoadAllEntriesAsync(
        ILogbookPlugin plugin,
        CancellationToken ct)
    {
        var entries = new List<LogbookEntrySnapshot>();
        for (var skip = 0; ; skip += PageSize)
        {
            var page = await plugin.GetEntriesAsync(skip, PageSize, ct).ConfigureAwait(false);
            entries.AddRange(page.Entries);
            if (page.Entries.Count < PageSize || entries.Count >= page.TotalCount)
                return entries;
        }
    }

    private ILogbookPluginV2 RequireV2()
    {
        var plugin = _logbook.Current;
        if (plugin is null)
            throw new InvalidOperationException(LogbookPluginMappings.MissingPluginError);
        if (plugin is not ILogbookPluginV2 v2)
            throw new NotSupportedException(LogbookPluginMappings.EditingUnsupportedError);
        return v2;
    }

    internal static string? LocateTqsl()
    {
        foreach (var candidate in TqslCandidates())
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string> TqslCandidates()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Path.Combine(dir, "tqsl");
            yield return Path.Combine(dir, "tqsl.exe");
        }

        yield return "/Applications/TrustedQSL/tqsl.app/Contents/MacOS/tqsl";
        yield return @"C:\Program Files (x86)\TrustedQSL\tqsl.exe";
        yield return @"C:\Program Files\TrustedQSL\tqsl.exe";
        yield return "/usr/bin/tqsl";
        yield return "/usr/local/bin/tqsl";
    }

    private static string BuildLotwReportUrl(string username, string password, DateTime? qslSince)
    {
        var sb = new StringBuilder("https://lotw.arrl.org/lotwuser/lotwreport.adi");
        sb.Append("?login=").Append(Uri.EscapeDataString(username));
        sb.Append("&password=").Append(Uri.EscapeDataString(password));
        sb.Append("&qso_query=1&qso_qsl=yes&qso_qsldetail=yes");
        if (qslSince.HasValue)
            sb.Append("&qso_qslsince=").Append(Uri.EscapeDataString(qslSince.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        return sb.ToString();
    }

    internal static AdifReport ParseAdif(string adif, bool requireEoh)
    {
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var records = new List<IReadOnlyDictionary<string, string>>();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inHeader = requireEoh || adif.Contains("<eoh", StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < adif.Length;)
        {
            var open = adif.IndexOf('<', i);
            if (open < 0) break;
            var close = adif.IndexOf('>', open + 1);
            if (close < 0) break;

            var tag = adif.Substring(open + 1, close - open - 1);
            var parts = tag.Split(':', 3);
            var name = parts[0].Trim().ToUpperInvariant();
            i = close + 1;

            if (name == "EOH")
            {
                inHeader = false;
                current.Clear();
                continue;
            }
            if (name == "EOR")
            {
                if (current.Count > 0)
                    records.Add(new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase));
                current.Clear();
                continue;
            }
            if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var len))
                continue;

            var value = i + len <= adif.Length ? adif.Substring(i, len) : adif[i..];
            i += Math.Min(len, adif.Length - i);
            if (inHeader)
                header[name] = value.Trim();
            else
                current[name] = value.Trim();
        }

        if (!inHeader && current.Count > 0)
            records.Add(current);
        return new AdifReport(header, records);
    }

    private static DateTime? ParseQsoTime(IReadOnlyDictionary<string, string> record)
    {
        var stamp = ParseAdifDateTime(record.GetValueOrDefault("APP_LOTW_QSO_TIMESTAMP"));
        if (stamp.HasValue) return stamp;

        var date = record.GetValueOrDefault("QSO_DATE");
        var time = record.GetValueOrDefault("TIME_ON");
        if (string.IsNullOrWhiteSpace(date) || string.IsNullOrWhiteSpace(time))
            return null;
        var t = time.Trim().PadRight(6, '0');
        return DateTime.TryParseExact(
            date.Trim() + t,
            "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static DateTime? ParseAdifDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParseExact(
            value.Trim(),
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc)
            : null;
    }

    private static DateTime? ParseAdifDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (DateTime.TryParse(
            v,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed))
            return parsed;
        if (DateTime.TryParseExact(
            v,
            ["yyyy-MM-dd HH:mm:ss", "yyyyMMdd HHmmss", "yyyyMMddHHmmss"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed))
            return parsed;
        return ParseAdifDate(v);
    }

    private static Dictionary<string, string> ParseForm(string text) =>
        text.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => WebUtility.UrlDecode(p[1]), StringComparer.OrdinalIgnoreCase);

    private static List<long> ParseLogIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (long?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string Normalize(string? value) => (value ?? "").Trim().ToUpperInvariant();

    private static string? NullIfWhite(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime? AsUtc(DateTime? value)
    {
        if (!value.HasValue) return null;
        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
        };
    }

    private static string Tail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var trimmed = value.Trim();
        return trimmed.Length <= 1200 ? trimmed : trimmed[^1200..];
    }

    private static string ZeusUserAgent()
    {
        var version = typeof(ZeusEndpoints).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        version = string.IsNullOrWhiteSpace(version) ? "1.0" : version.Split('+')[0];
        var agent = $"Zeus/{version}";
        return agent.Length <= 128 ? agent : agent[..128];
    }

    private sealed record MatchedQslUpdate(LogbookQslStatusUpdate Update, DateTime? QslDate);
}

public sealed record AdifReport(
    IReadOnlyDictionary<string, string> Header,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Records);

internal static class LotwEnumerableExtensions
{
    public static DateTime? MaxOrNull(this IEnumerable<DateTime?> values)
    {
        var filtered = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return filtered.Count == 0 ? null : filtered.Max();
    }
}
