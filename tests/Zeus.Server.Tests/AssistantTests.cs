// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

using System.Text.Json.Nodes;
using Zeus.Server.Assistant;
using Zeus.Server.Hosting.Remote;

namespace Zeus.Server.Tests;

public class AssistantManualIndexTests
{
    private const string Chapter =
        "# Display effects\n\nIntro text about effects.\n\n" +
        "## Phosphor persistence\n\nSteady carriers burn in; bursts glow and fade. The noise floor leaves no glow.\n\n" +
        "## Passband spotlight\n\nThe waterfall dims outside the receive filter.\n";

    [Fact]
    public void Splits_on_headings_and_names_the_chapter_from_its_title()
    {
        var sections = ManualIndex.SplitSections("07-display.md", Chapter).ToList();
        Assert.Equal(3, sections.Count);
        Assert.All(sections, s => Assert.Equal("Display effects", s.Chapter));
        Assert.Equal("Phosphor persistence", sections[1].Heading);
        Assert.Equal("Display effects › Phosphor persistence", sections[1].Citation);
    }

    [Fact]
    public void Search_ranks_the_section_whose_heading_matches()
    {
        var index = new ManualIndex(new[] { ("07-display.md", Chapter) });
        var hits = index.Search("why does the phosphor glow?");
        Assert.NotEmpty(hits);
        Assert.Equal("Phosphor persistence", hits[0].Section.Heading);
        Assert.Empty(index.Search("zzzz qqqq"));
        Assert.Empty(index.Search("the and for"));
    }

    [Fact]
    public void The_real_manual_is_embedded_and_searchable()
    {
        var index = ManualIndex.LoadEmbedded();
        Assert.True(index.Count > 30, $"only {index.Count} manual sections embedded");
        Assert.NotEmpty(index.Search("phosphor persistence"));
        Assert.NotEmpty(index.Search("PureSignal"));
    }
}

public class AssistantToolsTests
{
    private static AssistantTools Make(List<string>? log = null) => new(
        new ManualIndex(new[] { ("a.md", "# AGC\n\n## AGC-T\n\nAGC threshold sets where the AGC starts working.\n") }),
        () => "{\"vfoHz\":7188000}",
        id => "diagnosed:" + id,
        n => (log ?? new List<string> { "ps.arm ok", "wdsp.setPsMox true", "calcc refused: over-drive" }).TakeLast(n).ToList(),
        id => "[LIKELY CAUSE] checked:" + id);

    [Fact]
    public void The_tool_set_is_closed_and_read_only()
    {
        var names = AssistantTools.Definitions().Select(d => d.Name).ToArray();
        Assert.Equal(AssistantTools.ReadOnlyToolNames, names);
        // Nothing that could change a setting or transmit, by name.
        foreach (var n in names)
        {
            Assert.DoesNotMatch("^(set|write|apply|tx|mox|tune|key|transmit|update|delete|post)(_|$)", n);
        }
    }

    [Fact]
    public void Unknown_tools_are_refused_not_run()
    {
        var r = Make().Execute("set_mox", new JsonObject { ["on"] = true });
        Assert.Contains("refused", r.Summary);
        Assert.Contains("Only read-only tools", r.Content);
    }

    [Fact]
    public void Search_manual_returns_sections_with_citations()
    {
        var r = Make().Execute(AssistantTools.SearchManual, new JsonObject { ["query"] = "AGC-T threshold" });
        Assert.Contains("AGC › AGC-T", r.Citations);
        Assert.Contains("threshold", r.Content);
    }

    [Fact]
    public void State_diagnostic_and_log_tools_read_through_their_sources()
    {
        var t = Make();
        Assert.Contains("7188000", t.Execute(AssistantTools.GetRadioState, null).Content);
        Assert.Equal("diagnosed:ps-not-working",
            t.Execute(AssistantTools.RunDiagnostic, new JsonObject { ["symptom_id"] = "ps-not-working" }).Content);
        // An unknown symptom falls back to "other" instead of failing.
        Assert.Equal("diagnosed:other",
            t.Execute(AssistantTools.RunDiagnostic, new JsonObject { ["symptom_id"] = "explode" }).Content);
        var log = t.Execute(AssistantTools.GetRecentLog, new JsonObject { ["lines"] = 50, ["contains"] = "CALCC" });
        Assert.Equal("calcc refused: over-drive", log.Content);
    }

