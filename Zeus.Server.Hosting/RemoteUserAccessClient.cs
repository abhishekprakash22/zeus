// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Zeus.Contracts;
using Zeus.Plugins.Host;

namespace Zeus.Server.Hosting;

public sealed class RemoteUserAccessClient
{
    public const string HttpClientName = "RemoteUserAccess";

    private static readonly Uri DefaultSessionEndpoint = new("https://remote.openhpsdrzeus.com/users/session");
    private static readonly Uri DefaultCheckoutEndpoint = new("https://remote.openhpsdrzeus.com/billing/checkout");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan AllowedSessionCacheTtl = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DeniedOrUnavailableSessionCacheTtl = TimeSpan.FromSeconds(5);
    // Grace window: when the broker cannot answer, the last decision it DID
    // give for this callsign (allow or deny) keeps applying for this long
    // before the gate falls back to the local user store.
    private static readonly TimeSpan SessionGraceTtl = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<RemoteUserAccessClient> _log;
    private readonly object _sessionCacheLock = new();
    private readonly ConcurrentDictionary<string, CachedUserSession> _lastKnownRemoteByCallsign =
        new(StringComparer.OrdinalIgnoreCase);
    private CachedSessionResult? _cachedSession;
    private InflightSessionFetch? _sessionFetch;
    private long _sessionCacheVersion;
    private bool _unavailableWarningOpen;

    private sealed record CachedUserSession(ZeusUserSession Session, DateTimeOffset CachedAt);

