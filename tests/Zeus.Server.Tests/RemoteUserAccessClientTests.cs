using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;
using Zeus.Server.Hosting;

namespace Zeus.Server.Tests;

public sealed class RemoteUserAccessClientTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "zeus-remote-users-" + Guid.NewGuid().ToString("N"));

    public RemoteUserAccessClientTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task TryGetSessionAsync_uses_cached_allowed_decision_when_remote_temporarily_fails()
    {
        var remote = new SequenceRemoteHandler(
            () => JsonResponse(RemoteSessionJson(accessAllowed: true, isAdmin: true)),
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var qrz = await LoggedInQrzAsync(remote);
        var client = new RemoteUserAccessClient(
            new NamedFactory(new StubQrzHandler(), remote),
            NullLogger<RemoteUserAccessClient>.Instance);

        var first = await client.TryGetSessionAsync(qrz, qrz.GetStatus(), CancellationToken.None);
        // Age out the fresh TTL entry (but not the grace store) so the second
        // call goes back to the broker and hits the outage response.
        client.ExpireFreshSessionCacheForTests();
        var second = await client.TryGetSessionAsync(qrz, qrz.GetStatus(), CancellationToken.None);

        Assert.NotNull(first);
        Assert.True(first!.AccessAllowed);
        Assert.True(first.IsAdmin);
        Assert.NotNull(second);
        Assert.True(second!.AccessAllowed);
        Assert.True(second.IsAdmin);
        Assert.Equal(2, remote.RequestCount);
    }

    [Fact]
    public async Task TryGetSessionAsync_uses_cached_denied_decision_when_remote_temporarily_fails()
    {
        var remote = new SequenceRemoteHandler(
            () => JsonResponse(RemoteSessionJson(accessAllowed: false, isAdmin: false)),
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var qrz = await LoggedInQrzAsync(remote);
        var client = new RemoteUserAccessClient(
            new NamedFactory(new StubQrzHandler(), remote),
            NullLogger<RemoteUserAccessClient>.Instance);

        var first = await client.TryGetSessionAsync(qrz, qrz.GetStatus(), CancellationToken.None);
        // Age out the fresh TTL entry (but not the grace store) so the second
        // call goes back to the broker and hits the outage response.
        client.ExpireFreshSessionCacheForTests();
        var second = await client.TryGetSessionAsync(qrz, qrz.GetStatus(), CancellationToken.None);

        Assert.NotNull(first);
        Assert.False(first!.AccessAllowed);
        Assert.Equal("Access disabled by Zeus admin", first.DenialReason);
        Assert.NotNull(second);
        Assert.False(second!.AccessAllowed);
        Assert.Equal("Access disabled by Zeus admin", second.DenialReason);
        Assert.Equal(2, remote.RequestCount);
    }

    private async Task<QrzService> LoggedInQrzAsync(SequenceRemoteHandler remote)
    {
        var qrz = new QrzService(
            new NamedFactory(new StubQrzHandler(), remote),
            NullLogger<QrzService>.Instance,
            new CredentialStore(NullLogger<CredentialStore>.Instance, Path.Combine(_root, Guid.NewGuid() + ".db")));

        var status = await qrz.LoginAsync("N9WAR", "pw", CancellationToken.None);
        Assert.True(status.Connected);
        return qrz;
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static string RemoteSessionJson(bool accessAllowed, bool isAdmin) =>
        $$"""
        {
          "callsign": "N9WAR",
          "user": {
            "callsign": "N9WAR",
            "displayName": "N9WAR",
            "accessAllowed": {{accessAllowed.ToString().ToLowerInvariant()}},
            "isAdmin": {{isAdmin.ToString().ToLowerInvariant()}},
            "subscriptionStatus": "manual",
            "subscriptionExpiresAt": null,
            "pluginAccessMode": "free",
            "pluginEntitlements": [],
            "notes": null,
            "createdAt": 0,
            "updatedAt": 0,
            "lastLoginAt": 0
          },
          "plugins": []
        }
        """;

    private sealed class NamedFactory(
        HttpMessageHandler qrzHandler,
        HttpMessageHandler remoteHandler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(
                name == RemoteUserAccessClient.HttpClientName ? remoteHandler : qrzHandler,
                disposeHandler: false);
    }

    private sealed class SequenceRemoteHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new(responses);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.True(request.Headers.TryGetValues("X-QRZ-Callsign", out var callsigns));
            Assert.Equal("N9WAR", callsigns.Single());
            return Task.FromResult(
                _responses.TryDequeue(out var response)
                    ? response()
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class StubQrzHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query;
            var xml = query.Contains("username=", StringComparison.Ordinal)
                ? "<?xml version=\"1.0\" encoding=\"utf-8\" ?>"
                    + "<QRZDatabase version=\"1.36\" xmlns=\"http://xmldata.qrz.com\">"
                    + "<Session><Key>SESSION123</Key>"
                    + "<SubExp>Wed Dec 31 23:59:59 2031</SubExp></Session></QRZDatabase>"
                : "<?xml version=\"1.0\" encoding=\"utf-8\" ?>"
                    + "<QRZDatabase version=\"1.36\" xmlns=\"http://xmldata.qrz.com\">"
                    + "<Session><Key>SESSION123</Key></Session>"
                    + "<Callsign><call>N9WAR</call><fname>Test</fname></Callsign></QRZDatabase>";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml),
            });
        }
    }
}
