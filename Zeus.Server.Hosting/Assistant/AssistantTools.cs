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
// The assistant's tools — PHASE 1 IS READ-ONLY BY CONSTRUCTION. There is no
// tool here that changes a setting, keys the transmitter or touches the
// network beyond the model call itself; a model can only ask to READ. The
// set is closed: an unknown tool name is refused, and a test pins the list.
// Every result is size-capped so one call cannot flood the context.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zeus.Server.Assistant;

public sealed record AssistantToolDef(string Name, string Description, JsonObject Schema);

public sealed record AssistantToolResult(string Name, string Summary, string Content, IReadOnlyList<string> Citations);

public sealed class AssistantTools
{
    public const string SearchManual = "search_manual";
    public const string GetRadioState = "get_radio_state";
    public const string RunDiagnostic = "run_diagnostic";
    public const string GetRecentLog = "get_recent_log";
    public const string CheckSettings = "check_settings";

    /// <summary>The complete, closed tool set. Nothing outside it can run.</summary>
    public static readonly IReadOnlyList<string> ReadOnlyToolNames =
        new[] { SearchManual, CheckSettings, GetRadioState, RunDiagnostic, GetRecentLog };

    public static readonly IReadOnlyList<string> SymptomIds = new[]
    {
        "wont-connect", "no-tx-power", "ps-not-working", "tx-audio",
        "rx-no-audio", "rx-audio-quality", "ui-display", "rx-weak",
        "digital-modes", "remote-cant-connect", "remote-poor", "other",
    };

    public const int MaxResultChars = 12_000;

    private readonly ManualIndex _manual;
    private readonly Func<string> _stateJson;
    private readonly Func<string, string> _diagnose;
    private readonly Func<int, IReadOnlyList<string>> _logTail;
    private readonly Func<string, string> _checkSettings;

    public AssistantTools(
        ManualIndex manual,
        Func<string> stateJson,
        Func<string, string> diagnose,
        Func<int, IReadOnlyList<string>> logTail,
        Func<string, string>? checkSettings = null)
    {
        _manual = manual;
        _stateJson = stateJson;
        _diagnose = diagnose;
        _logTail = logTail;
        _checkSettings = checkSettings ?? (_ => "Settings checks are not available on this radio.");
    }

