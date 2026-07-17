using System.Text.Json;
using Zeus.Plugins.Contracts.Registry;

namespace Zeus.Plugins.Contracts.Tests;

public class RegistryCatalogTests
{
    [Fact]
    public void Deserialises_EmptyCatalog()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "generated": "2026-05-17T12:00:00Z",
          "plugins": []
        }
        """;
        var cat = JsonSerializer.Deserialize<RegistryCatalog>(json);
        Assert.NotNull(cat);
        Assert.Empty(cat!.Plugins);
    }

    [Fact]
    public void Deserialises_EntryWithOneVersion()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "generated": "2026-05-17T12:00:00Z",
          "plugins": [
            {
              "id": "com.example.amp",
              "name": "Amp",
              "description": "x",
              "author": "Jane",
              "license": "GPL-2.0-or-later",
              "homepage": "https://example.com",
              "categories": ["amplifier"],
              "verified": true,
              "subscription": {
                "required": true,
                "monthlyPriceCents": 499,
                "currency": "USD",
                "checkoutUrl": "https://example.com/subscribe",
                "notes": "monthly"
              },
              "versions": [
                {
                  "version": "1.0.0",
                  "sdkAbi": 1,
                  "sdkMinVersion": "1.0.0",
                  "platforms": ["any"],
                  "downloadUrl": "https://example.com/amp.zip",
                  "sha256": "deadbeef"
                }
              ]
            }
          ]
        }
        """;
        var cat = JsonSerializer.Deserialize<RegistryCatalog>(json);
        Assert.NotNull(cat);
        var entry = Assert.Single(cat!.Plugins);
        Assert.True(entry.Verified);
        Assert.NotNull(entry.Subscription);
        Assert.True(entry.Subscription!.Required);
        Assert.Equal(499, entry.Subscription.MonthlyPriceCents);
        Assert.Equal("https://example.com/subscribe", entry.Subscription.CheckoutUrl);
        var ver = Assert.Single(entry.Versions);
        Assert.Equal(1, ver.SdkAbi);
        Assert.Equal("deadbeef", ver.Sha256);
        Assert.Equal("any", Assert.Single(ver.Platforms));
    }
}