    [Fact]
    public void Check_settings_runs_the_catalog_for_a_validated_symptom()
    {
        var t = Make();
        Assert.Equal("[LIKELY CAUSE] checked:no-tx-power",
            t.Execute(AssistantTools.CheckSettings, new JsonObject { ["symptom_id"] = "no-tx-power" }).Content);
        Assert.Equal("[LIKELY CAUSE] checked:other",
            t.Execute(AssistantTools.CheckSettings, new JsonObject { ["symptom_id"] = "set-mox" }).Content);
    }

    [Fact]
    public void Results_are_capped()
    {
        var big = new string('x', AssistantTools.MaxResultChars * 2);
        Assert.True(AssistantTools.Cap(big).Length < AssistantTools.MaxResultChars + 32);
    }
}

public class AssistantProviderTests
{
    private static readonly IReadOnlyList<AssistantToolDef> Defs = AssistantTools.Definitions();

    [Fact]
    public void Anthropic_request_carries_system_tools_and_history()
    {
        var p = new AnthropicProvider();
        var history = AssistantService.BuildHistory(p, new[] { new AssistantTurn("user", "hi"), new AssistantTurn("assistant", "hello") }, "why no audio?");
        var body = p.BuildRequest("claude-sonnet-5-5", "SYS", history, Defs);
        Assert.Equal("SYS", body["system"]!.GetValue<string>());
        Assert.Equal(3, body["messages"]!.AsArray().Count);
        Assert.Equal("search_manual", body["tools"]![0]!["name"]!.GetValue<string>());
        Assert.NotNull(body["tools"]![0]!["input_schema"]);
        Assert.Equal("https://api.anthropic.com/v1/messages", p.Endpoint("https://api.anthropic.com/"));
    }

