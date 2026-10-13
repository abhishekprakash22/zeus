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
// Provider adapters — request shaping and reply parsing for the two wire
// formats the assistant speaks. Pure functions over JSON, so they are unit
// tested without a network; AssistantService owns the HTTP and the loop.
//
//   Anthropic Messages API:  POST {base}/v1/messages
//   OpenAI-compatible:       POST {base}/chat/completions   (base ends in /v1)

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zeus.Server.Assistant;

public sealed record AssistantToolCall(string Id, string Name, JsonNode? Input);

/// <summary>One model turn: its text, any tool calls, and the message to append to history.</summary>
public sealed record ProviderReply(string Text, IReadOnlyList<AssistantToolCall> Calls, JsonNode AssistantMessage);

public interface IAssistantProvider
{
    string Endpoint(string baseUrl);
    void ApplyHeaders(HttpRequestMessage req, string? apiKey);
    JsonObject BuildRequest(string model, string system, JsonArray history, IReadOnlyList<AssistantToolDef> tools);
    ProviderReply ParseReply(JsonNode response);
    void AppendToolResults(JsonArray history, ProviderReply reply, IReadOnlyList<(AssistantToolCall Call, string Result)> results);
    JsonNode UserMessage(string text);
    JsonNode AssistantText(string text);
}

public static class AssistantProviderFactory
{
    public static IAssistantProvider For(string provider) =>
        provider == AssistantSettingsStore.ProviderOpenAi ? new OpenAiCompatProvider() : new AnthropicProvider();
}

public sealed class AnthropicProvider : IAssistantProvider
{
    public const string ApiVersion = "2023-06-01";
    public const int MaxTokens = 1024;

    public string Endpoint(string baseUrl) => baseUrl.TrimEnd('/') + "/v1/messages";

    public void ApplyHeaders(HttpRequestMessage req, string? apiKey)
    {
        if (!string.IsNullOrEmpty(apiKey)) req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
    }

    public JsonObject BuildRequest(string model, string system, JsonArray history, IReadOnlyList<AssistantToolDef> tools)
    {
        var t = new JsonArray();
        foreach (var d in tools)
        {
            t.Add(new JsonObject
            {
                ["name"] = d.Name,
                ["description"] = d.Description,
                ["input_schema"] = d.Schema.DeepClone(),
            });
        }
        return new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = MaxTokens,
            ["system"] = system,
            ["messages"] = history.DeepClone(),
            ["tools"] = t,
        };
    }

    public ProviderReply ParseReply(JsonNode response)
    {
        var content = response["content"] as JsonArray ?? new JsonArray();
        var text = new List<string>();
        var calls = new List<AssistantToolCall>();
        foreach (var block in content)
        {
            var type = block?["type"]?.GetValue<string>();
            if (type == "text")
            {
                var s = block?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(s)) text.Add(s);
            }
            else if (type == "tool_use")
            {
                var id = block?["id"]?.GetValue<string>() ?? "";
                var name = block?["name"]?.GetValue<string>() ?? "";
                calls.Add(new AssistantToolCall(id, name, block?["input"]?.DeepClone()));
            }
        }
        var message = new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() };
        return new ProviderReply(string.Join("\n\n", text).Trim(), calls, message);
    }

    public void AppendToolResults(JsonArray history, ProviderReply reply, IReadOnlyList<(AssistantToolCall Call, string Result)> results)
    {
        history.Add(reply.AssistantMessage.DeepClone());
        var blocks = new JsonArray();
        foreach (var (call, result) in results)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = call.Id,
                ["content"] = result,
            });
        }
        history.Add(new JsonObject { ["role"] = "user", ["content"] = blocks });
    }

    public JsonNode UserMessage(string text) => new JsonObject { ["role"] = "user", ["content"] = text };

    public JsonNode AssistantText(string text) => new JsonObject { ["role"] = "assistant", ["content"] = text };
}

public sealed class OpenAiCompatProvider : IAssistantProvider
{
    public string Endpoint(string baseUrl) => baseUrl.TrimEnd('/') + "/chat/completions";

    public void ApplyHeaders(HttpRequestMessage req, string? apiKey)
    {
        if (!string.IsNullOrEmpty(apiKey)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
    }

    public JsonObject BuildRequest(string model, string system, JsonArray history, IReadOnlyList<AssistantToolDef> tools)
    {
        var t = new JsonArray();
        foreach (var d in tools)
        {
            t.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = d.Name,
                    ["description"] = d.Description,
                    ["parameters"] = d.Schema.DeepClone(),
                },
            });
        }
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in history) messages.Add(m?.DeepClone());
        return new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["tools"] = t,
            ["stream"] = false,
        };
    }

    public ProviderReply ParseReply(JsonNode response)
    {
        var message = response["choices"]?[0]?["message"] as JsonObject ?? new JsonObject { ["role"] = "assistant" };
        var text = message["content"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";
        var calls = new List<AssistantToolCall>();
        if (message["tool_calls"] is JsonArray tc)
        {
            foreach (var c in tc)
            {
                var id = c?["id"]?.GetValue<string>() ?? "";
                var name = c?["function"]?["name"]?.GetValue<string>() ?? "";
                JsonNode? input = null;
                var args = c?["function"]?["arguments"];
                if (args is JsonValue av && av.GetValueKind() == JsonValueKind.String)
                {
                    try { input = JsonNode.Parse(av.GetValue<string>()); } catch (JsonException) { input = null; }
                }
                else if (args is not null)
                {
                    input = args.DeepClone();
                }
                calls.Add(new AssistantToolCall(id, name, input));
            }
        }
        return new ProviderReply(text.Trim(), calls, message.DeepClone());
    }

    public void AppendToolResults(JsonArray history, ProviderReply reply, IReadOnlyList<(AssistantToolCall Call, string Result)> results)
    {
        history.Add(reply.AssistantMessage.DeepClone());
        foreach (var (call, result) in results)
        {
            history.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = call.Id,
                ["content"] = result,
            });
        }
    }

    public JsonNode UserMessage(string text) => new JsonObject { ["role"] = "user", ["content"] = text };

    public JsonNode AssistantText(string text) => new JsonObject { ["role"] = "assistant", ["content"] = text };
}
