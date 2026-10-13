// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// AssistantService — phase 1 of the ANAN Core assistant: answers questions
// from the operator manual and the radio's live state, and diagnoses
// problems with the built-in probes. It runs on the radio's backend so the
// API key never reaches a browser, and it only ever READS (AssistantTools is
// a closed, read-only set). One question at a time; a bounded tool loop.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Zeus.Server.Diagnostics;

namespace Zeus.Server.Assistant;

public sealed record AssistantTurn(string Role, string Text);

public sealed record AssistantAskRequest(string? Question, IReadOnlyList<AssistantTurn>? History);

public sealed record AssistantTraceEntry(string Tool, string Summary);

public sealed record AssistantAnswer(
    string Text,
    IReadOnlyList<AssistantTraceEntry> Trace,
    IReadOnlyList<string> Citations,
    string? Error);

public sealed class AssistantService
{
    public const string HttpClientName = "Assistant";
    public const int MaxRounds = 6;
    public const int MaxQuestionChars = 2000;
    public const int MaxHistoryTurns = 6;

    public const string SystemPrompt =
        "You are the built-in assistant of ANAN Core, the operating software of an Apache Labs ANAN " +
        "software-defined amateur radio transceiver. You help the operator understand and troubleshoot the radio.\n\n" +
        "Rules:\n" +
        "- You can only READ: the operator manual (search_manual), the radio's state (get_radio_state), the " +
        "built-in diagnostics (run_diagnostic) and the recent log (get_recent_log). You cannot change any " +
        "setting and you cannot transmit. Never claim you changed something. Tell the operator exactly what " +
        "to tap or set instead, using the control names they see on screen.\n" +
        "- Never tell the operator to bypass a protection (SWR protection, amplifier trips, PureSignal " +
        "over-drive refusal) or to exceed licensed power or band limits.\n" +
        "- For questions about controls, settings or procedures, call search_manual first and answer from it. " +
        "Name the section you used. If the manual has nothing, say so instead of guessing.\n" +
        "- For 'something is wrong' questions, call check_settings with the matching symptom FIRST, then the " +
        "diagnostic or state if you need more. Lead with any LIKELY CAUSE it reports and give its fix. " +
        "Report what you found as facts with the actual values, then the likely cause, then the fix.\n" +
        "- Keep answers short: they are read on an 8-inch screen. Lead with the answer. Plain text, short " +
        "paragraphs, no tables.\n" +
        "- The operator is a licensed radio amateur; you may use normal ham terminology.";

    private static readonly JsonSerializerOptions StateJson = CreateStateJson();

    private readonly AssistantSettingsStore _settings;
    private readonly ManualIndex _manual;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AssistantService> _log;
    private readonly AssistantTools _tools;
    private readonly SemaphoreSlim _one = new(1, 1);

    public AssistantService(
        AssistantSettingsStore settings,
        ManualIndex manual,
        RadioService radio,
        DiagnosticReportBuilder diagnostics,
        DiagnosticLogBuffer logBuffer,
        SettingsCheckService settingsChecks,
        IHttpClientFactory http,
        ILogger<AssistantService> log)
    {
        _settings = settings;
        _manual = manual;
        _http = http;
        _log = log;
        _tools = new AssistantTools(
            manual,
            () => JsonSerializer.Serialize(radio.Snapshot(), StateJson),
            id => diagnostics.Build(new DiagnosticRequest(id, null)).Markdown,
            n => logBuffer.Snapshot(n),
            id => SettingsCheckService.ToText(settingsChecks.Run(id)));
    }

    public int ManualSections => _manual.Count;

