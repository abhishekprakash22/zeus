// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

using System.Text.Json.Nodes;
using Zeus.Server.Diagnostics;

namespace Zeus.Server.Tests.Diagnostics;

public class SettingsCheckCatalogTests
{
    private static SettingsFacts Facts(string stateJson, Dictionary<string, string>? extra = null, params string[] log) =>
        new(JsonNode.Parse(stateJson), extra ?? new Dictionary<string, string>(), log);

    private static SettingsCheckResult One(IReadOnlyList<SettingsCheckResult> rs, string id, string? titleStartsWith = null) =>
        Assert.Single(rs, r => r.CheckId == id && (titleStartsWith is null || r.Title.StartsWith(titleStartsWith, StringComparison.Ordinal)));

    private const string HealthyRx =
        "{\"status\":\"Connected\",\"endpoint\":\"192.168.1.20\",\"mode\":\"LSB\",\"filterLowHz\":-2900,\"filterHighHz\":-100," +
        "\"agcTopDb\":90,\"autoAgcEnabled\":false,\"rxAfGainDb\":-10,\"attenDb\":0,\"adcOverloadWarning\":false," +
        "\"agc\":{\"mode\":\"Med\"},\"squelch\":{\"enabled\":false,\"level\":0}," +
        "\"receivers\":[{\"index\":0,\"enabled\":true,\"mode\":\"LSB\",\"filterLowHz\":-2900,\"filterHighHz\":-100,\"afGainDb\":-10,\"muted\":false,\"agcTopDb\":90,\"autoAgcEnabled\":false}]}";

    [Fact]
    public void A_healthy_receiver_reports_every_check_as_OK()
    {
        var rs = SettingsCheckCatalog.Evaluate("rx-no-audio", Facts(HealthyRx));
        Assert.NotEmpty(rs);
        Assert.All(rs, r => Assert.Equal(SettingsVerdict.Ok, r.Verdict));
    }

    [Fact]
    public void AGC_T_at_its_floor_is_the_likely_cause_of_quiet_audio()
    {
        // The field case: AGC-T dragged to the bottom (the server clamps to 30).
        var state = HealthyRx.Replace("\"agcTopDb\":90", "\"agcTopDb\":30");
        var rs = SettingsCheckCatalog.Evaluate("rx-no-audio", Facts(state));
        var agc = One(rs, "rx.agcT", "RX1");
        Assert.Equal(SettingsVerdict.Likely, agc.Verdict);
        Assert.Equal("30 dB", agc.Value);
        Assert.Contains("AGC-T", agc.Fix);
        // Likely causes sort first.
        Assert.Equal(SettingsVerdict.Likely, rs[0].Verdict);
    }

    [Fact]
    public void Auto_AGC_T_is_never_blamed()
    {
        var state = HealthyRx.Replace("\"agcTopDb\":90,\"autoAgcEnabled\":false}", "\"agcTopDb\":30,\"autoAgcEnabled\":true}");
        var agc = One(SettingsCheckCatalog.Evaluate("rx-no-audio", Facts(state)), "rx.agcT", "RX1");
        Assert.Equal(SettingsVerdict.Ok, agc.Verdict);
    }

    [Fact]
    public void Each_receiver_is_checked_on_its_own()
    {
        var state = HealthyRx.Replace("}]}",
            "},{\"index\":1,\"enabled\":true,\"mode\":\"USB\",\"filterLowHz\":100,\"filterHighHz\":2900,\"afGainDb\":-10,\"muted\":true,\"agcTopDb\":90,\"autoAgcEnabled\":false}]}");
        var rs = SettingsCheckCatalog.Evaluate("rx-no-audio", Facts(state));
        Assert.Equal(SettingsVerdict.Ok, One(rs, "rx.muted", "RX1").Verdict);
        Assert.Equal(SettingsVerdict.Likely, One(rs, "rx.muted", "RX2").Verdict);
    }

