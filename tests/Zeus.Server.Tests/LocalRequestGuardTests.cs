using System.Net;
using Microsoft.AspNetCore.Http;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class LocalRequestGuardTests
{
    [Fact]
    public void IsLocalRequest_AllowsLoopback()
    {
        var ctx = Context(remote: IPAddress.Loopback, local: IPAddress.Loopback);

        Assert.True(LocalRequestGuard.IsLocalRequest(ctx));
    }

    [Fact]
    public void IsLocalRequest_AllowsSameHostAddress()
    {
        var host = IPAddress.Parse("192.168.1.10");
        var ctx = Context(remote: host, local: host);

        Assert.True(LocalRequestGuard.IsLocalRequest(ctx));
    }

    [Fact]
    public void IsLocalRequest_RejectsLanPeer()
    {
        var ctx = Context(
            remote: IPAddress.Parse("192.168.1.50"),
            local: IPAddress.Parse("192.168.1.10"));

        Assert.False(LocalRequestGuard.IsLocalRequest(ctx));
    }

    [Fact]
    public void IsSameOriginOrNoOrigin_AllowsMissingOrigin()
    {
        var ctx = Context();

        Assert.True(LocalRequestGuard.IsSameOriginOrNoOrigin(ctx));
    }

    [Theory]
    [InlineData("http://localhost:6060", true)]
    [InlineData("https://localhost:6060", false)]
    [InlineData("http://localhost:6061", false)]
    [InlineData("http://evil.example", false)]
    [InlineData("null", false)]
    public void IsSameOriginOrNoOrigin_ChecksSchemeHostAndPort(string origin, bool expected)
    {
        var ctx = Context();
        ctx.Request.Headers.Origin = origin;

        Assert.Equal(expected, LocalRequestGuard.IsSameOriginOrNoOrigin(ctx));
    }

    [Fact]
    public void RejectIfNotLocalSameOrigin_RejectsCrossOriginLoopbackRequest()
    {
        var ctx = Context(remote: IPAddress.Loopback, local: IPAddress.Loopback);
        ctx.Request.Headers.Origin = "http://evil.example";

        Assert.NotNull(LocalRequestGuard.RejectIfNotLocalSameOrigin(ctx, "quit Zeus"));
    }

    private static DefaultHttpContext Context(IPAddress? remote = null, IPAddress? local = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = remote;
        ctx.Connection.LocalIpAddress = local;
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("localhost", 6060);
        return ctx;
    }
}
