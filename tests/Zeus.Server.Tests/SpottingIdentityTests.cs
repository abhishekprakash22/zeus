// SPDX-License-Identifier: GPL-2.0-or-later
//
// Spotting resolves its station the way the rest of Zeus already does.
//
// The panel has always told the operator that leaving callsign and grid blank
// falls back to the QRZ home station — the precedence OperatorIdentityResolver
// implements and the WSJT-X and N1MM broadcasters use. Spotting was the one
// subsystem that never asked the resolver: it kept its own copy of the identity
// and treated a blank field as "no station", so an operator whose identity came
// from QRZ had uploads silently paused. These tests pin the fix, and pin that a
// station with no identity anywhere still uploads nothing.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Zeus.Server.Hosting.Digital;

namespace Zeus.Server.Tests;

public sealed class SpottingIdentityTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"zeus-spotting-id-{Guid.NewGuid():N}");

    public SpottingIdentityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private OperatorIdentityStore NewStore() =>
        new(NullLogger<OperatorIdentityStore>.Instance, Path.Combine(_root, "operator.db"));

    // Logged out: GetStatus().Home is null, so the QRZ leg of the fallback is
    // exercised without touching the network.
    private QrzService NewLoggedOutQrz() =>
        new(new SingleClientFactory(), NullLogger<QrzService>.Instance,
            new CredentialStore(NullLogger<CredentialStore>.Instance, Path.Combine(_root, "creds.db")));

    private static SpottingSettings Enabled(string call, string grid) =>
        new(PskReporterEnabled: true, WsprnetEnabled: true, Callsign: call, Grid: grid);

    [Fact]
    public void BlankPanel_FallsBackToTheOperatorIdentity()
    {
        using var store = NewStore();
        var qrz = NewLoggedOutQrz();
        store.Set(new OperatorIdentity("W1AW", "FN31"));

        var effective = SpottingService.Effective(Enabled("", ""), store, qrz);

        Assert.Equal("W1AW", effective.Callsign);
        Assert.Equal("FN31", effective.Grid);
        Assert.True(effective.IdentityResolved);     // uploads are no longer paused
    }

    [Fact]
    public void WhatTheOperatorTypedInThePanelWins()
    {
        using var store = NewStore();
        var qrz = NewLoggedOutQrz();
        store.Set(new OperatorIdentity("W1AW", "FN31"));

        var effective = SpottingService.Effective(Enabled("EA5IUE", "IM98"), store, qrz);

        Assert.Equal("EA5IUE", effective.Callsign);
        Assert.Equal("IM98", effective.Grid);
    }

    [Fact]
    public void OnlyTheBlankFieldIsFilled()
    {
        using var store = NewStore();
        var qrz = NewLoggedOutQrz();
        store.Set(new OperatorIdentity("W1AW", "FN31"));

        var effective = SpottingService.Effective(Enabled("EA5IUE", ""), store, qrz);

        Assert.Equal("EA5IUE", effective.Callsign);  // the panel's own call stands
        Assert.Equal("FN31", effective.Grid);        // the missing half is resolved
    }

    [Fact]
    public void NoIdentityAnywhere_StaysUnresolved_SoNothingUploads()
    {
        using var store = NewStore();
        var qrz = NewLoggedOutQrz();

        var effective = SpottingService.Effective(Enabled("", ""), store, qrz);

        Assert.Equal("", effective.Callsign);
        Assert.False(effective.IdentityResolved);    // still fails closed
    }

    [Fact]
    public void WithoutTheIdentitySources_NothingIsInvented()
    {
        // The sources are optional on the constructor; absent them, spotting
        // behaves exactly as it did before — it never fabricates a station.
        var effective = SpottingService.Effective(Enabled("", ""), null, null);

        Assert.False(effective.IdentityResolved);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
