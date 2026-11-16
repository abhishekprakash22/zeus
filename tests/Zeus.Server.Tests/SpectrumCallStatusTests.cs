// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// Logbook status of calls painted on the spectrum: new DXCC / new on band /
// needed / worked, and the log re-index when the entry count changes.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Server;
using Zeus.Server.Hosting.Dxcc;

namespace Zeus.Server.Tests;

public sealed class SpectrumCallStatusTests
{
    private static LogbookEntrySnapshot Entry(string call, string band, int? dxcc = null) =>
        new(Guid.NewGuid().ToString("N"), DateTime.UtcNow, call, null, null, band, "SSB", "59", "59",
            null, null, dxcc, null, null, null, null, DateTime.UtcNow);

    private static readonly DxccTable Table = DxccTable.Default;

    private static CallStatusIndex Index(params LogbookEntrySnapshot[] e) => CallStatusService.BuildIndex(e, Table);

    [Fact]
    public void Worked_on_this_band()
    {
        var r = CallStatusService.Classify(Index(Entry("DL1ABC", "20m")), "DL1ABC", 14_074_000, Table);
        Assert.Equal("Worked", r.Status);
        Assert.Equal("20m", r.Band);
        Assert.True(r.WorkedAnyBand);
    }

    [Fact]
    public void Call_worked_elsewhere_entity_worked_here_is_needed()
    {
        var idx = Index(Entry("DL1ABC", "40m"), Entry("DL2XYZ", "20m"));
        var r = CallStatusService.Classify(idx, "DL1ABC", 14_200_000, Table);
        Assert.Equal("Needed", r.Status);
        Assert.True(r.WorkedAnyBand);
    }

    [Fact]
    public void Entity_worked_only_on_other_bands_is_new_on_band()
    {
        var idx = Index(Entry("JA1AAA", "40m"));
        var r = CallStatusService.Classify(idx, "JA1BBB", 14_200_000, Table);
        Assert.Equal("NewBand", r.Status);
        Assert.Equal(Table.Lookup("JA1BBB")!.Dxcc, r.Dxcc);
        Assert.False(r.WorkedAnyBand);
    }

    [Fact]
    public void Entity_never_worked_is_new_dxcc()
    {
        var idx = Index(Entry("W1AW", "20m"));
        var r = CallStatusService.Classify(idx, "VK2ABC", 14_200_000, Table);
        Assert.Equal("NewDxcc", r.Status);
        Assert.Equal("Australia", r.Entity);
    }

    [Fact]
    public void No_logbook_gives_no_status_but_still_the_entity()
    {
        var r = CallStatusService.Classify(null, "DL1ABC", 14_200_000, Table);
        Assert.Null(r.Status);
        Assert.Equal(230, r.Dxcc);
    }

    [Fact]
    public void Portable_suffix_matches_the_home_call()
    {
        var idx = Index(Entry("DL1ABC", "20m"));
        Assert.Equal("Worked", CallStatusService.Classify(idx, "DL1ABC/P", 14_074_000, Table).Status);
        Assert.Equal("DL1ABC", CallStatusService.NormalizeCall("dl1abc/p"));
        Assert.Equal("VK2/G4ABC", CallStatusService.NormalizeCall("VK2/G4ABC"));
    }

    [Fact]
    public void Band_labels_are_normalised()
    {
        var idx = Index(Entry("DL1ABC", "20M"));
        Assert.Equal("Worked", CallStatusService.Classify(idx, "DL1ABC", 14_074_000, Table).Status);
        Assert.Equal("20m", CallStatusService.NormalizeBand("20"));
        Assert.Null(CallStatusService.NormalizeBand("  "));
    }

    [Fact]
    public void Stored_dxcc_number_wins_over_the_prefix_lookup()
    {
        // An entry logged with an explicit entity (e.g. a special-event call)
        // counts toward that entity.
        var idx = Index(Entry("XX9SPECIAL", "20m", dxcc: 150));
        Assert.Equal("NewBand", CallStatusService.Classify(idx, "VK2ABC", 7_100_000, Table).Status);
    }

    [Theory]
    [InlineData("W1AW", true)]
    [InlineData("VK2/G4ABC", true)]
    [InlineData("CQ", false)]
    [InlineData("DE", false)]
    [InlineData("12345", false)]
    [InlineData("TEST", false)]
    [InlineData("W1AW;", false)]
    public void Call_shape_filter(string call, bool ok) => Assert.Equal(ok, CallStatusService.LooksLikeCall(call));

    [Fact]
    public async Task ClassifyAsync_reindexes_when_the_log_grows()
    {
        var time = new ManualTime();
        var log = new ListLogbook();
        var bridge = new LogbookPluginBridge(NullLogger<LogbookPluginBridge>.Instance);
        bridge.AttachFallback(log);
        using var svc = new CallStatusService(bridge, time);

        var items = new[] { new CallStatusItem("DL1ABC", 14_074_000) };
        Assert.Equal("NewDxcc", (await svc.ClassifyAsync(items, CancellationToken.None))[0].Status);

        log.Entries.Add(Entry("DL1ABC", "20m"));
        // Within the count-check interval the cached index stands…
        Assert.Equal("NewDxcc", (await svc.ClassifyAsync(items, CancellationToken.None))[0].Status);
        // …after it, the grown count triggers a re-index.
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal("Worked", (await svc.ClassifyAsync(items, CancellationToken.None))[0].Status);
    }

    [Fact]
    public async Task ClassifyAsync_drops_non_calls_and_caps_the_batch()
    {
        var bridge = new LogbookPluginBridge(NullLogger<LogbookPluginBridge>.Instance);
        using var svc = new CallStatusService(bridge);
        var items = Enumerable.Range(0, 300).Select(i => new CallStatusItem($"K{i % 10}AB", 14_074_000))
            .Append(new CallStatusItem("CQ", 14_074_000))
            .Append(new CallStatusItem(null, 14_074_000));
        var r = await svc.ClassifyAsync(items, CancellationToken.None);
        Assert.Equal(CallStatusService.MaxItems, r.Count);
        Assert.All(r, x => Assert.Null(x.Status)); // no logbook attached
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan d) => _now += d;
    }

    private sealed class ListLogbook : ILogbookPlugin
    {
        public List<LogbookEntrySnapshot> Entries { get; } = new();

        public Task<LogbookPage> GetEntriesAsync(int skip, int take, CancellationToken ct = default) =>
            Task.FromResult(new LogbookPage(Entries.Skip(skip).Take(take).ToList(), Entries.Count));

        public Task<LogbookEntrySnapshot> CreateAsync(LogbookNewEntry entry, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<LogbookEntrySnapshot>> GetByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LogbookWorkedSummary?> GetWorkedSummaryAsync(string callsign, int recentTake, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetDigitalWorkedCallsignsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> UpdateQrzUploadStatusAsync(string id, string qrzLogId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string> ExportAdifAsync(IEnumerable<string>? ids = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LogbookExportFileResult> ExportAdifToFileAsync(string? directory = null, IEnumerable<string>? ids = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LogbookImportResult> ImportAdifAsync(string adifText, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
