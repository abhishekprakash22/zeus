// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Net.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Hosting;

/// <summary>
/// Publishes a QRZ-verified "this Zeus user exists/is online" heartbeat to the
/// central broker. This is separate from remote-diagnostics support presence:
/// every QRZ-signed-in Zeus instance should populate the admin user ledger, even
/// if the operator has not opted into maintainer diagnostics sessions.
/// </summary>
public sealed class UserDirectoryReporter : BackgroundService
{
    public const string HttpClientName = "UserDirectoryReporter";

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromMinutes(5);
    private static readonly Uri DefaultEndpoint = new("https://remote.openhpsdrzeus.com/users/heartbeat");

    private readonly IHttpClientFactory _httpFactory;
    private readonly QrzService _qrz;
    private readonly ILogger<UserDirectoryReporter> _log;
    private readonly SemaphoreSlim _wake = new(0, 1);

    public UserDirectoryReporter(
        IHttpClientFactory httpFactory,
        QrzService qrz,
        ILogger<UserDirectoryReporter> log)
    {
        _httpFactory = httpFactory;
        _qrz = qrz;
        _log = log;
        _qrz.IdentityChanged += Wake;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextDelay = TimeSpan.Zero;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (nextDelay > TimeSpan.Zero)
            {
                try { await _wake.WaitAsync(nextDelay, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            nextDelay = await TryReportAsync(stoppingToken).ConfigureAwait(false)
                ? HeartbeatInterval
                : ErrorBackoff;
        }
    }

    private async Task<bool> TryReportAsync(CancellationToken ct)
    {
        (string Callsign, string SessionKey)? identity;
        try
        {
            identity = await _qrz.GetChatIdentityAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "user directory: QRZ identity unavailable");
            return false;
        }

        if (identity is null) return false;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint());
            req.Headers.TryAddWithoutValidation("X-QRZ-Callsign", identity.Value.Callsign);
            req.Headers.TryAddWithoutValidation("X-QRZ-Session", identity.Value.SessionKey);
            using var res = await _httpFactory.CreateClient(HttpClientName).SendAsync(req, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode) return true;

            _log.LogDebug(
                "user directory: broker heartbeat for {Callsign} returned {Status}",
                identity.Value.Callsign,
                (int)res.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "user directory: broker heartbeat failed");
            return false;
        }
    }

    private static Uri Endpoint()
    {
        var configured = Environment.GetEnvironmentVariable("ZEUS_USER_DIRECTORY_URL")?.Trim();
        if (Uri.TryCreate(configured, UriKind.Absolute, out var uri)) return uri;
        return DefaultEndpoint;
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    public override void Dispose()
    {
        try { _qrz.IdentityChanged -= Wake; } catch { }
        _wake.Dispose();
        base.Dispose();
    }
}
