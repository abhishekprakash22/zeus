// SPDX-License-Identifier: GPL-2.0-or-later
//
// Persistence coverage for Signal Intelligence weak-signal display policy.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class DisplayIntelligenceSettingsStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-display-intel-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private DisplayIntelligenceSettingsStore NewStore() =>
        new(NullLogger<DisplayIntelligenceSettingsStore>.Instance, _dbPath);

    [Fact]
    public void FirstRun_ReturnsBalancedWeakSignalDefaults()
    {
        using var store = NewStore();

        var settings = store.Get();

        Assert.Equal("balanced", settings.ProfileId);
        Assert.False(settings.PopEnabled);
        Assert.False(settings.SnapEnabled);
        Assert.False(settings.AutoNotchEnabled);
        Assert.True(settings.VisualAgcEnabled);
        Assert.True(settings.ImpulseRejectEnabled);
        Assert.Equal(30.0, settings.PopSpanDb);
        Assert.Equal(92, settings.WaterfallReliefDepth);
        Assert.Equal(64, settings.WaterfallSmoothness);
        Assert.Equal(4000, settings.SnapRadiusHz);
    }

    [Fact]
    public void Save_MigratesLegacyWaterfallReliefDefaults()
    {
        using var store = NewStore();

        var saved = store.Save(new DisplayIntelligenceSettingsDto(
            ProfileId: "balanced",
            PopEnabled: false,
            SnapEnabled: false,
            AutoNotchEnabled: false,
            AutoProfileEnabled: false,
            VisualAgcEnabled: true,
            ImpulseRejectEnabled: true,
            PopFloorDb: 3.0,
            PopSpanDb: 30.0,
            PopGamma: 0.5,
            PopRenderIntensity: 72,
            WaterfallReliefDepth: 48,
            WaterfallSmoothness: 42,
            CoherenceHoldGate: 0.45,
            CoherenceBoostDb: 4.0,
            RidgeBoost: 0.35,
            RidgeMaxBoostDb: 8.0,
            VisualAgcStrength: 45,
            ImpulseRejectDb: 18,
            SnapRadiusHz: 4000,
            SnapMinSnrDb: 6.0,
            PeakMinSnrDb: 8.0));

        Assert.Equal(92, saved.WaterfallReliefDepth);
        Assert.Equal(64, saved.WaterfallSmoothness);
    }

    [Fact]
    public void Save_NormalizesAndPersists()
    {
        using (var first = NewStore())
        {
            var saved = first.Save(new DisplayIntelligenceSettingsDto(
                ProfileId: " DX ",
                PopEnabled: true,
                SnapEnabled: true,
                AutoNotchEnabled: true,
                AutoProfileEnabled: true,
                VisualAgcEnabled: false,
                ImpulseRejectEnabled: false,
                PopFloorDb: 2.0,
                PopSpanDb: 24.0,
                PopGamma: 0.42,
                PopRenderIntensity: 92,
                WaterfallReliefDepth: 64,
                WaterfallSmoothness: 52,
                CoherenceHoldGate: 0.38,
                CoherenceBoostDb: 5.5,
                RidgeBoost: 0.5,
                RidgeMaxBoostDb: 10.0,
                VisualAgcStrength: 70,
                ImpulseRejectDb: 16,
                SnapRadiusHz: 5000,
                SnapMinSnrDb: 5.0,
                PeakMinSnrDb: 7.0));

            Assert.Equal("dx", saved.ProfileId);
            Assert.True(saved.PopEnabled);
            Assert.True(saved.AutoNotchEnabled);
            Assert.Equal(92, saved.PopRenderIntensity);
            Assert.Equal(64, saved.WaterfallReliefDepth);
            Assert.Equal(52, saved.WaterfallSmoothness);
        }

        using var second = NewStore();
        var settings = second.Get();

        Assert.Equal("dx", settings.ProfileId);
        Assert.True(settings.PopEnabled);
        Assert.True(settings.SnapEnabled);
        Assert.True(settings.AutoNotchEnabled);
        Assert.False(settings.VisualAgcEnabled);
        Assert.Equal(24.0, settings.PopSpanDb);
        Assert.Equal(64, settings.WaterfallReliefDepth);
        Assert.Equal(52, settings.WaterfallSmoothness);
        Assert.Equal(5000, settings.SnapRadiusHz);
    }
}
