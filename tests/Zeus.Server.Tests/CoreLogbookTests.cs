// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR),
//                         Ramón Martínez (EA5IUE), and contributors.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class CoreLogbookTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"zeus-core-logbook-{Guid.NewGuid():N}.db");

    private CoreLogbook NewLogbook() => new(NullLogger<CoreLogbook>.Instance, _dbPath);

    private static LogbookNewEntry Qso(
        string call,
        string mode = "FT8",
        string band = "20M",
        DateTime? when = null) => new(
            Callsign: call,
            Name: "Ramón",
            FrequencyMhz: 14.074,
            Band: band,
            Mode: mode,
            RstSent: "-10",
            RstRcvd: "-12",
            Grid: "IM98",
            Country: "Spain",
            Dxcc: 281,
            CqZone: 14,
            ItuZone: 37,
            State: null,
            Comment: "core logbook test",
            QsoDateTimeUtc: when ?? new DateTime(2026, 9, 20, 11, 30, 0, DateTimeKind.Utc));

    public void Dispose()
    {
        foreach (var f in new[] { _dbPath, _dbPath + "-log" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Create_stores_the_qso_and_it_comes_back_in_the_page()
    {
        using var log = NewLogbook();

        var saved = await log.CreateAsync(Qso("mw0usk"));

        Assert.False(string.IsNullOrWhiteSpace(saved.Id));
        Assert.Equal("MW0USK", saved.Callsign);   // callsigns normalise on the way in
        Assert.Equal(DateTimeKind.Utc, saved.QsoDateTimeUtc.Kind);

        var page = await log.GetEntriesAsync(0, 50);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal("MW0USK", page.Entries[0].Callsign);
        Assert.Equal(14.074, page.Entries[0].FrequencyMhz);
    }

    [Fact]
    public async Task Qsos_survive_a_reopen_of_the_database()
    {
        using (var first = NewLogbook())
            await first.CreateAsync(Qso("EA5BZY"));

        using var second = NewLogbook();
        var page = await second.GetEntriesAsync(0, 50);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("EA5BZY", page.Entries[0].Callsign);
        // The whole point of the UTC pinning: LiteDB hands DateTimes back as
        // Local on some platforms, and QRZ would then get the wall clock.
        Assert.Equal(DateTimeKind.Utc, page.Entries[0].QsoDateTimeUtc.Kind);
        Assert.Equal(new DateTime(2026, 9, 20, 11, 30, 0, DateTimeKind.Utc), page.Entries[0].QsoDateTimeUtc);
    }

    [Fact]
    public async Task Paging_returns_newest_first()
    {
        using var log = NewLogbook();
        await log.CreateAsync(Qso("EA1AAA", when: new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc)));
        await log.CreateAsync(Qso("EA2BBB", when: new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc)));
        await log.CreateAsync(Qso("EA3CCC", when: new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc)));

        var first = await log.GetEntriesAsync(0, 2);
        Assert.Equal(3, first.TotalCount);
        Assert.Equal(["EA3CCC", "EA2BBB"], first.Entries.Select(e => e.Callsign));

        var second = await log.GetEntriesAsync(2, 2);
        Assert.Equal(["EA1AAA"], second.Entries.Select(e => e.Callsign));
    }

    [Fact]
    public async Task Worked_summary_reports_bands_modes_and_recent_qsos()
    {
        using var log = NewLogbook();
        await log.CreateAsync(Qso("MW0USK", mode: "FT8", band: "20M",
            when: new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc)));
        await log.CreateAsync(Qso("MW0USK", mode: "SSB", band: "40M",
            when: new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc)));

        var summary = await log.GetWorkedSummaryAsync("mw0usk", 5);

        Assert.NotNull(summary);
        Assert.True(summary!.WorkedBefore);
        Assert.Equal(2, summary.TotalCount);
        Assert.Equal("40M", summary.LastBand);
        Assert.Equal("SSB", summary.LastMode);
        Assert.Equal(2, summary.RecentQsos.Count);
        Assert.Contains("20M", summary.Bands);
        Assert.Contains("FT8", summary.Modes);
    }

    [Fact]
    public async Task Worked_summary_for_a_new_callsign_is_not_worked_before()
    {
        using var log = NewLogbook();
        await log.CreateAsync(Qso("MW0USK"));

        var summary = await log.GetWorkedSummaryAsync("DL1ABC", 5);

        Assert.NotNull(summary);
        Assert.False(summary!.WorkedBefore);
        Assert.Equal(0, summary.TotalCount);
        Assert.Empty(summary.RecentQsos);
    }

    [Fact]
    public async Task Digital_worked_list_skips_phone_and_cw()
    {
        using var log = NewLogbook();
        await log.CreateAsync(Qso("EA1FT8", mode: "FT8"));
        await log.CreateAsync(Qso("EA2FT4", mode: "ft4"));
        await log.CreateAsync(Qso("EA3SSB", mode: "SSB"));
        await log.CreateAsync(Qso("EA4CW", mode: "CW"));

        var calls = await log.GetDigitalWorkedCallsignsAsync();

        Assert.Contains("EA1FT8", calls);
        Assert.Contains("EA2FT4", calls);
        Assert.DoesNotContain("EA3SSB", calls);
        Assert.DoesNotContain("EA4CW", calls);
    }

    [Fact]
    public async Task Qrz_upload_status_is_recorded()
    {
        using var log = NewLogbook();
        var saved = await log.CreateAsync(Qso("MW0USK"));

        Assert.True(await log.UpdateQrzUploadStatusAsync(saved.Id, "123456"));
        Assert.False(await log.UpdateQrzUploadStatusAsync("nope", "123456"));

        var back = (await log.GetByIdsAsync([saved.Id])).Single();
        Assert.Equal("123456", back.QrzLogId);
        Assert.NotNull(back.QrzUploadedUtc);
        Assert.Equal(DateTimeKind.Utc, back.QrzUploadedUtc!.Value.Kind);
    }

    [Fact]
    public async Task Delete_removes_only_the_named_ids()
    {
        using var log = NewLogbook();
        var a = await log.CreateAsync(Qso("EA1AAA"));
        await log.CreateAsync(Qso("EA2BBB"));

        Assert.Equal(1, await log.DeleteAsync([a.Id, "missing-id"]));

        var page = await log.GetEntriesAsync(0, 50);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal("EA2BBB", page.Entries[0].Callsign);
    }

    [Fact]
    public async Task Update_edits_fields_and_the_clear_flags_empty_them()
    {
        using var log = NewLogbook();
        var saved = await log.CreateAsync(Qso("MW0USK"));

        var edited = await log.UpdateAsync(saved.Id, new LogbookEntryUpdate(
            Name: "Steve",
            Comment: "nice signal",
            Tags: ["dx", "dx", "sota"],
            Rig: "Hermes Lite 2",
            TxPowerW: 5.0));

        Assert.NotNull(edited);
        Assert.Equal("Steve", edited!.Name);
        Assert.Equal(5.0, edited.TxPowerW);
        Assert.Equal(2, edited.Tags!.Count);   // tags de-duplicate

        var cleared = await log.UpdateAsync(saved.Id, new LogbookEntryUpdate(ClearTxPowerW: true));
        Assert.Null(cleared!.TxPowerW);
        Assert.Equal("Steve", cleared.Name);   // untouched fields stay put

        Assert.Null(await log.UpdateAsync("missing-id", new LogbookEntryUpdate(Name: "nobody")));
    }

    [Fact]
    public async Task Tags_are_listed_across_entries()
    {
        using var log = NewLogbook();
        var a = await log.CreateAsync(Qso("EA1AAA"));
        var b = await log.CreateAsync(Qso("EA2BBB"));
        await log.UpdateAsync(a.Id, new LogbookEntryUpdate(Tags: ["sota", "dx"]));
        await log.UpdateAsync(b.Id, new LogbookEntryUpdate(Tags: ["DX", "contest"]));

        var tags = await log.GetAllTagsAsync();

        // Sorted, and "dx"/"DX" count once — whichever casing was stored first wins.
        Assert.Equal(["contest", "dx", "sota"], tags.Select(t => t.ToLowerInvariant()));
    }

    [Fact]
    public async Task Qsl_status_updates_land_on_the_entry()
    {
        using var log = NewLogbook();
        var saved = await log.CreateAsync(Qso("MW0USK"));
        var rcvd = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

        var changed = await log.UpdateQslStatusAsync(
        [
            new LogbookQslStatusUpdate(saved.Id, LotwQslRcvdUtc: rcvd, LotwQslSentUtc: null,
                QrzQslRcvdUtc: null, QslRcvd: "Y", QslRcvdDate: rcvd),
            new LogbookQslStatusUpdate("missing-id", rcvd, null, null, "Y", rcvd),
        ]);

        Assert.Equal(1, changed);
        var back = (await log.GetByIdsAsync([saved.Id])).Single();
        Assert.Equal(rcvd, back.LotwQslRcvdUtc);
        Assert.Equal("Y", back.QslRcvd);
    }

    [Fact]
    public async Task Adif_export_round_trips_through_import()
    {
        using var source = NewLogbook();
        await source.CreateAsync(Qso("MW0USK", mode: "FT8", band: "20M"));
        await source.CreateAsync(Qso("EA5BZY", mode: "SSB", band: "40M",
            when: new DateTime(2026, 9, 19, 8, 15, 0, DateTimeKind.Utc)));
        var adif = await source.ExportAdifAsync();

        Assert.Contains("<EOH>", adif);
        Assert.Contains("<CALL:6>MW0USK", adif);
        Assert.Contains("<BAND:3>20M", adif);

        var otherPath = _dbPath + ".import.db";
        try
        {
            using var target = new CoreLogbook(NullLogger<CoreLogbook>.Instance, otherPath);
            var result = await target.ImportAdifAsync(adif);

            Assert.Equal(2, result.TotalRecords);
            Assert.Equal(2, result.ImportedCount);
            Assert.Empty(result.Errors);

            var page = await target.GetEntriesAsync(0, 50);
            var top = page.Entries[0];
            Assert.Equal("MW0USK", top.Callsign);
            Assert.Equal("FT8", top.Mode);
            Assert.Equal(new DateTime(2026, 9, 20, 11, 30, 0, DateTimeKind.Utc), top.QsoDateTimeUtc);
            Assert.Equal(14.074, top.FrequencyMhz!.Value, 6);
            Assert.Equal("IM98", top.Grid);
            Assert.Equal(281, top.Dxcc);
        }
        finally
        {
            foreach (var f in new[] { otherPath, otherPath + "-log" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Importing_the_same_file_twice_only_counts_duplicates()
    {
        using var log = NewLogbook();
        await log.CreateAsync(Qso("MW0USK"));
        var adif = await log.ExportAdifAsync();

        var result = await log.ImportAdifAsync(adif);

        Assert.Equal(1, result.TotalRecords);
        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(1, result.DuplicateCount);
        Assert.Equal(1, (await log.GetEntriesAsync(0, 50)).TotalCount);
    }

    [Fact]
    public async Task Import_reports_records_it_cannot_use()
    {
        using var log = NewLogbook();

        var result = await log.ImportAdifAsync(
            "<EOH>\n" +
            "<QSO_DATE:8>20260920 <TIME_ON:6>113000 <BAND:3>20M <MODE:3>FT8 <EOR>\n" +
            "<CALL:6>MW0USK <BAND:3>20M <MODE:3>FT8 <EOR>\n" +
            "<CALL:6>EA5BZY <QSO_DATE:8>20260920 <TIME_ON:4>1130 <BAND:3>20M <MODE:3>FT8 <EOR>\n");

        Assert.Equal(3, result.TotalRecords);
        Assert.Equal(1, result.ImportedCount);   // only the complete one
        Assert.Equal(2, result.SkippedCount);
        Assert.Equal(2, result.Errors.Count);

        var page = await log.GetEntriesAsync(0, 50);
        Assert.Equal("EA5BZY", page.Entries[0].Callsign);
        // TIME_ON given to the minute still lands on a whole second.
        Assert.Equal(new DateTime(2026, 9, 20, 11, 30, 0, DateTimeKind.Utc), page.Entries[0].QsoDateTimeUtc);
    }

    [Fact]
    public async Task Import_derives_the_band_from_the_frequency_when_it_is_missing()
    {
        using var log = NewLogbook();

        var result = await log.ImportAdifAsync(
            "<EOH>\n<CALL:6>MW0USK <QSO_DATE:8>20260920 <TIME_ON:6>113000 " +
            "<FREQ:9>14.074000 <MODE:3>FT8 <EOR>\n");

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal("20m", (await log.GetEntriesAsync(0, 50)).Entries[0].Band);
    }

    [Fact]
    public async Task Adif_field_lengths_are_utf8_octet_counts()
    {
        using var log = NewLogbook();
        // "Ramón" is 5 characters but 6 UTF-8 bytes — counting UTF-16 units
        // truncates the field for whoever reads the file back.
        await log.CreateAsync(Qso("EA5IUE"));

        var adif = await log.ExportAdifAsync();

        Assert.Contains("<NAME:6>Ramón", adif);
    }

    [Fact]
    public async Task Export_to_file_writes_the_adif_and_counts_the_records()
    {
        using var log = NewLogbook();
        await log.CreateAsync(Qso("MW0USK"));
        await log.CreateAsync(Qso("EA5BZY"));

        var dir = Path.Combine(Path.GetTempPath(), $"zeus-adif-{Guid.NewGuid():N}");
        try
        {
            var result = await log.ExportAdifToFileAsync(dir);

            Assert.Equal(2, result.Count);
            Assert.True(File.Exists(result.Path));
            Assert.True(result.Bytes > 0);
            Assert.Contains("<CALL:6>MW0USK", await File.ReadAllTextAsync(result.Path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Export_can_be_limited_to_selected_ids()
    {
        using var log = NewLogbook();
        var a = await log.CreateAsync(Qso("MW0USK"));
        await log.CreateAsync(Qso("EA5BZY"));

        var adif = await log.ExportAdifAsync([a.Id]);

        Assert.Contains("MW0USK", adif);
        Assert.DoesNotContain("EA5BZY", adif);
    }

    [Fact]
    public void Bridge_prefers_a_plugin_over_the_built_in_logbook()
    {
        var bridge = new LogbookPluginBridge(NullLogger<LogbookPluginBridge>.Instance);
        using var core = NewLogbook();

        bridge.AttachFallback(core);
        Assert.Same(core, bridge.Current);

        var plugin = new StubLogbookPlugin();
        bridge.Attach(plugin);
        Assert.Same(plugin, bridge.Current);

        // ...and the built-in store takes over again when the plugin goes away.
        bridge.Detach(plugin);
        Assert.Same(core, bridge.Current);
    }

    private sealed class StubLogbookPlugin : ILogbookPlugin
    {
        public Task<LogbookEntrySnapshot> CreateAsync(LogbookNewEntry entry, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LogbookPage> GetEntriesAsync(int skip, int take, CancellationToken ct = default) =>
            Task.FromResult(new LogbookPage([], 0));
        public Task<IReadOnlyList<LogbookEntrySnapshot>> GetByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LogbookEntrySnapshot>>([]);
        public Task<LogbookWorkedSummary?> GetWorkedSummaryAsync(string callsign, int recentTake, CancellationToken ct = default) =>
            Task.FromResult<LogbookWorkedSummary?>(null);
        public Task<IReadOnlyList<string>> GetDigitalWorkedCallsignsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> UpdateQrzUploadStatusAsync(string id, string qrzLogId, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<int> DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default) =>
            Task.FromResult(0);
        public Task<string> ExportAdifAsync(IEnumerable<string>? ids = null, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
        public Task<LogbookExportFileResult> ExportAdifToFileAsync(string? directory = null, IEnumerable<string>? ids = null, CancellationToken ct = default) =>
            Task.FromResult(new LogbookExportFileResult(string.Empty, 0, 0));
        public Task<LogbookImportResult> ImportAdifAsync(string adifText, CancellationToken ct = default) =>
            Task.FromResult(new LogbookImportResult(0, 0, 0, 0, []));
    }
}
