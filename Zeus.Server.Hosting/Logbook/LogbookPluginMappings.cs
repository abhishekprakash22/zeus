// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Text;
using LiteDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Server;

internal static class LogbookPluginMappings
{
    public const string MissingPluginError = "logbook plugin not installed";
    public const string PluginGoneError = "logbook plugin was uninstalled during the request";
    public const string EditingUnsupportedError = "logbook plugin does not support editing — update the Logbook plugin";

    public static IResult MissingPlugin() =>
        Results.Json(new { error = MissingPluginError }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult PluginGone() =>
        Results.Json(new { error = PluginGoneError }, statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// Run a seam call against the <paramref name="instance"/> snapshot a handler
    /// took from the bridge. A live uninstall can dispose that instance mid-request;
    /// only THAT case is translated to a 503. ObjectDisposedException is definitive.
    /// InvalidOperationException/LiteException qualify only when the instance is no
    /// longer the bridge's current plugin — on a still-attached plugin they are real
    /// bugs and must surface as 500s, not masquerade as "plugin unavailable".
    /// </summary>
    public static async Task<IResult> GuardAsync(
        Func<Task<IResult>> action, LogbookPluginBridge bridge, ILogbookPlugin instance, ILogger log)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsPluginDisposedFailure(ex, bridge, instance))
        {
            log.LogWarning(ex, "logbook.plugin.unavailable during request (live uninstall race)");
            return PluginGone();
        }
    }

    private static bool IsPluginDisposedFailure(Exception ex, LogbookPluginBridge bridge, ILogbookPlugin instance) =>
        ex is ObjectDisposedException
        || (ex is InvalidOperationException or LiteException && !ReferenceEquals(bridge.Current, instance));

    public static LogbookNewEntry ToPlugin(CreateLogEntryRequest req) => new(
        Callsign: req.Callsign,
        Name: req.Name,
        FrequencyMhz: req.FrequencyMhz,
        Band: req.Band,
        Mode: req.Mode,
        RstSent: req.RstSent,
        RstRcvd: req.RstRcvd,
        Grid: req.Grid,
        Country: req.Country,
        Dxcc: req.Dxcc,
        CqZone: req.CqZone,
        ItuZone: req.ItuZone,
        State: req.State,
        Comment: req.Comment,
        QsoDateTimeUtc: req.QsoDateTimeUtc);

    public static LogbookEntryUpdate ToPlugin(UpdateLogEntryRequest req) => new(
        Name: req.Name,
        Grid: req.Grid,
        Country: req.Country,
        State: req.State,
        Comment: req.Comment,
        Tags: req.Tags,
        QslSent: req.QslSent,
        QslRcvd: req.QslRcvd,
        QslSentDate: req.QslSentDate,
        QslRcvdDate: req.QslRcvdDate,
        Rig: req.Rig,
        Antenna: req.Antenna,
        TxPowerW: req.TxPowerW,
        RstSent: req.RstSent,
        RstRcvd: req.RstRcvd,
        Mode: req.Mode,
        Band: req.Band,
        FrequencyMhz: req.FrequencyMhz,
        QsoDateTimeUtc: req.QsoDateTimeUtc,
        ClearQslSentDate: req.ClearQslSentDate,
        ClearQslRcvdDate: req.ClearQslRcvdDate,
        ClearTxPowerW: req.ClearTxPowerW,
        ClearFrequencyMhz: req.ClearFrequencyMhz);

    public static LogEntry ToContract(LogbookEntrySnapshot entry) => new(
        Id: entry.Id,
        QsoDateTimeUtc: entry.QsoDateTimeUtc,
        Callsign: entry.Callsign,
        Name: entry.Name,
        FrequencyMhz: entry.FrequencyMhz,
        Band: entry.Band,
        Mode: entry.Mode,
        RstSent: entry.RstSent,
        RstRcvd: entry.RstRcvd,
        Grid: entry.Grid,
        Country: entry.Country,
        Dxcc: entry.Dxcc,
        CqZone: entry.CqZone,
        ItuZone: entry.ItuZone,
        State: entry.State,
        Comment: entry.Comment,
        CreatedUtc: entry.CreatedUtc,
        QrzLogId: entry.QrzLogId,
        QrzUploadedUtc: entry.QrzUploadedUtc,
        Tags: entry.Tags,
        QslSent: entry.QslSent,
        QslRcvd: entry.QslRcvd,
        QslSentDate: entry.QslSentDate,
        QslRcvdDate: entry.QslRcvdDate,
        LotwQslSentUtc: entry.LotwQslSentUtc,
        LotwQslRcvdUtc: entry.LotwQslRcvdUtc,
        QrzQslRcvdUtc: entry.QrzQslRcvdUtc,
        Rig: entry.Rig,
        Antenna: entry.Antenna,
        TxPowerW: entry.TxPowerW);

    public static LogEntriesResponse ToContract(LogbookPage page) =>
        new(page.Entries.Select(ToContract).ToList(), page.TotalCount);

    public static AdifImportResponse ToContract(LogbookImportResult result) => new(
        TotalRecords: result.TotalRecords,
        ImportedCount: result.ImportedCount,
        DuplicateCount: result.DuplicateCount,
        SkippedCount: result.SkippedCount,
        Errors: result.Errors.Select(e => new AdifImportError(e.RecordNumber, e.Message)).ToList());

    public static AdifExportToFileResponse ToContract(LogbookExportFileResult result) =>
        new(result.Path, result.Count, result.Bytes);

    public static WorkedCallsignSummary ToContract(LogbookWorkedSummary summary) => new(
        Callsign: summary.Callsign,
        WorkedBefore: summary.WorkedBefore,
        TotalCount: summary.TotalCount,
        LastWorkedUtc: summary.LastWorkedUtc,
        LastBand: summary.LastBand,
        LastMode: summary.LastMode,
        LastFrequencyMhz: summary.LastFrequencyMhz,
        LastRstSent: summary.LastRstSent,
        LastRstRcvd: summary.LastRstRcvd,
        LastName: summary.LastName,
        LastGrid: summary.LastGrid,
        LastCountry: summary.LastCountry,
        LastState: summary.LastState,
        LastComment: summary.LastComment,
        Bands: summary.Bands,
        Modes: summary.Modes,
        RecentQsos: summary.RecentQsos.Select(q => new WorkedCallsignRecentQso(
            QsoDateTimeUtc: q.QsoDateTimeUtc,
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

    public static WorkedCallsignSummary EmptyWorkedSummary(string callsign) => new(
        Callsign: (callsign ?? string.Empty).Trim().ToUpperInvariant(),
        WorkedBefore: false,
        TotalCount: 0,
        LastWorkedUtc: null,
        LastBand: null,
        LastMode: null,
        LastFrequencyMhz: null,
        LastRstSent: null,
        LastRstRcvd: null,
        LastName: null,
        LastGrid: null,
        LastCountry: null,
        LastState: null,
        LastComment: null,
        Bands: [],
        Modes: [],
        RecentQsos: []);

    public static string EmptyAdifExport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("ADIF Export from Zeus");
        sb.AppendLine("<ADIF_VER:5>3.1.4");
        sb.AppendLine("<PROGRAMID:4>Zeus");
        sb.AppendLine("<PROGRAMVERSION:5>1.0.0");
        sb.AppendLine("<EOH>");
        sb.AppendLine();
        return sb.ToString();
    }
}
