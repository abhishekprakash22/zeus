// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Collections.Concurrent;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using Zeus.Server.Diagnostics;
using Zeus.Server.Hosting.Remote;

namespace Zeus.Server.Hosting.Support;

/// <summary>
/// Signaling entry point for read-only maintainer support sessions. A support
/// offer is answered ONLY if it carries a request id the operator has approved
/// (a single-use <see cref="SupportGrant"/> in the <see cref="SupportGrantStore"/>);
/// otherwise it is refused. Deny-by-default end-to-end at the radio (ADR-0008):
/// the broker can relay an offer, but without the operator's local "Allow" there
/// is no grant and no session.
/// </summary>
public sealed class SupportWebRtcService
{
    private readonly SupportGrantStore _grants;
    private readonly ILogger<SupportWebRtcService> _log;
    private readonly IHttpClientFactory? _httpFactory;
    private readonly string? _loopbackBaseUrl;
    private readonly DiagnosticLogBuffer? _logBuffer;
    private readonly ConcurrentDictionary<Guid, ISupportWebRtcSession> _sessions = new();

    public SupportWebRtcService(
        SupportGrantStore grants,
        ILogger<SupportWebRtcService> log,
        DiagnosticLogBuffer? logBuffer = null,
        IHttpClientFactory? httpFactory = null,
        string? loopbackBaseUrl = null)
    {
        _grants = grants;
        _log = log;
        _logBuffer = logBuffer;
        _httpFactory = httpFactory;
        _loopbackBaseUrl = loopbackBaseUrl;
    }

    internal Func<SupportGrant, ILogger, IReadOnlyList<RTCIceServer>, SupportApiProxy?, DiagnosticLogBuffer?, ISupportWebRtcSession>? SessionFactoryForTest { get; set; }

    public int ActiveSessions => _sessions.Count;

    /// <summary>
    /// Answer a maintainer support offer for <paramref name="requestId"/>. Throws
    /// <see cref="SupportNotAuthorizedException"/> if there is no live operator-approved
    /// grant for that id (consumed single-use here, so a replayed offer is refused).
    /// </summary>
    public async Task<string> ConnectSupportAsync(string requestId, string offerSdp, CancellationToken ct = default)
    {
        if (!_grants.TryConsume(requestId, out var grant))
            throw new SupportNotAuthorizedException();

        SupportApiProxy? proxy = null;
        if (_httpFactory is not null && !string.IsNullOrEmpty(_loopbackBaseUrl))
            proxy = new SupportApiProxy(_httpFactory, _loopbackBaseUrl, _log);

        var id = Guid.NewGuid();
        var ice = await BrokerIceServers.GetIceServersAsync(_httpFactory, _log, "support.rtc", ct);
        _log.LogInformation(
            "support.rtc ICE: {Count} server(s), relay={Relay}",
            ice.Count,
            BrokerIceServers.RelayCount(ice) > 0);

        var session = (SessionFactoryForTest ?? CreateSession)(grant, _log, ice, proxy, _logBuffer);
        _sessions[id] = session;
        session.Closed += () =>
        {
            if (_sessions.TryRemove(id, out _))
                _log.LogInformation("support.rtc session {Id} closed ({Count} active)", id, _sessions.Count);
        };

        _log.LogInformation(
            "support.rtc answering offer for {Admin} (request {RequestId}), session {Id}",
            grant.AdminCallsign, requestId, id);
        return await session.CreateAnswerAsync(offerSdp, ct);
    }

    public void CloseAll()
    {
        foreach (var kv in _sessions)
            if (_sessions.TryRemove(kv.Key, out var s))
                s.Close();
    }

    private static ISupportWebRtcSession CreateSession(
        SupportGrant grant,
        ILogger log,
        IReadOnlyList<RTCIceServer> ice,
        SupportApiProxy? proxy,
        DiagnosticLogBuffer? logBuffer)
        => new SupportWebRtcSession(grant, log, ice, proxy, logBuffer);
}

/// <summary>Thrown when a support offer arrives without a live operator-approved grant.</summary>
public sealed class SupportNotAuthorizedException()
    : Exception("Support session refused: no operator-approved grant for this request.");
