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
// Assistant settings — which model answers, and with what key. Stored on the
// radio (assistant.json beside zeus-prefs.db, owner-only permissions on
// Unix) so the kiosk keeps them and no browser ever holds the key. Off by
// default: nothing leaves the radio until an operator turns it on.
//
// Providers:
//   anthropic — the Anthropic Messages API with the operator's own key.
//   openai    — any OpenAI-compatible chat-completions server: a model
//               server on a shack PC (Ollama, llama.cpp server, LM Studio)
//               or a hosted service. Key optional.

using System.Text.Json;

namespace Zeus.Server.Assistant;

public sealed record AssistantSettings(
    bool Enabled,
    string Provider,
    string BaseUrl,
    string Model,
    string? ApiKey);

/// <summary>What the UI sees — never the key itself.</summary>
public sealed record AssistantSettingsDto(
    bool Enabled,
    string Provider,
    string BaseUrl,
    string Model,
    bool HasApiKey,
    string? ApiKeyHint);

/// <summary>Partial update; null fields are left unchanged.</summary>
public sealed record AssistantSettingsUpdate(
    bool? Enabled,
    string? Provider,
    string? BaseUrl,
    string? Model,
    string? ApiKey,
    bool? ClearApiKey);

public sealed class AssistantSettingsStore
{
    public const string ProviderAnthropic = "anthropic";
    public const string ProviderOpenAi = "openai";
    public const string DefaultAnthropicBaseUrl = "https://api.anthropic.com";
    public const string DefaultAnthropicModel = "claude-sonnet-5-5";
    public const string DefaultOpenAiBaseUrl = "http://localhost:11434/v1";
    public const string DefaultOpenAiModel = "llama3.1";

    public static readonly AssistantSettings Defaults =
        new(false, ProviderAnthropic, DefaultAnthropicBaseUrl, DefaultAnthropicModel, null);

    private readonly string _path;
    private readonly object _gate = new();
    private AssistantSettings? _cached;

    public AssistantSettingsStore() : this(DefaultPath()) { }

    public AssistantSettingsStore(string path)
    {
        _path = path;
    }

    public static string DefaultPath()
    {
        var dir = Path.GetDirectoryName(PrefsDbPath.Get()) ?? ".";
        return Path.Combine(dir, "assistant.json");
    }

    public AssistantSettings Get()
    {
        lock (_gate)
        {
            if (_cached is not null) return _cached;
            try
            {
                if (File.Exists(_path))
                {
                    var s = JsonSerializer.Deserialize<AssistantSettings>(File.ReadAllText(_path));
                    if (s is not null) return _cached = Normalize(s);
                }
            }
            catch
            {
                // A corrupt file reads as defaults (assistant off); it is rewritten on the next save.
            }
            return _cached = Defaults;
        }
    }

    public AssistantSettings Apply(AssistantSettingsUpdate u)
    {
        lock (_gate)
        {
            var cur = Get();
            var provider = u.Provider is null ? cur.Provider : NormalizeProvider(u.Provider);
            var providerChanged = provider != cur.Provider;
            // Switching provider resets the endpoint and model to that provider's
            // defaults unless the same request supplies its own.
            var baseUrl = u.BaseUrl ?? (providerChanged ? DefaultBaseUrl(provider) : cur.BaseUrl);
            var model = u.Model ?? (providerChanged ? DefaultModel(provider) : cur.Model);
            var key = u.ClearApiKey == true
                ? null
                : string.IsNullOrWhiteSpace(u.ApiKey) ? cur.ApiKey : u.ApiKey.Trim();
            var next = Normalize(new AssistantSettings(u.Enabled ?? cur.Enabled, provider, baseUrl, model, key));
            Save(next);
            return _cached = next;
        }
    }

    public static AssistantSettingsDto ToDto(AssistantSettings s) => new(
        s.Enabled,
        s.Provider,
        s.BaseUrl,
        s.Model,
        !string.IsNullOrEmpty(s.ApiKey),
        KeyHint(s.ApiKey));

    /// <summary>"…a1b2" — enough to recognise a key, never enough to use one.</summary>
    public static string? KeyHint(string? key) =>
        string.IsNullOrEmpty(key) ? null : key.Length <= 8 ? "set" : "…" + key[^4..];

    public static string NormalizeProvider(string? p) =>
        string.Equals(p?.Trim(), ProviderOpenAi, StringComparison.OrdinalIgnoreCase) ? ProviderOpenAi : ProviderAnthropic;

    public static string DefaultBaseUrl(string provider) =>
        provider == ProviderOpenAi ? DefaultOpenAiBaseUrl : DefaultAnthropicBaseUrl;

    public static string DefaultModel(string provider) =>
        provider == ProviderOpenAi ? DefaultOpenAiModel : DefaultAnthropicModel;

    public static AssistantSettings Normalize(AssistantSettings s)
    {
        var provider = NormalizeProvider(s.Provider);
        var baseUrl = (s.BaseUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            baseUrl = DefaultBaseUrl(provider);
        }
        var model = string.IsNullOrWhiteSpace(s.Model) ? DefaultModel(provider) : s.Model.Trim();
        if (model.Length > 128) model = model[..128];
        var key = string.IsNullOrWhiteSpace(s.ApiKey) ? null : s.ApiKey.Trim();
        return new AssistantSettings(s.Enabled, provider, baseUrl, model, key);
    }

    private void Save(AssistantSettings s)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(s));
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Best effort on filesystems without POSIX modes.
            }
        }
    }
}
