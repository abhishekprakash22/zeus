using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;
using Zeus.Server.Hosting;

namespace Zeus.Server.Tests;

public sealed class UserAccessGateEndpointTests
{
    [Theory]
    [InlineData("POST", "/api/connect/p3", true)]
    [InlineData("POST", "/api/connect/p2", true)]
    [InlineData("POST", "/api/vfo", true)]
    [InlineData("PUT", "/api/support/availability", true)]
    [InlineData("POST", "/api/support/agreement", true)]
    [InlineData("POST", "/api/plugins/checkout", true)]
    [InlineData("POST", "/api/qrz/login", false)]
    [InlineData("POST", "/api/qrz/logout", false)]
    [InlineData("POST", "/api/qrz/apikey", false)]
    [InlineData("POST", "/api/disconnect", false)]
    [InlineData("POST", "/api/disconnect/p2", false)]
    [InlineData("POST", "/api/disconnect/p3", false)]
    [InlineData("GET", "/api/users/session", false)]
    [InlineData("GET", "/api/state", false)]
    [InlineData("POST", "/manual", false)]
    public void Protected_request_classifier_matches_user_access_contract(
        string method,
        string path,
        bool expected)
    {
        Assert.Equal(expected, ZeusEndpoints.IsUserAccessGateProtectedRequest(method, new PathString(path)));
    }

    [Fact]
    public async Task BrokerUnavailable_AllowsMutationViaLocalFallback_WithoutRevokingRadio()
    {
        using var gate = new GateServices(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await gate.LoginAsync();

        var radio = gate.GetRequiredService<RadioService>();
        var tx = gate.GetRequiredService<TxService>();
        radio.MarkProtocol3Connected("p3-test", sampleRateHz: 192_000, maxReceivers: 1);
        Assert.True(tx.TrySetMox(true, out var moxError), moxError);

        var result = await gate.InvokeGateAsync();

        Assert.False(result.Denied);
        Assert.True(radio.IsConnected);
        Assert.True(tx.IsMoxOn);
        Assert.Equal(1, gate.RemoteUserSessionCalls);

        tx.TrySetMox(false, out _);
        radio.MarkProtocol3Disconnected();
    }

    [Fact]
    public async Task BrokerExplicitDeny_RejectsMutation_AndRevokesRadioAccess()
    {
        using var gate = new GateServices(_ => BrokerSession(accessAllowed: false));
        await gate.LoginAsync();

        var radio = gate.GetRequiredService<RadioService>();
        var tx = gate.GetRequiredService<TxService>();
        radio.MarkProtocol3Connected("p3-test", sampleRateHz: 192_000, maxReceivers: 1);
        Assert.True(tx.TrySetMox(true, out var moxError), moxError);

        var result = await gate.InvokeGateAsync();

        Assert.True(result.Denied);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Context.Response.StatusCode);
        Assert.False(tx.IsMoxOn);
        Assert.False(radio.IsConnected);
        Assert.Equal(1, gate.RemoteUserSessionCalls);
    }

    [Fact]
    public async Task BrokerSession_IsCachedAcrossRapidGateAndSessionRequests()
    {
        using var gate = new GateServices(_ => BrokerSession(accessAllowed: true));
        await gate.LoginAsync();

        var first = await gate.InvokeGateAsync();
        var remoteUsers = gate.GetRequiredService<RemoteUserAccessClient>();
        var qrz = gate.GetRequiredService<QrzService>();
        var session = await remoteUsers.GetSessionResultAsync(qrz, qrz.GetStatus(), CancellationToken.None);
        var second = await gate.InvokeGateAsync();

        Assert.False(first.Denied);
        Assert.Equal(RemoteUserAccessSessionOutcome.Success, session.Outcome);
        Assert.False(second.Denied);
        Assert.Equal(1, gate.RemoteUserSessionCalls);
    }

    [Fact]
    public async Task SharedSessionFetch_IgnoresFirstWaiterCancellation()
    {
        var remoteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstCts = new CancellationTokenSource();
        using var gate = new GateServices(async (_, ct) =>
        {
            remoteStarted.TrySetResult();
            await releaseRemote.Task.WaitAsync(ct);
            return BrokerSession(accessAllowed: true);
        });
        await gate.LoginAsync();

        var remoteUsers = gate.GetRequiredService<RemoteUserAccessClient>();
        var qrz = gate.GetRequiredService<QrzService>();
        var qrzStatus = qrz.GetStatus();
        var first = remoteUsers.GetSessionResultAsync(qrz, qrzStatus, firstCts.Token);

        await remoteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = remoteUsers.GetSessionResultAsync(qrz, qrzStatus, CancellationToken.None);
        firstCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        releaseRemote.SetResult();
        var result = await second;

        Assert.Equal(RemoteUserAccessSessionOutcome.Success, result.Outcome);
        Assert.Equal(1, gate.RemoteUserSessionCalls);
    }

