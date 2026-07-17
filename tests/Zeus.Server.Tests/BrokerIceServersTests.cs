// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Net;
using Zeus.Server.Diagnostics;
using Zeus.Server.Hosting.Remote;
using Zeus.Server.Hosting.Support;

namespace Zeus.Server.Tests;

public sealed class BrokerIceServersTests
{
    private const string BrokerTurnJson = """
    {"iceServers":[
      {"urls":"stun:stun.cloudflare.com:3478"},
      {"urls":[
        "turn:turnv2.realtime.cloudflare.com:3478?transport=udp",
        "turn:turn.cloudflare.com:3478?transport=tcp",
        "turns:turn.cloudflare.com:5349?transport=tcp",
        "turn:turn.cloudflare.com:53?transport=udp"
      ],"username":"u","credential":"c"}
    ]}
    """;

    [Fact]
    public void ParseIceServers_AcceptsStringAndArrayUrls_DropsTcpAndTurns_AttachesCredentials()
    {
        var servers = BrokerIceServers.ParseIceServers(BrokerTurnJson);

        Assert.Equal(3, servers.Count);
        Assert.Contains(servers, s => s.urls == "stun:stun.cloudflare.com:3478");
        var turns = servers.Where(s => s.urls.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, turns.Count);
        Assert.All(turns, s =>
        {
            Assert.DoesNotContain("transport=tcp", s.urls);
            Assert.Equal("u", s.username);
            Assert.Equal("c", s.credential);
        });
        Assert.DoesNotContain(servers, s => s.urls.StartsWith("turns:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseIceServers_MalformedJson_ReturnsEmpty()
        => Assert.Empty(BrokerIceServers.ParseIceServers("{ not json"));

    [Fact]
    public async Task GetIceServersAsync_Success_UsesParsedBrokerList()
    {
        var factory = new StubHttpClientFactory(_ => Json(BrokerTurnJson));

        var servers = await BrokerIceServers.GetIceServersAsync(
            factory, NullLogger.Instance, "test.rtc", CancellationToken.None);

        Assert.Equal(3, servers.Count);
        Assert.Equal(1, factory.CallCount);
        Assert.Contains(servers, s => s.urls.StartsWith("turn:", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task GetIceServersAsync_NonSuccess_FallsBackToStun(HttpStatusCode status)
    {
        var factory = new StubHttpClientFactory(_ => new HttpResponseMessage(status));

        var servers = await BrokerIceServers.GetIceServersAsync(
            factory, NullLogger.Instance, "test.rtc", CancellationToken.None);

        Assert.Single(servers);
        Assert.Equal("stun:stun.cloudflare.com:3478", servers[0].urls);
    }

    [Fact]
    public async Task GetIceServersAsync_ExceptionOrTimeout_FallsBackToStun()
    {
        var factory = new StubHttpClientFactory(_ => throw new TaskCanceledException("timeout"));

        var servers = await BrokerIceServers.GetIceServersAsync(
            factory, NullLogger.Instance, "test.rtc", CancellationToken.None);

        Assert.Single(servers);
        Assert.Equal("stun:stun.cloudflare.com:3478", servers[0].urls);
    }

    [Fact]
    public async Task GetIceServersAsync_NullFactory_FallsBackToStun()
    {
        var servers = await BrokerIceServers.GetIceServersAsync(
            null, NullLogger.Instance, "test.rtc", CancellationToken.None);

        Assert.Single(servers);
        Assert.Equal("stun:stun.cloudflare.com:3478", servers[0].urls);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public int CallCount { get; private set; }
        public HttpClient CreateClient(string name) => new(new StubHandler(this, respond));

        private sealed class StubHandler(
            StubHttpClientFactory owner,
            Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                owner.CallCount++;
                return Task.FromResult(respond(request));
            }
        }
    }
}

public sealed class SupportWebRtcServiceIceTests
{
    [Fact]
    public async Task ConnectSupportAsync_ConsumesGrant_AndPassesFetchedIceToSession()
    {
        const string json = """
        {"iceServers":[{"urls":["stun:stun.cloudflare.com:3478","turn:turn.example.test:3478?transport=udp"],"username":"u","credential":"c"}]}
        """;
        var grants = new SupportGrantStore();
        grants.Approve("req-1", "KB2UKA");
        var factory = new StubHttpClientFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        IReadOnlyList<RTCIceServer>? seen = null;
        var service = new SupportWebRtcService(grants, NullLogger<SupportWebRtcService>.Instance, httpFactory: factory)
        {
            SessionFactoryForTest = (grant, _, ice, _, _) =>
            {
                Assert.Equal("req-1", grant.RequestId);
                seen = ice;
                return new FakeSupportSession();
            },
        };

        var answer = await service.ConnectSupportAsync("req-1", "offer");

        Assert.Equal("answer", answer);
        Assert.Equal(0, grants.LiveCount);
        Assert.NotNull(seen);
        Assert.Contains(seen!, s => s.urls.StartsWith("turn:", StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAsync<SupportNotAuthorizedException>(() =>
            service.ConnectSupportAsync("req-1", "offer"));
    }

    [Fact]
    public async Task ConnectSupportAsync_NoFactory_FallsBackToStunAndAnswers()
    {
        var grants = new SupportGrantStore();
        grants.Approve("req-2", "KB2UKA");
        IReadOnlyList<RTCIceServer>? seen = null;
        var service = new SupportWebRtcService(grants, NullLogger<SupportWebRtcService>.Instance)
        {
            SessionFactoryForTest = (_, _, ice, _, _) =>
            {
                seen = ice;
                return new FakeSupportSession();
            },
        };

        var answer = await service.ConnectSupportAsync("req-2", "offer");

        Assert.Equal("answer", answer);
        Assert.NotNull(seen);
        Assert.Single(seen!);
        Assert.Equal("stun:stun.cloudflare.com:3478", seen![0].urls);
    }

    private sealed class FakeSupportSession : ISupportWebRtcSession
    {
        public event Action? Closed;
        public Task<string> CreateAnswerAsync(string offerSdp, CancellationToken ct = default) =>
            Task.FromResult("answer");
        public void Close() => Closed?.Invoke();
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(respond));

        private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromResult(respond(request));
        }
    }
}
