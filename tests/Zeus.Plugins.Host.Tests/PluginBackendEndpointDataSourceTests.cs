using Microsoft.AspNetCore.Http;
using Zeus.Plugins.Host;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// Unit tests for the mutable plugin-route data source: snapshot composition
/// and change-token signalling, which is what makes mid-session installs go
/// live without a restart (the route matcher rebuilds on the token).
/// </summary>
public class PluginBackendEndpointDataSourceTests
{
    private static Endpoint Ep(string name) =>
        new(context => Task.CompletedTask, EndpointMetadataCollection.Empty, name);

    [Fact]
    public void Set_Publishes_Endpoints_And_Fires_ChangeToken()
    {
        var source = new PluginBackendEndpointDataSource();
        var token = source.GetChangeToken();
        Assert.Empty(source.Endpoints);
        Assert.False(token.HasChanged);

        source.SetPluginEndpoints("com.example.a", new[] { Ep("a1"), Ep("a2") });

        Assert.True(token.HasChanged);
        Assert.Equal(2, source.Endpoints.Count);
    }

    [Fact]
    public void Set_Same_Id_Replaces_Instead_Of_Duplicating()
    {
        var source = new PluginBackendEndpointDataSource();
        source.SetPluginEndpoints("com.example.a", new[] { Ep("old1"), Ep("old2") });

        var token = source.GetChangeToken();
        source.SetPluginEndpoints("com.example.a", new[] { Ep("new1") });

        Assert.True(token.HasChanged);
        var ep = Assert.Single(source.Endpoints);
        Assert.Equal("new1", ep.DisplayName);
    }

    [Fact]
    public void Remove_Drops_Only_That_Plugin_And_Fires_ChangeToken()
    {
        var source = new PluginBackendEndpointDataSource();
        source.SetPluginEndpoints("com.example.a", new[] { Ep("a1") });
        source.SetPluginEndpoints("com.example.b", new[] { Ep("b1") });

        var token = source.GetChangeToken();
        source.RemovePluginEndpoints("com.example.a");

        Assert.True(token.HasChanged);
        var ep = Assert.Single(source.Endpoints);
        Assert.Equal("b1", ep.DisplayName);
    }

    [Fact]
    public void Remove_Unknown_Id_Is_A_NoOp_And_Does_Not_Signal()
    {
        var source = new PluginBackendEndpointDataSource();
        source.SetPluginEndpoints("com.example.a", new[] { Ep("a1") });

        var token = source.GetChangeToken();
        source.RemovePluginEndpoints("com.example.never-mapped");

        Assert.False(token.HasChanged);
        Assert.Single(source.Endpoints);
    }

    [Fact]
    public void Set_Empty_List_Behaves_Like_Remove()
    {
        var source = new PluginBackendEndpointDataSource();
        source.SetPluginEndpoints("com.example.a", new[] { Ep("a1") });

        source.SetPluginEndpoints("com.example.a", Array.Empty<Endpoint>());

        Assert.Empty(source.Endpoints);
    }
}