    public RemoteUserAccessClient(
        IHttpClientFactory httpFactory,
        ILogger<RemoteUserAccessClient> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    // STANDALONE: remote user management (remote.openhpsdrzeus.com/users/session
    // and /billing/checkout) is project infrastructure. In a standalone build it
    // is forced off so the local UserManagementStore session is authoritative and
    // no session/billing call is ever attempted.
    public bool Enabled =>
        !ZeusStandalone.Enabled &&
        !string.Equals(
            Environment.GetEnvironmentVariable("ZEUS_REMOTE_USER_MANAGEMENT"),
            "off",
            StringComparison.OrdinalIgnoreCase);

    public async Task<ZeusUserSession?> TryGetSessionAsync(
        QrzService qrz,
        QrzStatus qrzStatus,
        CancellationToken ct)
    {
        var result = await GetSessionResultAsync(qrz, qrzStatus, ct).ConfigureAwait(false);
        return result.Outcome == RemoteUserAccessSessionOutcome.Unavailable ? null : result.Session;
    }

    public async Task<RemoteUserAccessSessionResult> GetSessionResultAsync(
        QrzService qrz,
        QrzStatus qrzStatus,
        CancellationToken ct)
    {
        if (!Enabled || !qrzStatus.Connected)
            return RemoteUserAccessSessionResult.Unavailable();

        var callsign = NormalizeCallsign(qrzStatus.Home?.Callsign);
        if (callsign is null)
            return RemoteUserAccessSessionResult.Unavailable();

        var now = DateTimeOffset.UtcNow;
        Task<RemoteUserAccessSessionResult> fetch;
        lock (_sessionCacheLock)
        {
            if (_cachedSession is { } cached &&
                string.Equals(cached.Callsign, callsign, StringComparison.OrdinalIgnoreCase) &&
                cached.ExpiresUtc > now)
            {
                return cached.Result;
            }

            if (_sessionFetch is { } existing &&
                string.Equals(existing.Callsign, callsign, StringComparison.OrdinalIgnoreCase))
            {
                fetch = existing.Fetch;
            }
            else
            {
                var version = _sessionCacheVersion;
                fetch = FetchAndCacheSessionResultAsync(qrz, qrzStatus, callsign, version, CancellationToken.None);
                _sessionFetch = new InflightSessionFetch(callsign, fetch);
                _ = fetch.ContinueWith(
                    completed =>
                    {
                        lock (_sessionCacheLock)
                        {
                            if (ReferenceEquals(_sessionFetch?.Fetch, completed))
                                _sessionFetch = null;
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        return await fetch.WaitAsync(ct).ConfigureAwait(false);
    }

    public void InvalidateSessionCache()
    {
        lock (_sessionCacheLock)
        {
            _sessionCacheVersion++;
            _cachedSession = null;
            _sessionFetch = null;
            _unavailableWarningOpen = false;
        }

        _lastKnownRemoteByCallsign.Clear();
    }

    // Drops only the fresh TTL entry, keeping the grace store, so tests can
    // force the next call back to the broker and exercise the outage path.
    internal void ExpireFreshSessionCacheForTests()
    {
        lock (_sessionCacheLock)
            _cachedSession = null;
    }

    private ZeusUserSession? TryGetGraceSession(string callsign)
    {
        if (!_lastKnownRemoteByCallsign.TryGetValue(callsign, out var cached))
            return null;

        var age = DateTimeOffset.UtcNow - cached.CachedAt;
        if (age > SessionGraceTtl)
        {
            _lastKnownRemoteByCallsign.TryRemove(callsign, out _);
            return null;
        }

        _log.LogWarning(
            "remote user-management unavailable; using cached decision for {Callsign} age={AgeSeconds}s accessAllowed={AccessAllowed}",
            callsign,
            (int)age.TotalSeconds,
            cached.Session.AccessAllowed);
        return cached.Session;
    }

    public void LogUnavailableFallbackOnce(ILogger log, QrzStatus qrzStatus)
    {
        var shouldLog = false;
        lock (_sessionCacheLock)
        {
            if (!_unavailableWarningOpen)
            {
                _unavailableWarningOpen = true;
                shouldLog = true;
            }
        }

        if (shouldLog)
        {
            log.LogWarning(
                "remote user-management unavailable; allowing QRZ user {Callsign} via local user store",
                qrzStatus.Home?.Callsign);
        }
    }

    private async Task<RemoteUserAccessSessionResult> FetchAndCacheSessionResultAsync(
        QrzService qrz,
        QrzStatus qrzStatus,
        string callsign,
        long version,
        CancellationToken ct)
    {
        var result = await FetchSessionResultAsync(qrz, qrzStatus, ct).ConfigureAwait(false);

        if (result.Outcome != RemoteUserAccessSessionOutcome.Unavailable && result.Session is { } fresh)
        {
            _lastKnownRemoteByCallsign[callsign] = new CachedUserSession(fresh, DateTimeOffset.UtcNow);
        }
        else if (result.Outcome == RemoteUserAccessSessionOutcome.Unavailable &&
                 TryGetGraceSession(callsign) is { } grace)
        {
            // Broker unreachable but it gave a decision for this callsign within
            // the grace window — keep honouring that decision (allow OR deny)
            // instead of dropping to the local-store fallback.
            result = grace.AccessAllowed
                ? RemoteUserAccessSessionResult.Success(grace)
                : RemoteUserAccessSessionResult.ExplicitDeny(grace);
        }

        var ttl = CacheTtlFor(result);
        var expiresUtc = DateTimeOffset.UtcNow + ttl;

        lock (_sessionCacheLock)
        {
            if (version == _sessionCacheVersion)
                _cachedSession = new CachedSessionResult(callsign, result, expiresUtc);
        }

        if (result.Outcome != RemoteUserAccessSessionOutcome.Unavailable)
            ClearUnavailableWarning();

        return result;
    }

    private async Task<RemoteUserAccessSessionResult> FetchSessionResultAsync(
        QrzService qrz,
        QrzStatus qrzStatus,
        CancellationToken ct)
    {
        var identity = await TryGetIdentityAsync(qrz, ct).ConfigureAwait(false);
        if (identity is null) return RemoteUserAccessSessionResult.Unavailable();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, SessionEndpoint());
            AddQrzHeaders(req, identity.Value);
            using var res = await _httpFactory.CreateClient(HttpClientName).SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogDebug(
                    "remote user-management session returned {Status} for {Callsign}",
                    (int)res.StatusCode,
                    identity.Value.Callsign);
                return RemoteUserAccessSessionResult.Unavailable();
            }

            var remote = await res.Content.ReadFromJsonAsync<BrokerUserSessionResponse>(JsonOptions, ct)
                .ConfigureAwait(false);
            if (remote?.User is null)
                return RemoteUserAccessSessionResult.Unavailable();

            var session = ToSession(remote, qrzStatus);
            return session.AccessAllowed
                ? RemoteUserAccessSessionResult.Success(session)
                : RemoteUserAccessSessionResult.ExplicitDeny(session);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "remote user-management session failed");
            return RemoteUserAccessSessionResult.Unavailable();
        }
    }

    private void ClearUnavailableWarning()
    {
        lock (_sessionCacheLock)
            _unavailableWarningOpen = false;
    }

    private static TimeSpan CacheTtlFor(RemoteUserAccessSessionResult result) =>
        result.Outcome == RemoteUserAccessSessionOutcome.Success && result.Session?.AccessAllowed == true
            ? AllowedSessionCacheTtl
            : DeniedOrUnavailableSessionCacheTtl;

    public async Task<PluginCheckoutResponse> CreateCheckoutAsync(
        QrzService qrz,
        IReadOnlyList<string> pluginIds,
        string? returnUrl,
        CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("Remote user management is disabled");
        if (pluginIds.Count == 0) throw new ArgumentException("At least one plugin id is required", nameof(pluginIds));

        var identity = await TryGetIdentityAsync(qrz, ct).ConfigureAwait(false);
        if (identity is null) throw new InvalidOperationException("QRZ login required");

        using var req = new HttpRequestMessage(HttpMethod.Post, CheckoutEndpoint())
        {
            Content = JsonContent.Create(new PluginCheckoutRequest(pluginIds, returnUrl), options: JsonOptions),
        };
        AddQrzHeaders(req, identity.Value);
        using var res = await _httpFactory.CreateClient(HttpClientName).SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var detail = await ReadErrorAsync(res, ct).ConfigureAwait(false);
            throw new InvalidOperationException(detail ?? $"Plugin checkout failed: {(int)res.StatusCode}");
        }

        var response = await res.Content.ReadFromJsonAsync<PluginCheckoutResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
        return response ?? new PluginCheckoutResponse(null, false, Array.Empty<string>());
    }

    private static ZeusUserSession ToSession(BrokerUserSessionResponse remote, QrzStatus qrzStatus)
    {
        var user = ToUser(remote.User, qrzStatus);
        var allowed = user.AccessAllowed;
        return new ZeusUserSession(
            QrzConnected: true,
            Callsign: user.Callsign,
            DisplayName: user.DisplayName,
            AccessAllowed: allowed,
            IsAdmin: allowed && user.IsAdmin,
            HasQrzXmlSubscription: qrzStatus.HasXmlSubscription,
            SubscriptionStatus: user.SubscriptionStatus,
            SubscriptionExpiresUtc: user.SubscriptionExpiresUtc,
            PluginAccessMode: user.PluginAccessMode,
            PluginEntitlements: user.PluginEntitlements,
            ManagedPlugins: remote.Plugins.Select(ToPlugin).ToArray(),
            DenialReason: allowed ? null : "Access disabled by Zeus admin",
            User: user);
    }

    private static ZeusUserRecord ToUser(BrokerManagedUserDto user, QrzStatus qrzStatus)
    {
        var callsign = NormalizeCallsign(user.Callsign) ?? NormalizeCallsign(qrzStatus.Home?.Callsign) ?? "";
        return new ZeusUserRecord(
            Callsign: callsign,
            DisplayName: NullIfWhiteSpace(user.DisplayName) ?? DisplayName(qrzStatus.Home) ?? callsign,
            AccessAllowed: user.AccessAllowed,
            IsAdmin: user.IsAdmin,
            SubscriptionStatus: NullIfWhiteSpace(user.SubscriptionStatus) ?? UserManagementStore.DefaultSubscriptionStatus,
            SubscriptionExpiresUtc: UnixMs(user.SubscriptionExpiresAt),
            PluginAccessMode: user.PluginAccessMode == "selected" ? "selected" : UserManagementStore.DefaultPluginAccessMode,
            PluginEntitlements: user.PluginEntitlements.Select(ToEntitlement).ToArray(),
            HasQrzXmlSubscription: qrzStatus.HasXmlSubscription,
            Grid: NullIfWhiteSpace(qrzStatus.Home?.Grid),
            Notes: NullIfWhiteSpace(user.Notes),
            CreatedUtc: UnixMs(user.CreatedAt) ?? DateTime.UnixEpoch,
            UpdatedUtc: UnixMs(user.UpdatedAt) ?? DateTime.UnixEpoch,
            LastLoginUtc: UnixMs(user.LastLoginAt));
    }

    private static ZeusPluginEntitlement ToEntitlement(BrokerPluginEntitlementDto entitlement) =>
        new(
            PluginId: entitlement.PluginId,
            AccessAllowed: entitlement.AccessAllowed,
            SubscriptionStatus: NullIfWhiteSpace(entitlement.SubscriptionStatus) ?? UserManagementStore.DefaultSubscriptionStatus,
            SubscriptionExpiresUtc: UnixMs(entitlement.SubscriptionExpiresAt),
            DenialReason: NullIfWhiteSpace(entitlement.DenialReason));

    private static ZeusManagedPluginRecord ToPlugin(BrokerManagedPluginDto plugin) =>
        new(
            PluginId: plugin.PluginId,
            DisplayName: NullIfWhiteSpace(plugin.DisplayName) ?? plugin.PluginId,
            SubscriptionRequired: plugin.SubscriptionRequired,
            MonthlyPriceCents: Math.Max(0, plugin.MonthlyPriceCents),
            Currency: NullIfWhiteSpace(plugin.Currency)?.ToUpperInvariant() ?? "USD",
            Active: plugin.Active,
            CheckoutUrl: NullIfWhiteSpace(plugin.CheckoutUrl),
            Notes: NullIfWhiteSpace(plugin.Notes),
            CreatedUtc: UnixMs(plugin.CreatedAt) ?? DateTime.UnixEpoch,
            UpdatedUtc: UnixMs(plugin.UpdatedAt) ?? DateTime.UnixEpoch);

    private static async Task<(string Callsign, string SessionKey)?> TryGetIdentityAsync(
        QrzService qrz,
        CancellationToken ct)
    {
        try
        {
            var identity = await qrz.GetChatIdentityAsync(ct).ConfigureAwait(false);
            if (identity is null) return null;
            return (NormalizeCallsign(identity.Value.Callsign) ?? identity.Value.Callsign, identity.Value.SessionKey);
        }
        catch
        {
            return null;
        }
    }

    private static void AddQrzHeaders(
        HttpRequestMessage req,
        (string Callsign, string SessionKey) identity)
    {
        req.Headers.TryAddWithoutValidation("X-QRZ-Callsign", identity.Callsign);
        req.Headers.TryAddWithoutValidation("X-QRZ-Session", identity.SessionKey);
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var raw = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(JsonOptions, ct)
                .ConfigureAwait(false);
            if (raw is not null && raw.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
                return error;
        }
        catch
        {
            // Fall through to generic status below.
        }

        return $"{(int)response.StatusCode} {response.ReasonPhrase}";
    }

    private static Uri SessionEndpoint()
    {
        var configured = Environment.GetEnvironmentVariable("ZEUS_REMOTE_USERS_SESSION_URL")?.Trim();
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri) ? uri : DefaultSessionEndpoint;
    }

