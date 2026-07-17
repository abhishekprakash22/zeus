// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class UserManagementStoreTests : IDisposable
{
    private readonly string _testDbDir;
    private readonly string _testDbPath;
    private readonly UserManagementStore _store;

    public UserManagementStoreTests()
    {
        _testDbDir = Path.Combine(Path.GetTempPath(), $"zeus-users-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDbDir);
        _testDbPath = Path.Combine(_testDbDir, "zeus-prefs.db");
        _store = new UserManagementStore(NullLogger<UserManagementStore>.Instance, _testDbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            if (Directory.Exists(_testDbDir))
                Directory.Delete(_testDbDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; Windows can briefly hold LiteDB files.
        }
    }

    [Fact]
    public void GetSession_WhenLoggedOut_DeniesUntilQrzLogin()
    {
        var session = _store.GetSession(new QrzStatus(false, false, null, null));

        Assert.False(session.QrzConnected);
        Assert.False(session.AccessAllowed);
        Assert.Equal("QRZ login required", session.DenialReason);
    }

    [Fact]
    public void FirstQrzLogin_BootstrapsAllowedAdmin()
    {
        var session = _store.GetSession(Qrz("n9war", hasXml: true));

        Assert.True(session.QrzConnected);
        Assert.True(session.AccessAllowed);
        Assert.True(session.IsAdmin);
        Assert.Equal("N9WAR", session.Callsign);
        Assert.Equal("qrz-xml", session.SubscriptionStatus);
        Assert.Single(_store.ListUsers());
    }

    [Fact]
    public void LaterQrzLogin_IsAllowedNonAdminByDefault()
    {
        _store.GetSession(Qrz("N9WAR"));

        var session = _store.GetSession(Qrz("kb2uka"));

        Assert.True(session.AccessAllowed);
        Assert.False(session.IsAdmin);
        Assert.Equal("KB2UKA", session.Callsign);
        Assert.Equal(2, _store.ListUsers().Count);
    }

    [Fact]
    public void Update_CanDisableAccessAndSetSubscriptionMetadata()
    {
        _store.GetSession(Qrz("N9WAR"));
        _store.GetSession(Qrz("KB2UKA"));
        var expiry = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var updated = _store.Update(
            "kb2uka",
            new ZeusUserUpdateRequest(
                AccessAllowed: false,
                IsAdmin: false,
                SubscriptionStatus: "past_due",
                SubscriptionExpiresUtc: expiry,
                Notes: "manual hold"));

        Assert.False(updated.AccessAllowed);
        Assert.Equal("past_due", updated.SubscriptionStatus);
        Assert.Equal(expiry, updated.SubscriptionExpiresUtc);
        Assert.Equal("manual hold", updated.Notes);

        var session = _store.GetSession(Qrz("KB2UKA"));
        Assert.False(session.AccessAllowed);
        Assert.Equal("Access disabled by Zeus admin", session.DenialReason);
    }

    [Fact]
    public void Update_StoresPerUserPluginEntitlements()
    {
        _store.GetSession(Qrz("N9WAR"));
        _store.GetSession(Qrz("KB2UKA"));
        var expiry = new DateTime(2027, 5, 6, 7, 8, 9, DateTimeKind.Utc);

        var updated = _store.Update(
            "kb2uka",
            new ZeusUserUpdateRequest(
                PluginAccessMode: "selected",
                PluginEntitlements: new[]
                {
                    new ZeusPluginEntitlement(
                        PluginId: "COM.OPENHPSDR.PAID",
                        AccessAllowed: true,
                        SubscriptionStatus: "active",
                        SubscriptionExpiresUtc: expiry),
                }));

        Assert.Equal("selected", updated.PluginAccessMode);
        var entitlement = Assert.Single(updated.PluginEntitlements);
        Assert.Equal("com.openhpsdr.paid", entitlement.PluginId);
        Assert.True(entitlement.AccessAllowed);
        Assert.Equal("active", entitlement.SubscriptionStatus);
        Assert.Equal(expiry, entitlement.SubscriptionExpiresUtc);

        var session = _store.GetSession(Qrz("KB2UKA"));
        Assert.Equal("selected", session.PluginAccessMode);
        Assert.Single(session.PluginEntitlements);
    }

    [Fact]
    public void UpsertManagedPlugin_StoresSubscriptionCatalogMetadata()
    {
        var saved = _store.UpsertManagedPlugin(
            "COM.OPENHPSDR.PAID",
            new ZeusManagedPluginUpdateRequest(
                DisplayName: "Paid Mode",
                SubscriptionRequired: true,
                MonthlyPriceCents: 499,
                Currency: "usd",
                Active: true,
                CheckoutUrl: "https://checkout.example/plugin",
                Notes: "monthly subscription"));

        Assert.Equal("com.openhpsdr.paid", saved.PluginId);
        Assert.Equal("Paid Mode", saved.DisplayName);
        Assert.True(saved.SubscriptionRequired);
        Assert.Equal(499, saved.MonthlyPriceCents);
        Assert.Equal("USD", saved.Currency);
        Assert.Equal("https://checkout.example/plugin", saved.CheckoutUrl);

        var listed = Assert.Single(_store.ListManagedPlugins());
        Assert.Equal(saved.PluginId, listed.PluginId);

        var session = _store.GetSession(Qrz("N9WAR"));
        Assert.Single(session.ManagedPlugins);
    }

    [Fact]
    public void Update_PreventsRemovingTheLastAllowedAdmin()
    {
        _store.GetSession(Qrz("N9WAR"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _store.Update("N9WAR", new ZeusUserUpdateRequest(IsAdmin: false)));
        Assert.Contains("At least one allowed admin", ex.Message);
    }

    private static QrzStatus Qrz(string callsign, bool hasXml = false) => new(
        Connected: true,
        HasXmlSubscription: hasXml,
        Home: new QrzStation(
            Callsign: callsign,
            Name: "Operator",
            FirstName: "Test",
            Country: "United States",
            State: "MA",
            City: "Town",
            Grid: "FN42",
            Lat: 42.0,
            Lon: -71.0,
            Dxcc: 291,
            CqZone: 5,
            ItuZone: 8,
            ImageUrl: null),
        Error: null);
}
