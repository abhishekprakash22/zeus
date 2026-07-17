// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class LotwServiceTests
{
    [Fact]
    public async Task CredentialsRoundTripUsesPrefsStore()
    {
        using var h = new Harness(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var saved = await h.Service.SetCredentialsAsync(new LotwCredentialsRequest("kb2uka", "secret", "Home"));
        var status = await h.Service.GetStatusAsync();
        var cleared = await h.Service.SetCredentialsAsync(new LotwCredentialsRequest("", null, null));

        Assert.True(saved.Configured);
        Assert.True(status.Configured);
        Assert.Equal("Home", status.StationLocation);
        Assert.False(cleared.Configured);
        Assert.Null(cleared.StationLocation);
    }

    [Fact]
    public async Task SyncLotwMatchesBoundarySubmodeBandCaseAndTracksUnmatched()
    {
        var adif = Adif(
            header: [Field("APP_LoTW_LASTQSL", "2026-05-04 00:00:00")],
            records:
            [
                Qsl("K1ABC", "20m", "SSB", "PHONE", "20260501", "120000", "20260502"),
                Qsl("K2DIG", "40M", "MFSK", "DATA", "20260501", "130500", "20260503"),
                Qsl("K3CW", "30M", "CW", "CW", "20260501", "143000", "20260503"),
                Qsl("K9MISS", "20M", "FT8", "DATA", "20260501", "120000", "20260503"),
            ]);
        using var h = new Harness(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("qso_qsldetail=yes", req.RequestUri!.Query, StringComparison.Ordinal);
            return Text(adif);
        });
        h.Plugin.Entries.Add(Entry("qso-1", "K1ABC", "20M", "USB", DateTimeUtc(2026, 5, 1, 12, 0)));
        h.Plugin.Entries.Add(Entry("qso-2", "K2DIG", "40m", "FT8", DateTimeUtc(2026, 5, 1, 13, 30)));
        h.Plugin.Entries.Add(Entry("qso-3", "K3CW", "30m", "CW", DateTimeUtc(2026, 5, 1, 14, 0)));
        await h.Service.SetCredentialsAsync(new LotwCredentialsRequest("kb2uka", "secret", "Home"));

        var result = await h.Service.SyncAsync();
        var status = await h.Service.GetStatusAsync();

        Assert.Equal(4, result.Lotw.Fetched);
        Assert.Equal(3, result.Lotw.Matched);
        Assert.Equal(1, result.Lotw.Unmatched);
        Assert.Equal(0, result.Qrz.Fetched);
        Assert.Equal(DateTimeUtc(2026, 5, 4, 0, 0), status.LastQslSince);
        Assert.Equal(["qso-1", "qso-2", "qso-3"], h.Plugin.QslUpdates.Select(u => u.Id).ToArray());
        Assert.All(h.Plugin.QslUpdates, u => Assert.Equal("Y", u.QslRcvd));
        Assert.Equal(DateTimeUtc(2026, 5, 2, 0, 0), h.Plugin.QslUpdates[0].LotwQslRcvdUtc);
    }

    [Fact]
    public async Task SyncLotwRejectsHtmlLoginFailure()
    {
        using var h = new Harness(_ => Text("<html>bad login</html>"));
        h.Plugin.Entries.Add(Entry("qso-1", "K1ABC", "20M", "FT8", DateTimeUtc(2026, 5, 1, 12, 0)));
        await h.Service.SetCredentialsAsync(new LotwCredentialsRequest("kb2uka", "bad", "Home"));

        var ex = await Assert.ThrowsAsync<LotwAuthException>(() => h.Service.SyncAsync());

        Assert.Contains("LoTW login failed", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Plugin.QslUpdates);
    }

    [Fact]
    public async Task SyncQrzUsesStoredApiKeyAndMarksConfirmations()
    {
        var qrzAdif = Adif(
            header: [],
            records: [Qsl("K4QRZ", "20M", "FT8", "DATA", "20260502", "102000", "20260503")],
            includeEoh: false);
        using var h = new Harness(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("https://logbook.qrz.com/api", req.RequestUri!.ToString());
            Assert.Contains("Zeus/", req.Headers.UserAgent.ToString(), StringComparison.Ordinal);
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("ACTION=FETCH", body, StringComparison.Ordinal);
            Assert.Contains("STATUS%3ACONFIRMED", body, StringComparison.Ordinal);
            return Text($"RESULT=OK&COUNT=1&LOGIDS=42&ADIF={WebUtility.UrlEncode(qrzAdif)}");
        });
        h.Plugin.Entries.Add(Entry("qso-1", "K4QRZ", "20m", "FT8", DateTimeUtc(2026, 5, 2, 10, 30)));
        await h.Qrz.SetApiKeyAsync("QRZ-KEY");

        var result = await h.Service.SyncAsync();

        Assert.Equal(0, result.Lotw.Fetched);
        Assert.Equal(1, result.Qrz.Fetched);
        Assert.Equal(1, result.Qrz.Matched);
        var update = Assert.Single(h.Plugin.QslUpdates);
        Assert.Equal("qso-1", update.Id);
        Assert.Equal(DateTimeUtc(2026, 5, 3, 0, 0), update.QrzQslRcvdUtc);
        Assert.Null(update.LotwQslRcvdUtc);
    }

    [Fact]
    public async Task UploadMarksEntriesSentWhenTqslSucceeds()
    {
        using var h = new Harness(
            _ => new HttpResponseMessage(HttpStatusCode.OK),
            tqslPath: Path.Combine(Path.GetTempPath(), "tqsl"),
            processResult: new LotwProcessResult(0, "", "uploaded"));
        h.Plugin.Entries.Add(Entry("qso-1", "K1ABC", "20M", "FT8", DateTimeUtc(2026, 5, 1, 12, 0)));
        await h.Service.SetSettingsAsync(new LotwSettingsRequest(StationLocation: "Home"));

        var result = await h.Service.UploadAsync(new LotwUploadRequest(["qso-1"]));

        Assert.True(result.Success);
        Assert.False(result.TqslMissing);
        Assert.Equal(1, result.UploadedCount);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "tqsl"), h.ProcessRunner.FileName);
        Assert.Contains("-l", h.ProcessRunner.Arguments);
        Assert.Contains("Home", h.ProcessRunner.Arguments);
        var update = Assert.Single(h.Plugin.QslUpdates);
        Assert.Equal("qso-1", update.Id);
        Assert.NotNull(update.LotwQslSentUtc);
        // Successful uploads clean up the temp ADIF and report no leftover path.
        Assert.Null(result.ExportedPath);
    }

    [Fact]
    public async Task UploadNoOpsWhenNothingPending()
    {
        using var h = new Harness(
            _ => new HttpResponseMessage(HttpStatusCode.OK),
            tqslPath: Path.Combine(Path.GetTempPath(), "tqsl"),
            processResult: new LotwProcessResult(0, "", ""));
        await h.Service.SetSettingsAsync(new LotwSettingsRequest(StationLocation: "Home"));

        var result = await h.Service.UploadAsync(new LotwUploadRequest(null));

        Assert.True(result.Success);
        Assert.Equal(0, result.UploadedCount);
        Assert.Null(result.ExportedPath);
        Assert.Null(h.ProcessRunner.FileName);
        Assert.Empty(h.Plugin.QslUpdates);
    }

    [Fact]
    public async Task UploadReturnsManualExportWhenTqslMissing()
    {
        using var h = new Harness(_ => new HttpResponseMessage(HttpStatusCode.OK), tqslPath: null);
        h.Plugin.Entries.Add(Entry("qso-1", "K1ABC", "20M", "FT8", DateTimeUtc(2026, 5, 1, 12, 0)));
        await h.Service.SetSettingsAsync(new LotwSettingsRequest(StationLocation: "Home"));

        var result = await h.Service.UploadAsync(new LotwUploadRequest(["qso-1"]));

        Assert.False(result.Success);
        Assert.True(result.TqslMissing);
        Assert.True(File.Exists(result.ExportedPath));
        Assert.Empty(h.Plugin.QslUpdates);
        try { File.Delete(result.ExportedPath); } catch { }
    }

    private static LogbookEntrySnapshot Entry(string id, string call, string band, string mode, DateTime qsoUtc) => new(
        Id: id,
        QsoDateTimeUtc: qsoUtc,
        Callsign: call,
        Name: null,
        FrequencyMhz: null,
        Band: band,
        Mode: mode,
        RstSent: "599",
        RstRcvd: "599",
        Grid: null,
        Country: null,
        Dxcc: null,
        CqZone: null,
        ItuZone: null,
        State: null,
        Comment: null,
        CreatedUtc: qsoUtc);

    private static string Qsl(
        string call,
        string band,
        string mode,
        string modeGroup,
        string qsoDate,
        string timeOn,
        string qslDate) =>
        string.Concat(
            Field("CALL", call),
            Field("BAND", band),
            Field("MODE", mode),
            Field("APP_LoTW_MODEGROUP", modeGroup),
            Field("QSO_DATE", qsoDate),
            Field("TIME_ON", timeOn),
            Field("QSLRDATE", qslDate),
            Field("QSL_RCVD", "Y"));

    private static string Adif(IEnumerable<string> header, IEnumerable<string> records, bool includeEoh = true)
    {
        var text = string.Concat(header);
        if (includeEoh)
            text += "<EOH>";
        foreach (var record in records)
            text += record + "<EOR>";
        return text;
    }

    private static string Field(string name, string value) => $"<{name}:{value.Length}>{value}";

    private static DateTime DateTimeUtc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static HttpResponseMessage Text(string value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(value) };

    private sealed class Harness : IDisposable
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-lotw-test-{Guid.NewGuid():N}.db");
        private readonly CredentialStore _credentials;
        private readonly LotwSettingsStore _settings;

        public Harness(
            Func<HttpRequestMessage, HttpResponseMessage> response,
            string? tqslPath = null,
            LotwProcessResult? processResult = null)
        {
            _credentials = new CredentialStore(NullLogger<CredentialStore>.Instance, _dbPath);
            _settings = new LotwSettingsStore(NullLogger<LotwSettingsStore>.Instance, _dbPath);
            var bridge = new LogbookPluginBridge(NullLogger<LogbookPluginBridge>.Instance);
            bridge.Attach(Plugin);
            Qrz = new QrzService(
                new StubHttpClientFactory(response),
                NullLogger<QrzService>.Instance,
                _credentials);
            ProcessRunner = new FakeProcessRunner(processResult ?? new LotwProcessResult(0, "", ""));
            Service = new LotwService(
                new StubHttpClientFactory(response),
                _credentials,
                _settings,
                bridge,
                Qrz,
                ProcessRunner,
                new FakeTqslLocator(tqslPath),
                NullLogger<LotwService>.Instance);
        }

        public FakePlugin Plugin { get; } = new();
        public QrzService Qrz { get; }
        public FakeProcessRunner ProcessRunner { get; }
        public LotwService Service { get; }

        public void Dispose()
        {
            _settings.Dispose();
            _credentials.Dispose();
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
            try { if (File.Exists(_dbPath + "-log")) File.Delete(_dbPath + "-log"); } catch { }
        }
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHttpHandler(response))
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class FakeTqslLocator(string? path) : ILotwTqslLocator
    {
        public string? Locate() => path;
    }

    private sealed class FakeProcessRunner(LotwProcessResult result) : ILotwProcessRunner
    {
        public string? FileName { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<LotwProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken ct)
        {
            FileName = fileName;
            Arguments = arguments.ToArray();
            return Task.FromResult(result);
        }
    }

    private sealed class FakePlugin : ILogbookPluginV2
    {
        public List<LogbookEntrySnapshot> Entries { get; } = [];
        public List<LogbookQslStatusUpdate> QslUpdates { get; } = [];

        public Task<LogbookEntrySnapshot> CreateAsync(LogbookNewEntry entry, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LogbookPage> GetEntriesAsync(int skip, int take, CancellationToken ct = default) =>
            Task.FromResult(new LogbookPage(Entries.Skip(skip).Take(take).ToList(), Entries.Count));

        public Task<IReadOnlyList<LogbookEntrySnapshot>> GetByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default)
        {
            var idSet = ids.ToHashSet(StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyList<LogbookEntrySnapshot>>(Entries.Where(e => idSet.Contains(e.Id)).ToList());
        }

        public Task<LogbookWorkedSummary?> GetWorkedSummaryAsync(string callsign, int recentTake, CancellationToken ct = default) =>
            Task.FromResult<LogbookWorkedSummary?>(null);

        public Task<IReadOnlyList<string>> GetDigitalWorkedCallsignsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> UpdateQrzUploadStatusAsync(string id, string qrzLogId, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<int> DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<string> ExportAdifAsync(IEnumerable<string>? ids = null, CancellationToken ct = default) =>
            Task.FromResult("ADIF Export from Zeus\n<EOH>\n");

        public Task<LogbookExportFileResult> ExportAdifToFileAsync(string? directory = null, IEnumerable<string>? ids = null, CancellationToken ct = default) =>
            Task.FromResult(new LogbookExportFileResult("", 0, 0));

        public Task<LogbookImportResult> ImportAdifAsync(string adifText, CancellationToken ct = default) =>
            Task.FromResult(new LogbookImportResult(0, 0, 0, 0, []));

        public Task<LogbookEntrySnapshot?> UpdateAsync(string id, LogbookEntryUpdate update, CancellationToken ct = default) =>
            Task.FromResult<LogbookEntrySnapshot?>(Entries.FirstOrDefault(e => e.Id == id));

        public Task<int> UpdateQslStatusAsync(IReadOnlyList<LogbookQslStatusUpdate> updates, CancellationToken ct = default)
        {
            QslUpdates.AddRange(updates);
            return Task.FromResult(updates.Count);
        }

        public Task<IReadOnlyList<string>> GetAllTagsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