    [Fact]
    public void Anthropic_reply_with_tool_use_round_trips_into_history()
    {
        var p = new AnthropicProvider();
        var reply = p.ParseReply(JsonNode.Parse(
            "{\"content\":[{\"type\":\"text\",\"text\":\"Let me look.\"}," +
            "{\"type\":\"tool_use\",\"id\":\"tu_1\",\"name\":\"get_radio_state\",\"input\":{}}]}")!);
        Assert.Equal("Let me look.", reply.Text);
        Assert.Single(reply.Calls);
        Assert.Equal("tu_1", reply.Calls[0].Id);
        var history = new JsonArray();
        p.AppendToolResults(history, reply, new[] { (reply.Calls[0], "{\"vfoHz\":1}") });
        Assert.Equal("assistant", history[0]!["role"]!.GetValue<string>());
        Assert.Equal("tool_result", history[1]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("tu_1", history[1]!["content"]![0]!["tool_use_id"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAi_compatible_request_and_tool_calls()
    {
        var p = new OpenAiCompatProvider();
        var body = p.BuildRequest("llama3.1", "SYS", AssistantService.BuildHistory(p, null, "q"), Defs);
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("function", body["tools"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("http://pc:11434/v1/chat/completions", p.Endpoint("http://pc:11434/v1"));

        var reply = p.ParseReply(JsonNode.Parse(
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[" +
            "{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"search_manual\",\"arguments\":\"{\\\"query\\\":\\\"agc\\\"}\"}}]}}]}")!);
        Assert.Equal("", reply.Text);
        Assert.Equal("search_manual", reply.Calls[0].Name);
        Assert.Equal("agc", reply.Calls[0].Input!["query"]!.GetValue<string>());
        var history = new JsonArray();
        p.AppendToolResults(history, reply, new[] { (reply.Calls[0], "result") });
        Assert.Equal("tool", history[1]!["role"]!.GetValue<string>());
        Assert.Equal("c1", history[1]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public void History_is_capped_to_recent_turns()
    {
        var turns = Enumerable.Range(0, 20).Select(i => new AssistantTurn(i % 2 == 0 ? "user" : "assistant", "t" + i)).ToList();
        var h = AssistantService.BuildHistory(new AnthropicProvider(), turns, "now");
        Assert.Equal(AssistantService.MaxHistoryTurns + 1, h.Count);
    }

    [Theory]
    [InlineData(401, "API key")]
    [InlineData(404, "model name")]
    [InlineData(429, "rate-limiting")]
    [InlineData(503, "error")]
    public void Http_failures_are_explained_in_operator_terms(int status, string expect)
    {
        Assert.Contains(expect, AssistantService.ExplainHttp(status));
    }
}

public class AssistantSettingsTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "anan-assistant-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Off_by_default_and_the_key_is_never_shown()
    {
        var store = new AssistantSettingsStore(TempPath());
        Assert.False(store.Get().Enabled);
        var s = store.Apply(new AssistantSettingsUpdate(true, null, null, null, "sk-ant-abcdef123456", null));
        var dto = AssistantSettingsStore.ToDto(s);
        Assert.True(dto.HasApiKey);
        Assert.Equal("…3456", dto.ApiKeyHint);
        Assert.DoesNotContain("abcdef", System.Text.Json.JsonSerializer.Serialize(dto));
    }

    [Fact]
    public void Persists_and_keeps_the_key_across_a_restart_until_cleared()
    {
        var path = TempPath();
        new AssistantSettingsStore(path).Apply(new AssistantSettingsUpdate(true, null, null, null, "k-1234567890", null));
        var reopened = new AssistantSettingsStore(path);
        Assert.Equal("k-1234567890", reopened.Get().ApiKey);
        // An empty key in an update leaves the stored one alone…
        reopened.Apply(new AssistantSettingsUpdate(null, null, null, "m", "", null));
        Assert.Equal("k-1234567890", reopened.Get().ApiKey);
        // …only an explicit clear removes it.
        reopened.Apply(new AssistantSettingsUpdate(null, null, null, null, null, true));
        Assert.Null(reopened.Get().ApiKey);
    }

    [Fact]
    public void Switching_provider_resets_endpoint_and_model_and_bad_urls_fall_back()
    {
        var store = new AssistantSettingsStore(TempPath());
        var s = store.Apply(new AssistantSettingsUpdate(null, "openai", null, null, null, null));
        Assert.Equal(AssistantSettingsStore.DefaultOpenAiBaseUrl, s.BaseUrl);
        Assert.Equal(AssistantSettingsStore.DefaultOpenAiModel, s.Model);
        s = store.Apply(new AssistantSettingsUpdate(null, null, "ftp://nope", null, null, null));
        Assert.Equal(AssistantSettingsStore.DefaultOpenAiBaseUrl, s.BaseUrl);
        s = store.Apply(new AssistantSettingsUpdate(null, "ANTHROPIC", null, null, null, null));
        Assert.Equal(AssistantSettingsStore.DefaultAnthropicBaseUrl, s.BaseUrl);
    }

    [Fact]
    public void Remote_sessions_may_ask_but_never_change_the_key()
    {
        Assert.True(RemoteWebRtcSession.IsDenied("/api/assistant/settings", mutating: true));
        Assert.False(RemoteWebRtcSession.IsDenied("/api/assistant/settings", mutating: false));
        Assert.False(RemoteWebRtcSession.IsDenied("/api/assistant/ask", mutating: true));
    }
}

public class AssistantRemoteTunnelTests
{
    [Theory]
    [InlineData("/api/assistant/ask", true)]
    [InlineData("/API/ASSISTANT/ASK", true)]
    [InlineData("/api/assistant/settings", false)]
    [InlineData("/api/state", false)]
    public void Only_the_assistant_ask_gets_the_long_loopback_deadline(string path, bool slow)
    {
        Assert.Equal(slow, RemoteWebRtcSession.IsSlowApiPath(path));
    }
}
