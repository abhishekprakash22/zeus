// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// FreeDV HTTP surface, served from CORE under the plugin route prefix the
// frontend already targets: /api/plugins/org.openhpsdr.freedv/...
//
// The contract is dictated by zeus-web/src/api/client.ts (FreeDvStatusDto /
// FreeDvConfigRequest / FreeDvInstallStatusDto / FreeDvStationsResponseDto /
// FreeDvReporterSettings) and zeus-web/src/api/freedv-plugin.ts — those are
// already written and tested, so this matches them exactly rather than
// inventing shapes. GET /status is the liveness probe AND the mode gate:
// 2xx unlocks the FREEDV mode entry, 404/503 keeps it gated.
//
// SCOPE NOTES
// - /install exists for UI parity with the retired plugin-zip flow. The
//   natives ship inside the Zeus binary (Zeus.Dsp/runtimes/{rid}/native), so
//   the endpoint reports an already-installed shape when libcodec2 loads and
//   a terminal "failed" shape (with the rebuild hint) when it does not —
//   there is nothing to download.
// - /stations, /reporter/settings and QSY front FreeDvReporterService, the
//   qso.freedv.org client. Polling /stations keeps a "view" session alive
//   while the panel is open; "report" role (opt-in + callsign + grid) stays
//   connected on its own. QSY returns 409 unless we are reporting and the
//   station is known.

using Zeus.Server.Hosting.FreeDv;

namespace Zeus.Server.Hosting.FreeDv;

public static class FreeDvEndpoints
{
    public const string PluginId = "org.openhpsdr.freedv";

    public static IEndpointRouteBuilder MapFreeDvEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup($"/api/plugins/{PluginId}");

        // Liveness probe / mode gate + panel telemetry.
        g.MapGet("/status", (FreeDvModemService modem) =>
            Results.Ok(StatusDto.From(modem.Snapshot())));

        // PUT /config — only supplied (non-null) fields change; returns the
        // updated status so the panel reconciles in one round-trip.
        g.MapPut("/config", (ConfigRequest req, FreeDvModemService modem) =>
        {
            FreeDvSubmode? submode = null;
            if (req.Submode is string s)
            {
                if (!Enum.TryParse<FreeDvSubmode>(s, ignoreCase: true, out var parsed))
                    return Results.BadRequest(new { error = $"unknown submode '{s}'" });
                submode = parsed;
            }
            var status = modem.Configure(
                submode, req.AutoDetect, req.SquelchEnabled,
                req.SnrSquelchThreshDb, req.TxText);
            return Results.Ok(StatusDto.From(status));
        });

        // ---- install (compat: natives are bundled, nothing to fetch) --------
        g.MapGet("/install", (FreeDvModemService modem) =>
            Results.Ok(InstallDto.From(modem.NativeAvailable)));
        g.MapPost("/install", (FreeDvModemService modem) =>
            Results.Ok(InstallDto.From(modem.NativeAvailable)));

        // ---- FreeDV Reporter ------------------------------------------------
        g.MapGet("/stations", (FreeDvReporterService reporter) =>
        {
            var s = reporter.GetStations();
            return Results.Ok(new
            {
                connectionState = s.ConnectionState,
                enabled = s.Enabled,
                stations = s.Stations.Select(st => new
                {
                    sid = st.Sid,
                    callsign = st.Callsign,
                    gridSquare = st.GridSquare,
                    freqHz = st.FreqHz,
                    mode = st.Mode,
                    transmitting = st.Transmitting,
                    rxOnly = st.RxOnly,
                    message = st.Message,
                    version = st.Version,
                    lastRxSnr = st.LastRxSnr,
                    lastRxCallsign = st.LastRxCallsign,
                    lastRxMode = st.LastRxMode,
                    lastUpdate = st.LastUpdate,
                    connectTime = st.ConnectTime,
                }),
                reporting = s.Reporting,
                mySid = s.MySid,
                incomingQsy = s.IncomingQsy is { } q
                    ? new
                    {
                        id = q.Id,
                        callsign = q.Callsign,
                        freqHz = q.FreqHz,
                        message = q.Message,
                        receivedUtc = q.ReceivedUtc.ToString("o"),
                    }
                    : null,
            });
        });

        g.MapGet("/reporter/settings", (FreeDvSettingsStore store) =>
            Results.Ok(ReporterDto.From(store.GetReporter())));

        g.MapPost("/reporter/settings", (ReporterDto req, FreeDvSettingsStore store, FreeDvReporterService reporter) =>
        {
            var saved = store.SetReporter(new FreeDvReporterSettings(
                req.ReportEnabled, req.Callsign ?? "", req.GridSquare ?? "", req.Message ?? ""));
            reporter.ReporterSettingsChanged();
            return Results.Ok(ReporterDto.From(saved));
        });

        g.MapPost("/stations/{sid}/qsy", async (string sid, FreeDvReporterService reporter, CancellationToken ct) =>
            // 200 with a body, not 204: the panel's jsonFetch always parses the
            // response, and WebKit rejects an empty body ("The string did not
            // match the expected pattern").
            await reporter.RequestQsyAsync(sid, ct)
                ? Results.Ok(new { ok = true })
                : Results.Json(
                    new { error = "not reporting", message = "Not reporting to FreeDV Reporter, or that station is gone" },
                    statusCode: StatusCodes.Status409Conflict));

        return app;
    }

    // ---- wire DTOs (System.Text.Json Web defaults → camelCase) --------------

    /// <summary>Mirrors zeus-web FreeDvStatusDto field for field.</summary>
    internal sealed record StatusDto(
        bool NativeAvailable,
        bool Active,
        string Submode,
        bool Synced,
        double SnrDb,
        bool SquelchEnabled,
        double SnrSquelchThreshDb,
        int SpeechSampleRateHz,
        int ModemSampleRateHz,
        string? RxText,
        string? TxText,
        string? LibraryVersion,
        bool AutoDetect,
        bool RadeAvailable)
    {
        public static StatusDto From(FreeDvModemStatus s) => new(
            s.NativeAvailable, s.Active, s.Submode.ToString(), s.Synced,
            Math.Round(s.SnrDb, 1), s.SquelchEnabled, s.SnrSquelchThreshDb,
            s.SpeechSampleRateHz, s.ModemSampleRateHz, s.RxText, s.TxText,
            s.LibraryVersion, s.AutoDetect, s.RadeAvailable);
    }

    /// <summary>PUT /config body — every field optional, null = unchanged.</summary>
    internal sealed record ConfigRequest(
        string? Submode,
        bool? AutoDetect,
        bool? SquelchEnabled,
        double? SnrSquelchThreshDb,
        string? TxText);

    internal sealed record InstallDto(
        string Phase, int Percent, string? Message, bool Installed)
    {
        public static InstallDto From(bool nativeAvailable) => nativeAvailable
            ? new("done", 100, "FreeDV natives ship with Zeus — nothing to install.", true)
            : new("failed", 0,
                "libcodec2 did not load for this platform. Rebuild native/codec2 " +
                "(see native/codec2/VENDORING.md) and redeploy runtimes/{rid}/native.",
                false);
    }

    internal sealed record ReporterDto(
        bool ReportEnabled, string? Callsign, string? GridSquare, string? Message)
    {
        public static ReporterDto From(FreeDvReporterSettings s) =>
            new(s.ReportEnabled, s.Callsign, s.GridSquare, s.Message);
    }
}