    public static IReadOnlyList<AssistantToolDef> Definitions() => new[]
    {
        new AssistantToolDef(
            SearchManual,
            "Search the ANAN Core operator manual. Returns the best-matching sections with their " +
            "chapter and heading. Use it for any question about what a control does or how to do something, " +
            "and cite the section you used.",
            Schema(new JsonObject
            {
                ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Keywords or a short question." },
            }, "query")),
        new AssistantToolDef(
            CheckSettings,
            "Check the radio's settings against the common causes of a problem (e.g. drive, mic gain, AGC-T, mute, " +
            "attenuator, filters, PureSignal feedback, connection, remote setup, clock sync, power and temperature). " +
            "Returns each relevant setting with its value and a verdict (LIKELY CAUSE / CHECK / OK) plus the fix. " +
            "Call this FIRST for any 'something is wrong' question. Read-only.",
            Schema(new JsonObject
            {
                ["symptom_id"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(SymptomIds.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                },
            }, "symptom_id")),
        new AssistantToolDef(
            GetRadioState,
            "Read the radio's current state: frequencies, modes, filters, receivers, AF/AGC/NR, mute, TX and " +
            "PureSignal status, connection. Read-only.",
            Schema(new JsonObject(), null)),
        new AssistantToolDef(
            RunDiagnostic,
            "Run ANAN Core's built-in diagnostic probes and known-issue rules for a symptom and return the " +
            "findings (includes a redacted log tail). Read-only.",
            Schema(new JsonObject
            {
                ["symptom_id"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(SymptomIds.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                },
            }, "symptom_id")),
        new AssistantToolDef(
            GetRecentLog,
            "Read the most recent application log lines (already redacted), optionally only those containing " +
            "a word. Read-only.",
            Schema(new JsonObject
            {
                ["lines"] = new JsonObject { ["type"] = "integer", ["minimum"] = 10, ["maximum"] = 200 },
                ["contains"] = new JsonObject { ["type"] = "string" },
            }, null)),
    };

    /// <summary>Run one tool call. Unknown names and bad input come back as an error text, never an exception.</summary>
    public AssistantToolResult Execute(string name, JsonNode? input)
    {
        try
        {
            switch (name)
            {
                case SearchManual:
                {
                    var query = Str(input, "query") ?? "";
                    var hits = _manual.Search(query, 3);
                    if (hits.Count == 0)
                    {
                        return new AssistantToolResult(name, $"search_manual(\"{Short(query)}\") → no match",
                            "No manual section matched. Say so rather than guessing.", Array.Empty<string>());
                    }
                    var arr = new JsonArray();
                    foreach (var h in hits)
                    {
                        arr.Add(new JsonObject
                        {
                            ["citation"] = h.Section.Citation,
                            ["text"] = h.Section.Text,
                        });
                    }
                    return new AssistantToolResult(name, $"search_manual(\"{Short(query)}\")",
                        Cap(arr.ToJsonString()), hits.Select(h => h.Section.Citation).ToList());
                }
                case GetRadioState:
                    return new AssistantToolResult(name, "get_radio_state()", Cap(_stateJson()), Array.Empty<string>());
                case CheckSettings:
                {
                    var id = Str(input, "symptom_id") ?? "other";
                    if (!SymptomIds.Contains(id)) id = "other";
                    return new AssistantToolResult(name, $"check_settings({id})", Cap(_checkSettings(id)), Array.Empty<string>());
                }
                case RunDiagnostic:
                {
                    var id = Str(input, "symptom_id") ?? "other";
                    if (!SymptomIds.Contains(id)) id = "other";
                    return new AssistantToolResult(name, $"run_diagnostic({id})", Cap(_diagnose(id)), Array.Empty<string>());
                }
                case GetRecentLog:
                {
                    var lines = Math.Clamp(Int(input, "lines") ?? 60, 10, 200);
                    var contains = Str(input, "contains");
                    IEnumerable<string> tail = _logTail(lines);
                    if (!string.IsNullOrWhiteSpace(contains))
                    {
                        string needle = contains;
                        tail = tail.Where(l => l.Contains(needle, StringComparison.OrdinalIgnoreCase));
                    }
                    var label = string.IsNullOrWhiteSpace(contains)
                        ? $"get_recent_log({lines})"
                        : $"get_recent_log({lines}, \"{Short(contains)}\")";
                    return new AssistantToolResult(name, label, Cap(string.Join('\n', tail)), Array.Empty<string>());
                }
                default:
                    return new AssistantToolResult(name, $"{Short(name)} — refused",
                        "Unknown tool. Only read-only tools are available.", Array.Empty<string>());
            }
        }
        catch (Exception ex)
        {
            return new AssistantToolResult(name, $"{Short(name)} — failed",
                "The tool failed: " + ex.GetType().Name, Array.Empty<string>());
        }
    }

    public static string Cap(string s) =>
        s.Length <= MaxResultChars ? s : s[..MaxResultChars] + "\n… (truncated)";

    private static string Short(string s) => s.Length <= 40 ? s : s[..40] + "…";

    private static JsonObject Schema(JsonObject props, string? required)
    {
        var o = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required is not null) o["required"] = new JsonArray(JsonValue.Create(required));
        return o;
    }

    private static string? Str(JsonNode? input, string key)
    {
        if (input is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is null) return null;
        return v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
    }

    private static int? Int(JsonNode? input, string key)
    {
        if (input is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is null) return null;
        // Read through the JSON text: GetValue<double>() only converts when the
        // node came from parsed JSON — a node built in code (JsonValue<int>)
        // throws InvalidOperationException. ToJsonString() works for both.
        var text = v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            ? (int)Math.Round(Math.Clamp(d, int.MinValue, int.MaxValue))
            : null;
    }
}