    private static HttpResponseMessage BrokerSession(bool accessAllowed)
    {
        var allowed = accessAllowed ? "true" : "false";
        return JsonResponse(
            $$"""
            {
              "callsign": "KB2UKA",
              "user": {
                "callsign": "KB2UKA",
                "displayName": "Douglas Cerrato",
                "accessAllowed": {{allowed}},
                "isAdmin": true,
                "subscriptionStatus": "manual",
                "subscriptionExpiresAt": null,
                "pluginAccessMode": "all",
                "pluginEntitlements": [],
                "notes": null,
                "createdAt": 0,
                "updatedAt": 0,
                "lastLoginAt": 0
              },
              "plugins": []
            }
            """);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class GateServices : IDisposable
    {
        private readonly GateHttpStubs _http;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"zeus-gate-test-{Guid.NewGuid():N}.db");
        private readonly string? _previousRemoteUserManagement;
        private readonly ServiceProvider _services;

        public GateServices(Func<HttpRequestMessage, HttpResponseMessage> remoteUserResponse)
            : this((request, _) => Task.FromResult(remoteUserResponse(request)))
        {
        }

        public GateServices(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> remoteUserResponse)
        {
            _http = new GateHttpStubs(remoteUserResponse);
            _previousRemoteUserManagement = Environment.GetEnvironmentVariable("ZEUS_REMOTE_USER_MANAGEMENT");
            Environment.SetEnvironmentVariable("ZEUS_REMOTE_USER_MANAGEMENT", null);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpClient("Qrz")
                .ConfigurePrimaryHttpMessageHandler(_ => _http.CreateQrzHandler());
            services.AddHttpClient(RemoteUserAccessClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(_ => _http.CreateRemoteUserHandler());
            services.AddSingleton(sp =>
                new CredentialStore(sp.GetRequiredService<ILogger<CredentialStore>>(), _dbPath));
            services.AddSingleton<QrzService>();
            services.AddSingleton(sp =>
                new UserManagementStore(sp.GetRequiredService<ILogger<UserManagementStore>>(), _dbPath));
            services.AddSingleton<RemoteUserAccessClient>();
            services.AddSingleton<StreamingHub>();
            services.AddSingleton(sp =>
                new DspSettingsStore(sp.GetRequiredService<ILogger<DspSettingsStore>>(), _dbPath));
            services.AddSingleton(sp =>
                new PaSettingsStore(sp.GetRequiredService<ILogger<PaSettingsStore>>(), _dbPath));
            services.AddSingleton<RadioService>();
            services.AddSingleton<DspPipelineService>();
            services.AddSingleton<IBandPlanService>(NullBandPlanService.Instance);
            services.AddSingleton<TxService>();

            _services = services.BuildServiceProvider();
        }

        public int RemoteUserSessionCalls => _http.RemoteUserSessionCalls;

        public T GetRequiredService<T>() where T : notnull =>
            _services.GetRequiredService<T>();

        public async Task LoginAsync()
        {
            var qrz = GetRequiredService<QrzService>();
            var status = await qrz.LoginAsync("KB2UKA", "pw", CancellationToken.None);
            Assert.True(status.Connected, status.Error);
            GetRequiredService<RemoteUserAccessClient>().InvalidateSessionCache();
        }

        public async Task<(bool Denied, DefaultHttpContext Context)> InvokeGateAsync()
        {
            var ctx = new DefaultHttpContext
            {
                RequestServices = _services,
                Response = { Body = new MemoryStream() },
            };
            ctx.Request.Method = HttpMethods.Post;
            ctx.Request.Path = "/api/tx/leveling";

            var method = typeof(ZeusEndpoints).GetMethod(
                "DenyIfUserAccessBlockedAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var task = (Task<bool>)method!.Invoke(null, new object[] { ctx, NullLogger.Instance })!;
            return (await task.ConfigureAwait(false), ctx);
        }

        public void Dispose()
        {
            _services.Dispose();
            Environment.SetEnvironmentVariable("ZEUS_REMOTE_USER_MANAGEMENT", _previousRemoteUserManagement);
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
            try { if (File.Exists(_dbPath + "-log")) File.Delete(_dbPath + "-log"); } catch { }
        }
    }

    private sealed class GateHttpStubs
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _remoteUserResponse;
        private int _remoteUserSessionCalls;

        public GateHttpStubs(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> remoteUserResponse)
        {
            _remoteUserResponse = remoteUserResponse;
        }

        public int RemoteUserSessionCalls => Volatile.Read(ref _remoteUserSessionCalls);

        public HttpMessageHandler CreateQrzHandler() => new QrzHandler();

        public HttpMessageHandler CreateRemoteUserHandler() => new RemoteUserHandler(this);

        private Task<HttpResponseMessage> SendRemoteUserAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _remoteUserSessionCalls);
            return _remoteUserResponse(request, cancellationToken);
        }

        private static HttpResponseMessage QrzResponse(HttpRequestMessage request)
        {
            var query = request.RequestUri?.Query ?? "";
            if (query.Contains("username=", StringComparison.Ordinal))
            {
                return XmlResponse(
                    "<?xml version=\"1.0\" encoding=\"utf-8\" ?>"
                    + "<QRZDatabase version=\"1.36\" xmlns=\"http://xmldata.qrz.com\">"
                    + "<Session><Key>SESSION123</Key>"
                    + "<SubExp>Wed Dec 31 23:59:59 2031</SubExp></Session></QRZDatabase>");
            }

            return XmlResponse(
                "<?xml version=\"1.0\" encoding=\"utf-8\" ?>"
                + "<QRZDatabase version=\"1.36\" xmlns=\"http://xmldata.qrz.com\">"
                + "<Session><Key>SESSION123</Key></Session>"
                + "<Callsign><call>KB2UKA</call><fname>Douglas</fname><name>Cerrato</name><grid>FN20</grid></Callsign>"
                + "</QRZDatabase>");
        }

        private static HttpResponseMessage XmlResponse(string xml) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
            };

        private sealed class QrzHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromResult(QrzResponse(request));
        }

        private sealed class RemoteUserHandler(GateHttpStubs owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                owner.SendRemoteUserAsync(request, cancellationToken);
        }
    }
}