    [Fact]
    public void Low_output_points_at_drive_cap_and_mic()
    {
        const string tx = "{\"mode\":\"USB\",\"drivePct\":5,\"driveMaxPct\":40,\"tunePct\":10,\"micGainDb\":-30,\"txFilterLowHz\":100,\"txFilterHighHz\":2900}";
        var rs = SettingsCheckCatalog.Evaluate("no-tx-power", Facts(tx));
        Assert.Equal(SettingsVerdict.Likely, One(rs, "tx.drive").Verdict);
        Assert.Equal(SettingsVerdict.Likely, One(rs, "tx.driveCap").Verdict);
        Assert.Equal(SettingsVerdict.Likely, One(rs, "tx.mic").Verdict);
        Assert.Equal(SettingsVerdict.Ok, One(rs, "tx.filter").Verdict);
    }

    [Fact]
    public void Mic_gain_is_not_judged_in_CW_or_digital_modes()
    {
        var rs = SettingsCheckCatalog.Evaluate("no-tx-power", Facts("{\"mode\":\"CWU\",\"micGainDb\":-40,\"drivePct\":80}"));
        Assert.DoesNotContain(rs, r => r.CheckId == "tx.mic");
    }

    [Fact]
    public void A_hot_PureSignal_feedback_without_auto_attenuate_is_the_likely_cause()
    {
        var rs = SettingsCheckCatalog.Evaluate("ps-not-working",
            Facts("{\"psEnabled\":true,\"psCalibrationStalled\":true,\"psFeedbackLevel\":214,\"psAutoAttenuate\":false}"));
        Assert.Equal(SettingsVerdict.Likely, One(rs, "ps.feedback").Verdict);
        Assert.Equal(SettingsVerdict.Likely, One(rs, "ps.stalled").Verdict);
    }

    [Fact]
    public void Connection_and_p2app_problems_are_named()
    {
        var extra = new Dictionary<string, string> { ["p2app.mode"] = "Paused" };
        var rs = SettingsCheckCatalog.Evaluate("wont-connect",
            Facts("{\"status\":\"Disconnected\"}", extra, "kestrel: address already in use"));
        Assert.Equal(SettingsVerdict.Likely, One(rs, "conn.status").Verdict);
        Assert.Equal("stopped", One(rs, "conn.p2app").Value);
        Assert.Equal(SettingsVerdict.Likely, One(rs, "conn.portInUse").Verdict);
    }

    [Fact]
    public void Remote_setup_password_callsign_and_claim()
    {
        var extra = new Dictionary<string, string> { ["remote.passwordSet"] = "false", ["remote.callsignSet"] = "true", ["remote.callsign"] = "VK2KKK" };
        var rs = SettingsCheckCatalog.Evaluate("remote-cant-connect", Facts("{}", extra,
            "remote broker: connecting to wss://x as VK2KKK",
            "remote broker refused VK2KKK: the callsign is claimed by another radio's host key."));
        Assert.Equal(SettingsVerdict.Likely, One(rs, "remote.password").Verdict);
        Assert.Equal(SettingsVerdict.Ok, One(rs, "remote.callsign").Verdict);
        Assert.Equal(SettingsVerdict.Likely, One(rs, "remote.claimed").Verdict);
    }

    [Fact]
    public void A_broker_that_came_back_online_is_not_blamed()
    {
        var rs = SettingsCheckCatalog.Evaluate("remote-cant-connect", Facts("{}", null,
            "remote broker connection ended; retrying in 00:00:05",
            "remote broker: host online for VK2KKK"));
        Assert.Equal(SettingsVerdict.Ok, One(rs, "remote.broker").Verdict);
    }

    [Fact]
    public void Digital_modes_need_a_synced_clock_and_a_wide_filter()
    {
        var extra = new Dictionary<string, string> { ["host.clockSynced"] = "false" };
        var state = "{\"mode\":\"DIGU\",\"drivePct\":50,\"receivers\":[{\"index\":0,\"enabled\":true,\"mode\":\"DIGU\",\"filterLowHz\":100,\"filterHighHz\":1600}]}";
        var rs = SettingsCheckCatalog.Evaluate("digital-modes", Facts(state, extra));
        Assert.Equal(SettingsVerdict.Likely, One(rs, "digital.clock").Verdict);
        Assert.Equal(SettingsVerdict.Check, One(rs, "rx.filter").Verdict);
        Assert.Equal(SettingsVerdict.Ok, One(rs, "digital.mode").Verdict);
    }

