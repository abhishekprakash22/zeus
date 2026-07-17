using System.Collections.Concurrent;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// Signaling entry point for remote access (Phase 1). Answers a browser WebRTC
/// offer with a session that is LOCKED until the SPAKE2+ password handshake
/// completes (ADR-0008). Remote access is refused unless the operator has set a
/// password — there is no unauthenticated remote path.
/// </summary>
public sealed class RemoteWebRtcService
{
    private readonly RemotePasswordStore _passwords;
    private readonly ILogger<RemoteWebRtcService> _log;
    private readonly Zeus.Server.StreamingHub? _hub;
    private readonly IHttpClientFactory? _httpFactory;
    private readonly string? _loopbackBaseUrl;
    private readonly ConcurrentDictionary<Guid, RemoteWebRtcSession> _sessions = new();

    public RemoteWebRtcService(
        RemotePasswordStore passwords, ILogger<RemoteWebRtcService> log,
        Zeus.Server.StreamingHub? hub = null,
        IHttpClientFactory? httpFactory = null, string? loopbackBaseUrl = null)
    {
        _passwords = passwords;
        _log = log;
        _hub = hub;
        _httpFactory = httpFactory;
        _loopbackBaseUrl = loopbackBaseUrl;
    }

    /// <summary>Whether remote access can be offered at all (a password is set).</summary>
    public bool RemoteEnabled => _passwords.HasPassword();

    public int ActiveSessions => _sessions.Count;

    /// <summary>
    /// Answer a remote offer. Throws <see cref="RemoteAccessDisabledException"/> if
    /// no password is set (deny-by-default — the operator must enable it).
    /// </summary>
    public async Task<string> ConnectAsync(string offerSdp, CancellationToken ct = default)
    {
        var verifier = _passwords.GetVerifier()
            ?? throw new RemoteAccessDisabledException();

        var id = Guid.NewGuid();
        var ice = await GetIceServersAsync(ct);
        var session = new RemoteWebRtcSession(
            verifier, _log, ice, _hub, _httpFactory, _loopbackBaseUrl);
        _sessions[id] = session;
        session.Closed += () =>
        {
            if (_sessions.TryRemove(id, out _))
                _log.LogInformation("rtc.remote session {Id} closed ({Count} active)", id, _sessions.Count);
        };
        session.Unlocked += () => _log.LogInformation("rtc.remote session {Id} unlocked", id);

        _log.LogInformation("rtc.remote answering offer, session {Id}", id);
        return await session.CreateAnswerAsync(offerSdp, ct);
    }

    public void CloseAll()
    {
        foreach (var kv in _sessions)
            if (_sessions.TryRemove(kv.Key, out var s))
                s.Close();
    }

    internal const string TurnHttpClientName = BrokerIceServers.TurnHttpClientName;

    /// <summary>
    /// Gather the ICE servers for a session. The HOST (this radio) sits behind the
    /// operator's NAT/CGNAT, so STUN alone only yields a server-reflexive candidate
    /// that a symmetric NAT won't accept inbound — across the internet that means no
    /// candidate pair forms and the data channels never open ("remote API request
    /// timed out / won't connect"). Fetching the broker-minted TURN credentials lets
    /// the host gather a relay candidate too, so traversal works regardless of NAT
    /// type (the browser already does the same via /turn). Falls back to STUN-only if
    /// the broker is unreachable — fine on the LAN, may still fail over the internet.
    /// </summary>
    private async Task<List<RTCIceServer>> GetIceServersAsync(CancellationToken ct)
        => await BrokerIceServers.GetIceServersAsync(_httpFactory, _log, "rtc.remote", ct);

    /// <summary>
    /// Parse a broker <c>/turn</c> JSON body (<c>{"iceServers":[{urls,username?,credential?}]}</c>)
    /// into SIPSorcery ICE servers. <c>urls</c> may be a string or an array; each URL becomes
    /// its own entry. Only schemes SIPSorcery reliably gathers from are kept — STUN and UDP TURN;
    /// <c>turns:</c> (TLS) and <c>transport=tcp</c> entries are dropped so a parse/transport quirk
    /// in one URL can't poison candidate gathering.
    /// </summary>
    internal static List<RTCIceServer> ParseIceServers(string json)
        => BrokerIceServers.ParseIceServers(json);
}

/// <summary>Thrown when a remote connection is attempted with no session password set.</summary>
public sealed class RemoteAccessDisabledException()
    : Exception("Remote access requires a session password (Server menu).");

/// <summary>Body for <c>POST /api/remote/connect</c> (the browser's WebRTC offer).</summary>
public sealed record RemoteConnectRequest(string Sdp);