    private static Uri CheckoutEndpoint()
    {
        var configured = Environment.GetEnvironmentVariable("ZEUS_PLUGIN_CHECKOUT_URL")?.Trim();
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri) ? uri : DefaultCheckoutEndpoint;
    }

    private static DateTime? UnixMs(long? value) =>
        value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value).UtcDateTime : null;

    private static string? NormalizeCallsign(string? callsign) =>
        NullIfWhiteSpace(callsign)?.ToUpperInvariant();

    private static string? DisplayName(QrzStation? home)
    {
        if (home is null) return null;
        var parts = new[] { home.FirstName, home.Name }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim());
        return NullIfWhiteSpace(string.Join(' ', parts)) ?? NormalizeCallsign(home.Callsign);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record BrokerUserSessionResponse(
        string Callsign,
        BrokerManagedUserDto User,
        IReadOnlyList<BrokerManagedPluginDto> Plugins);

    private sealed record BrokerManagedUserDto(
        string Callsign,
        string? DisplayName,
        bool AccessAllowed,
        bool IsAdmin,
        string? SubscriptionStatus,
        long? SubscriptionExpiresAt,
        string? PluginAccessMode,
        IReadOnlyList<BrokerPluginEntitlementDto> PluginEntitlements,
        string? Notes,
        long? CreatedAt,
        long? UpdatedAt,
        long? LastLoginAt);

    private sealed record BrokerPluginEntitlementDto(
        string PluginId,
        bool AccessAllowed,
        string? SubscriptionStatus,
        long? SubscriptionExpiresAt,
        string? DenialReason);

    private sealed record BrokerManagedPluginDto(
        string PluginId,
        string? DisplayName,
        bool SubscriptionRequired,
        int MonthlyPriceCents,
        string? Currency,
        bool Active,
        string? CheckoutUrl,
        string? Notes,
        long? CreatedAt,
        long? UpdatedAt);

    private sealed record CachedSessionResult(
        string Callsign,
        RemoteUserAccessSessionResult Result,
        DateTimeOffset ExpiresUtc);

    private sealed record InflightSessionFetch(
        string Callsign,
        Task<RemoteUserAccessSessionResult> Fetch);
}

public enum RemoteUserAccessSessionOutcome
{
    Success,
    ExplicitDeny,
    Unavailable,
}

public sealed record RemoteUserAccessSessionResult(
    RemoteUserAccessSessionOutcome Outcome,
    ZeusUserSession? Session)
{
    public static RemoteUserAccessSessionResult Success(ZeusUserSession session) =>
        new(RemoteUserAccessSessionOutcome.Success, session);

    public static RemoteUserAccessSessionResult ExplicitDeny(ZeusUserSession session) =>
        new(RemoteUserAccessSessionOutcome.ExplicitDeny, session);

    public static RemoteUserAccessSessionResult Unavailable() =>
        new(RemoteUserAccessSessionOutcome.Unavailable, null);
}

public sealed record PluginCheckoutRequest(
    IReadOnlyList<string> PluginIds,
    string? ReturnUrl = null);

public sealed record PluginCheckoutResponse(
    string? Url,
    bool SubscriptionUpdated,
    IReadOnlyList<string> PluginIds);