    [Fact]
    public void Host_checks_apply_to_every_symptom()
    {
        var extra = new Dictionary<string, string> { ["host.underVoltage"] = "true", ["host.cpuTempC"] = "83", ["host.diskFreeMb"] = "300" };
        foreach (var symptom in new[] { "rx-no-audio", "wont-connect", "remote-poor" })
        {
            var rs = SettingsCheckCatalog.Evaluate(symptom, Facts("{}", extra));
            Assert.Equal(SettingsVerdict.Likely, One(rs, "host.power").Verdict);
            Assert.Equal(SettingsVerdict.Likely, One(rs, "host.thermal").Verdict);
            Assert.Equal(SettingsVerdict.Likely, One(rs, "host.disk").Verdict);
        }
    }

    [Fact]
    public void Missing_facts_skip_checks_instead_of_failing()
    {
        var rs = SettingsCheckCatalog.Evaluate(null, Facts("{}"));
        Assert.NotNull(rs);
        Assert.DoesNotContain(rs, r => r.CheckId == "rx.agcT" || r.CheckId == "tx.drive");
    }

    [Fact]
    public void Facts_read_numbers_from_typed_nodes_too()
    {
        // A state node built in code (JsonValue<int>, not parsed text) must read
        // as a number, not throw — the same trap that broke get_recent_log.
        var state = new JsonObject { ["drivePct"] = 5, ["driveMaxPct"] = 100L, ["micGainDb"] = -3.5 };
        var f = new SettingsFacts(state, new Dictionary<string, string>(), Array.Empty<string>());
        Assert.Equal(5, f.Num("drivePct"));
        Assert.Equal(100, f.Num("driveMaxPct"));
        Assert.Equal(-3.5, f.Num("micGainDb"));
        var drive = One(SettingsCheckCatalog.Evaluate("no-tx-power", f), "tx.drive");
        Assert.Equal(SettingsVerdict.Likely, drive.Verdict);
    }

    [Theory]
    [InlineData("0x50005", "true", "true")]
    [InlineData("throttled=0x0", "false", "false")]
    [InlineData("0x10000", "true", "false")]
    [InlineData("junk", null, null)]
    public void Pi_throttle_flags_parse(string raw, string? underVoltage, string? throttled)
    {
        var d = HostFacts.ParseThrottled(raw);
        Assert.Equal(underVoltage, d.TryGetValue("host.underVoltage", out var u) ? u : null);
        Assert.Equal(throttled, d.TryGetValue("host.throttled", out var t) ? t : null);
    }

    [Fact]
    public void Report_section_and_findings_carry_the_fix()
    {
        var rs = SettingsCheckCatalog.Evaluate("rx-no-audio", Facts(HealthyRx.Replace("\"muted\":false", "\"muted\":true")));
        var section = SettingsCheckService.ToSection(rs);
        Assert.Equal("settings-check", section.Id);
        Assert.Contains(section.Items, i => i.Key == "RX1 mute" && i.Value.StartsWith("LIKELY CAUSE", StringComparison.Ordinal));
        var finding = Assert.Single(SettingsCheckService.ToFindings(rs));
        Assert.Contains("MUTE off", finding.Detail);
        Assert.Contains("LIKELY CAUSE", SettingsCheckService.ToText(rs));
    }

    [Fact]
    public void Every_check_id_is_unique_and_every_symptom_is_a_registered_one()
    {
        var ids = SettingsCheckCatalog.All.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        var registered = new SymptomRegistry().All.Select(s => s.Id).ToHashSet();
        foreach (var c in SettingsCheckCatalog.All)
        {
            foreach (var s in c.Symptoms) Assert.Contains(s, registered);
        }
    }
}
