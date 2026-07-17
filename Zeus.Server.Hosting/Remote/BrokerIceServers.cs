// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// Shared broker-minted ICE server plumbing for WebRTC sessions hosted by the
/// operator's Zeus instance. The broker returns short-lived TURN credentials; if
/// that fetch fails, callers fall back to STUN-only so LAN sessions still work.
/// </summary>
internal static class BrokerIceServers
{
    internal const string TurnHttpClientName = "RemoteTurn";

    /// <summary>
    /// Broker endpoint that mints short-lived Cloudflare TURN credentials.
    /// Overridable for tests / self-hosted brokers via ZEUS_REMOTE_TURN_URL.
    /// </summary>
    private static string TurnUrl =>
        Environment.GetEnvironmentVariable("ZEUS_REMOTE_TURN_URL")?.Trim() is { Length: > 0 } u
            ? u
            : "https://remote.openhpsdrzeus.com/turn";

    /// <summary>
    /// Gather the ICE servers for a host-side WebRTC answer. The host often sits
    /// behind NAT/CGNAT, so STUN alone may not produce a usable candidate pair.
    /// </summary>
    internal static async Task<List<RTCIceServer>> GetIceServersAsync(
        IHttpClientFactory? httpFactory,
        ILogger log,
        string logPrefix,
        CancellationToken ct)
    {
        if (httpFactory is not null)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var client = httpFactory.CreateClient(TurnHttpClientName);
                using var resp = await client.PostAsync(TurnUrl, content: null, timeout.Token);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(timeout.Token);
                var parsed = ParseIceServers(json);
                if (parsed.Count > 0)
                {
                    log.LogInformation(
                        "{Prefix} ICE: {Count} server(s) from broker TURN ({Relay} relay)",
                        logPrefix,
                        parsed.Count,
                        RelayCount(parsed));
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex,
                    "{Prefix} TURN fetch failed; STUN-only fallback (NAT traversal may fail across the internet)",
                    logPrefix);
            }
        }

        return StunFallback();
    }

    /// <summary>
    /// Parse a broker <c>/turn</c> JSON body (<c>{"iceServers":[{urls,username?,credential?}]}</c>)
    /// into SIPSorcery ICE servers. <c>urls</c> may be a string or an array; each URL becomes
    /// its own entry. Only STUN/STUNS and UDP TURN entries are kept; <c>turns:</c> and TCP TURN
    /// are dropped so an unsupported transport cannot poison candidate gathering.
    /// </summary>
    internal static List<RTCIceServer> ParseIceServers(string json)
    {
        var into = new List<RTCIceServer>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("iceServers", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return into;

            foreach (var entry in arr.EnumerateArray())
            {
                string? username = entry.TryGetProperty("username", out var u)
                    && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                string? credential = entry.TryGetProperty("credential", out var c)
                    && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

                if (!entry.TryGetProperty("urls", out var urls)) continue;
                if (urls.ValueKind == JsonValueKind.String)
                    AddUrl(into, urls.GetString(), username, credential);
                else if (urls.ValueKind == JsonValueKind.Array)
                    foreach (var url in urls.EnumerateArray())
                        if (url.ValueKind == JsonValueKind.String)
                            AddUrl(into, url.GetString(), username, credential);
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return into;
    }

    internal static List<RTCIceServer> StunFallback() =>
    [
        new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" },
    ];

    internal static int RelayCount(IEnumerable<RTCIceServer> servers) =>
        servers.Count(s => s.urls?.StartsWith("turn", StringComparison.OrdinalIgnoreCase) == true);

    private static void AddUrl(List<RTCIceServer> into, string? url, string? username, string? credential)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var lower = url.ToLowerInvariant();
        bool keep = lower.StartsWith("stun:") || lower.StartsWith("stuns:")
            || (lower.StartsWith("turn:") && !lower.Contains("transport=tcp"));
        if (!keep) return;

        var server = new RTCIceServer { urls = url };
        if (!string.IsNullOrEmpty(username)) server.username = username;
        if (!string.IsNullOrEmpty(credential)) server.credential = credential;
        into.Add(server);
    }
}
