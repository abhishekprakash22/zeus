// SPDX-License-Identifier: GPL-2.0-or-later
//
// FreeDV Reporter client tests — socket.io framing, the station table built
// from reporter events (shapes from freedv-gui 2.1.0 FreeDVReporter.cpp) and
// the auth dictionary for each role. No network: HandleEvent is fed directly.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.Hosting.FreeDv;

namespace Zeus.Server.Tests;

public sealed class FreeDvReporterTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-freedv-rep-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Theory]
    [InlineData("0{\"sid\":\"x\",\"pingInterval\":25000}", "Open")]
    [InlineData("2", "Ping")]
    [InlineData("40{\"sid\":\"abc\"}", "NamespaceConnect")]
    [InlineData("42[\"tx_report\",{}]", "Event")]
    [InlineData("44{\"message\":\"no\"}", "ConnectError")]
    [InlineData("41", "Close")]
    [InlineData("1", "Close")]
    [InlineData("", "Unknown")]
    public void Classify_RecognisesTheEngineIoAndSocketIoPrefixes(string text, string expected)
    {
        Assert.Equal(expected, FreeDvReporterClient.Classify(text).ToString());
    }

    [Fact]
    public void Event_And_Sid_Parse()
    {
        Assert.True(FreeDvReporterClient.TryParseEvent("[\"freq_change\",{\"freq\":7177000}]", out var name, out var args));
        Assert.Equal("freq_change", name);
        Assert.Equal(7177000, args.GetProperty("freq").GetInt64());

        Assert.True(FreeDvReporterClient.TryParseEvent("[\"hide_self\"]", out name, out args));
        Assert.Equal("hide_self", name);
        Assert.Equal(JsonValueKind.Undefined, args.ValueKind);

        Assert.False(FreeDvReporterClient.TryParseEvent("{}", out _, out _));
        Assert.Equal("abc", FreeDvReporterClient.ReadSid("{\"sid\":\"abc\"}"));
        Assert.Null(FreeDvReporterClient.ReadSid(""));
    }

    private FreeDvReporterService NewService(out FreeDvSettingsStore store, out FreeDvModemService modem)
    {
        store = new FreeDvSettingsStore(NullLogger<FreeDvSettingsStore>.Instance, _dbPath);
        modem = new FreeDvModemService(NullLogger<FreeDvModemService>.Instance, store);
        return new FreeDvReporterService(NullLogger<FreeDvReporterService>.Instance, store, modem);
    }

    [Fact]
    public void StationTable_FollowsReporterEvents()
    {
        var svc = NewService(out var store, out var modem);
        using (store) using (modem) using (svc)
        {
            svc.HandleEvent("new_connection", Json("""
                {"sid":"s1","last_update":"2026-09-19T10:00:00Z","callsign":"EA5IUE",
                 "grid_square":"IM99","version":"freedv-gui 2.1.0","rx_only":false}
                """));
            svc.HandleEvent("freq_change", Json("""{"sid":"s1","last_update":"t2","callsign":"EA5IUE","grid_square":"IM99","freq":7177000}"""));
            svc.HandleEvent("tx_report", Json("""{"sid":"s1","last_update":"t3","callsign":"EA5IUE","grid_square":"IM99","mode":"RADEV1","transmitting":true,"last_tx":null}"""));
            svc.HandleEvent("rx_report", Json("""{"sid":"s1","last_update":"t4","receiver_callsign":"EA5IUE","receiver_grid_square":"IM99","callsign":"G4ABC","snr":7.5,"mode":"RADEV1"}"""));
            svc.HandleEvent("message_update", Json("""{"sid":"s1","last_update":"t5","message":"73"}"""));
            // Events for unknown sids are ignored, not invented.
            svc.HandleEvent("freq_change", Json("""{"sid":"nobody","last_update":"t","callsign":"X","grid_square":"Y","freq":1}"""));

            var st = Assert.Single(svc.GetStations().Stations);
            Assert.Equal("EA5IUE", st.Callsign);
            Assert.Equal("IM99", st.GridSquare);
            Assert.Equal(7177000, st.FreqHz);
            Assert.Equal("RADEV1", st.Mode);
            Assert.True(st.Transmitting);
            Assert.Equal("G4ABC", st.LastRxCallsign);
            Assert.Equal(7.5, st.LastRxSnr);
            Assert.Equal("73", st.Message);
            Assert.Equal("t5", st.LastUpdate);
            Assert.Equal("2026-09-19T10:00:00Z", st.ConnectTime);

            svc.HandleEvent("remove_connection", Json("""{"sid":"s1","last_update":"t6","callsign":"EA5IUE","grid_square":"IM99","version":"v","rx_only":false}"""));
            Assert.Empty(svc.GetStations().Stations);
        }
    }

    [Fact]
    public void BulkUpdate_ReplaysEachEvent()
    {
        var svc = NewService(out var store, out var modem);
        using (store) using (modem) using (svc)
        {
            svc.HandleEvent("bulk_update", Json("""
                [
                  ["new_connection", {"sid":"a","last_update":"t","callsign":"AA1AA","grid_square":"FN42","version":"v","rx_only":true}],
                  ["new_connection", {"sid":"b","last_update":"t","callsign":"BB2BB","grid_square":"JO01","version":"v","rx_only":false}],
                  ["freq_change",    {"sid":"b","last_update":"t","callsign":"BB2BB","grid_square":"JO01","freq":14236000}]
                ]
                """));
            var list = svc.GetStations().Stations;
            Assert.Equal(new[] { "AA1AA", "BB2BB" }, list.Select(s => s.Callsign));
            Assert.True(list[0].RxOnly);
            Assert.Equal(14236000, list[1].FreqHz);
        }
    }

    [Fact]
    public void IncomingQsyRequest_IsExposed_WithAFreshIdEachTime()
    {
        var svc = NewService(out var store, out var modem);
        using (store) using (modem) using (svc)
        {
            Assert.Null(svc.GetStations().IncomingQsy);

            svc.HandleEvent("qsy_request", Json("""{"callsign":"EA5BZY","frequency":7177000,"message":"QSY?"}"""));
            var first = svc.GetStations().IncomingQsy;
            Assert.NotNull(first);
            Assert.Equal("EA5BZY", first!.Callsign);
            Assert.Equal(7177000, first.FreqHz);
            Assert.Equal("QSY?", first.Message);

            svc.HandleEvent("qsy_request", Json("""{"callsign":"EA5BZY","frequency":7177000,"message":""}"""));
            Assert.True(svc.GetStations().IncomingQsy!.Id > first.Id);   // a repeat is a new request

            // Malformed requests don't replace a good one.
            var before = svc.GetStations().IncomingQsy;
            svc.HandleEvent("qsy_request", Json("""{"callsign":"X","frequency":"soon"}"""));
            Assert.Equal(before, svc.GetStations().IncomingQsy);
        }
    }

    [Fact]
    public void Auth_IsViewOnly_UntilOptedInWithCallsignAndGrid()
    {
        var view = JsonDocument.Parse(FreeDvReporterService.BuildAuthJson(
            new FreeDvReporterSettings(true, "EA5IUE", "", ""))).RootElement;
        Assert.Equal("view", view.GetProperty("role").GetString());
        Assert.False(view.TryGetProperty("callsign", out _));   // no personal data in view role
        Assert.Equal(FreeDvReporterService.ProtocolVersion, view.GetProperty("protocol_version").GetInt32());

        var report = JsonDocument.Parse(FreeDvReporterService.BuildAuthJson(
            new FreeDvReporterSettings(true, "EA5IUE", "IM99", "hi"))).RootElement;
        Assert.Equal("report", report.GetProperty("role").GetString());
        Assert.Equal("EA5IUE", report.GetProperty("callsign").GetString());
        Assert.Equal("IM99", report.GetProperty("grid_square").GetString());
        Assert.False(report.GetProperty("rx_only").GetBoolean());
        Assert.StartsWith("ANAN Core", report.GetProperty("version").GetString());
    }

    [Fact]
    public void ModeNames_MatchFreeDvGui()
    {
        Assert.Equal("RADEV1", FreeDvReporterService.ModeName(FreeDvSubmode.RadeV1));
        Assert.Equal("700D", FreeDvReporterService.ModeName(FreeDvSubmode.Mode700D));
        Assert.Equal("1600", FreeDvReporterService.ModeName(FreeDvSubmode.Mode1600));
    }
}