    public async Task<AssistantAnswer> AskAsync(AssistantAskRequest req, CancellationToken ct)
    {
        var s = _settings.Get();
        if (!s.Enabled)
        {
            return Fail("The assistant is off. Turn it on in Settings → Assistant.");
        }
        if (s.Provider == AssistantSettingsStore.ProviderAnthropic && string.IsNullOrEmpty(s.ApiKey))
        {
            return Fail("No API key is set. Add one in Settings → Assistant.");
        }
        var question = (req.Question ?? "").Trim();
        if (question.Length == 0) return Fail("Ask a question first.");
        if (question.Length > MaxQuestionChars) question = question[..MaxQuestionChars];

        if (!await _one.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return Fail("The assistant is still answering the previous question.");
        }
        try
        {
            return await RunAsync(s, question, req.History, ct).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<AssistantAnswer> RunAsync(
        AssistantSettings s, string question, IReadOnlyList<AssistantTurn>? prior, CancellationToken ct)
    {
        var provider = AssistantProviderFactory.For(s.Provider);
        var history = BuildHistory(provider, prior, question);
        var defs = AssistantTools.Definitions();
        var trace = new List<AssistantTraceEntry>();
        var citations = new List<string>();
        var client = _http.CreateClient(HttpClientName);

        for (var round = 0; round < MaxRounds; round++)
        {
            var body = provider.BuildRequest(s.Model, SystemPrompt, history, defs);
            using var msg = new HttpRequestMessage(HttpMethod.Post, provider.Endpoint(s.BaseUrl))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            provider.ApplyHeaders(msg, s.ApiKey);

            JsonNode? json;
            try
            {
                using var resp = await client.SendAsync(msg, ct).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogWarning("assistant.ask provider={Provider} http={Status}", s.Provider, (int)resp.StatusCode);
                    return new AssistantAnswer("", trace, citations, ExplainHttp((int)resp.StatusCode));
                }
                json = JsonNode.Parse(text);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new AssistantAnswer("", trace, citations, "The model didn't answer in time. Try again.");
            }
            catch (HttpRequestException)
            {
                return new AssistantAnswer("", trace, citations,
                    "Couldn't reach the model server. Check the address in Settings → Assistant and the radio's internet or LAN.");
            }
            catch (JsonException)
            {
                return new AssistantAnswer("", trace, citations, "The model server sent a reply ANAN Core couldn't read.");
            }
            if (json is null) return new AssistantAnswer("", trace, citations, "The model server sent an empty reply.");

            var reply = provider.ParseReply(json);
            if (reply.Calls.Count == 0)
            {
                _log.LogInformation("assistant.ask provider={Provider} rounds={Rounds} tools={Tools}",
                    s.Provider, round + 1, trace.Count);
                var answer = reply.Text.Length > 0 ? reply.Text : "(The model returned no text.)";
                return new AssistantAnswer(answer, trace, citations.Distinct().ToList(), null);
            }

            var results = new List<(AssistantToolCall, string)>();
            foreach (var call in reply.Calls)
            {
                var r = _tools.Execute(call.Name, call.Input);
                trace.Add(new AssistantTraceEntry(r.Name, r.Summary));
                citations.AddRange(r.Citations);
                results.Add((call, r.Content));
            }
            provider.AppendToolResults(history, reply, results);
        }

        return new AssistantAnswer("", trace, citations.Distinct().ToList(),
            "The question needed more lookups than allowed. Try asking something more specific.");
    }

    /// <summary>Prior turns (text only, capped) followed by the new question.</summary>
    public static JsonArray BuildHistory(IAssistantProvider provider, IReadOnlyList<AssistantTurn>? prior, string question)
    {
        var history = new JsonArray();
        if (prior is not null)
        {
            foreach (var t in prior.TakeLast(MaxHistoryTurns))
            {
                var text = (t.Text ?? "").Trim();
                if (text.Length == 0) continue;
                if (text.Length > 4000) text = text[..4000];
                history.Add(string.Equals(t.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? provider.AssistantText(text)
                    : provider.UserMessage(text));
            }
        }
        history.Add(provider.UserMessage(question));
        return history;
    }

    public static string ExplainHttp(int status) => status switch
    {
        401 or 403 => "The model server refused the API key. Check it in Settings → Assistant.",
        404 => "The model or endpoint wasn't found. Check the model name and address in Settings → Assistant.",
        429 => "The model service is rate-limiting this key. Wait a moment and try again.",
        >= 500 => "The model service had an error. Try again shortly.",
        _ => $"The model server answered with HTTP {status}.",
    };

    private static AssistantAnswer Fail(string why) =>
        new("", Array.Empty<AssistantTraceEntry>(), Array.Empty<string>(), why);

    private static JsonSerializerOptions CreateStateJson()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}
