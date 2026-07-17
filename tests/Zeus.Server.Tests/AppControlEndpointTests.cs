using System.Net;

namespace Zeus.Server.Tests;

public sealed class AppControlEndpointTests
{
    [Theory]
    [InlineData("/api/app/restart")]
    [InlineData("/api/app/quit")]
    public async Task AppControlEndpoint_RejectsCrossOriginPost_BeforeProcessControl(string path)
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Headers.TryAddWithoutValidation("Origin", "http://evil.example");

        using var response = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("same-origin", body, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Factory : IsolatedPrefsFactory { }
}
