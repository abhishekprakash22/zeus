// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.FrontPanel;

namespace Zeus.Server.Tests;

// Per-install G2-Ultra front-panel mapping: store persistence + the router's
// override-aware resolution (pinned MOX/TUNE, tr01-only firing for overridden
// buttons, ids outside the default table, PureSignal reachable only via an
// explicit mapping).
public sealed class G2PanelMappingTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-g2map-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private G2PanelMappingStore NewStore() =>
        new(NullLogger<G2PanelMappingStore>.Instance, _dbPath);

    // ---- Store -------------------------------------------------------------

    [Fact]
    public void FreshStore_HasNoOverrides()
    {
        using var store = NewStore();
        Assert.Empty(store.Overrides(G2PanelMappingStore.KindButton));
        Assert.Empty(store.Overrides(G2PanelMappingStore.KindEncoder));
    }

    [Fact]
    public void SetOverride_RoundTrips_AndClears()
    {
        using var store = NewStore();
        store.SetOverride(G2PanelMappingStore.KindButton, 44, "TogglePureSignal");
        Assert.Equal("TogglePureSignal", store.Overrides(G2PanelMappingStore.KindButton)[44]);

        store.SetOverride(G2PanelMappingStore.KindButton, 44, null);
        Assert.Empty(store.Overrides(G2PanelMappingStore.KindButton));
    }

    [Fact]
    public void ButtonAndEncoderOverrides_AreDisjointNamespaces()
    {
        using var store = NewStore();
        store.SetOverride(G2PanelMappingStore.KindButton, 5, "ToggleCtun");
        store.SetOverride(G2PanelMappingStore.KindEncoder, 5, "Drive");
        Assert.Equal("ToggleCtun", store.Overrides(G2PanelMappingStore.KindButton)[5]);
        Assert.Equal("Drive", store.Overrides(G2PanelMappingStore.KindEncoder)[5]);
    }

    [Fact]
    public void ResetAll_DeletesEverything()
    {
        using var store = NewStore();
        store.SetOverride(G2PanelMappingStore.KindButton, 44, "TogglePureSignal");
        store.SetOverride(G2PanelMappingStore.KindEncoder, 6, "Atten");
        store.ResetAll();
        Assert.Empty(store.Overrides(G2PanelMappingStore.KindButton));
        Assert.Empty(store.Overrides(G2PanelMappingStore.KindEncoder));
    }

    [Fact]
    public void Overrides_PersistAcrossReopen()
    {
        using (var store = NewStore())
            store.SetOverride(G2PanelMappingStore.KindButton, 44, "TogglePureSignal");
        using var reopened = NewStore();
        Assert.Equal("TogglePureSignal", reopened.Overrides(G2PanelMappingStore.KindButton)[44]);
    }

    // ---- Router resolution -------------------------------------------------

    [Fact]
    public void ReservedId_WithOverride_FiresOnShortPressOnly()
    {
        var overrides = new Dictionary<int, G2PanelActionRouter.ButtonAction>
        {
            [44] = G2PanelActionRouter.ButtonAction.TogglePureSignal,
        };
        // tr01 fires…
        Assert.Equal(G2PanelActionRouter.ButtonAction.TogglePureSignal,
            G2PanelActionRouter.ResolveButtonAction(44, 0, 1, overrides));
        // …long-hold and release do not.
        Assert.Null(G2PanelActionRouter.ResolveButtonAction(44, 1, 2, overrides));
        Assert.Null(G2PanelActionRouter.ResolveButtonAction(44, 1, 0, overrides));
    }

    [Fact]
    public void OverriddenLongPressButton_FiresOnPressNotRelease()
    {
        // Default for 25 (NR) fires on tr10; an override makes it a plain
        // push-button (tr01) — uniform semantics for every remapped key.
        var overrides = new Dictionary<int, G2PanelActionRouter.ButtonAction>
        {
            [25] = G2PanelActionRouter.ButtonAction.ToggleCtun,
        };
        Assert.Equal(G2PanelActionRouter.ButtonAction.ToggleCtun,
            G2PanelActionRouter.ResolveButtonAction(25, 0, 1, overrides));
        Assert.Null(G2PanelActionRouter.ResolveButtonAction(25, 1, 0, overrides));
    }

    [Theory]
    [InlineData(G2PanelActionRouter.MoxButtonId, "ToggleMox")]
    [InlineData(G2PanelActionRouter.TuneButtonId, "ToggleTune")]
    public void MoxAndTune_ArePinned_OverridesIgnored(int buttonId, string expectedDefault)
    {
        var overrides = new Dictionary<int, G2PanelActionRouter.ButtonAction>
        {
            [buttonId] = G2PanelActionRouter.ButtonAction.ToggleCtun,
        };
        Assert.Equal(expectedDefault,
            G2PanelActionRouter.ResolveButtonAction(buttonId, 0, 1, overrides)?.ToString());
    }

    [Fact]
    public void NoOverrides_ResolvesExactlyTheDefaultTable()
    {
        Assert.Equal("BandPlus",
            G2PanelActionRouter.ResolveButtonAction(16, 0, 1, null)?.ToString());
        Assert.Null(G2PanelActionRouter.ResolveButtonAction(44, 0, 1, null));
        Assert.Equal("Drive",
            G2PanelActionRouter.ResolveEncoderAction(6, null)?.ToString());
    }

    [Fact]
    public void EncoderOverride_WinsOverDefault()
    {
        var overrides = new Dictionary<int, G2PanelActionRouter.EncoderAction>
        {
            [6] = G2PanelActionRouter.EncoderAction.Atten,
        };
        Assert.Equal(G2PanelActionRouter.EncoderAction.Atten,
            G2PanelActionRouter.ResolveEncoderAction(6, overrides));
    }

    // ---- Catalog invariants ------------------------------------------------

    [Fact]
    public void PureSignal_IsInCatalog_ButNeverADefault()
    {
        Assert.Contains("TogglePureSignal", G2PanelActionRouter.ButtonActionNames());
        // KB2UKA no-auto-arm: no inventory entry defaults to PS.
        Assert.DoesNotContain(G2PanelActionRouter.ButtonInventory(),
            c => c.DefaultAction == "TogglePureSignal");
    }

    [Fact]
    public void EncoderCatalog_ExcludesCountSentinel()
    {
        Assert.DoesNotContain("Count", G2PanelActionRouter.EncoderActionNames());
    }

    [Fact]
    public void Inventory_PinsExactlyMoxAndTune()
    {
        var pinned = G2PanelActionRouter.ButtonInventory()
            .Where(c => c.Pinned).Select(c => c.Id).OrderBy(i => i).ToArray();
        Assert.Equal(
            new[] { G2PanelActionRouter.TuneButtonId, G2PanelActionRouter.MoxButtonId }, pinned);
        Assert.DoesNotContain(G2PanelActionRouter.EncoderInventory(), c => c.Pinned);
    }

    // ---- VFO divisor (piHPSDR vfo_encoder_divisor model) ---------------------

    [Fact]
    public void VfoDivisor_ZeroIsAuto_StepDerived()
    {
        Assert.Equal(
            G2PanelActionRouter.VfoEncoderDivisorForStep(100),
            G2PanelActionRouter.EffectiveVfoDivisor(100, 0));
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(1, 1)]
    [InlineData(60, 60)]
    [InlineData(999, 60)] // clamped to the max
    public void VfoDivisor_FixedValue_OverridesStepDerivation(int fixedDivisor, int expected)
    {
        // Step would derive a different divisor; the fixed value must win.
        Assert.Equal(expected, G2PanelActionRouter.EffectiveVfoDivisor(1, fixedDivisor));
        Assert.Equal(expected, G2PanelActionRouter.EffectiveVfoDivisor(100_000, fixedDivisor));
    }

    [Fact]
    public void DivideVfoEncoderTicks_HonoursFixedDivisor()
    {
        // 10 ticks / divisor 5 → 2 logical steps, no remainder — regardless of
        // what the 1 Hz step would have derived (divisor 1).
        var (steps, remainder) = G2PanelActionRouter.DivideVfoEncoderTicks(0, 10, 1, 5);
        Assert.Equal(2, steps);
        Assert.Equal(0, remainder);
        // Remainder carries.
        (steps, remainder) = G2PanelActionRouter.DivideVfoEncoderTicks(0, 7, 1, 5);
        Assert.Equal(1, steps);
        Assert.Equal(2, remainder);
    }

    // ---- VFO tick shaping (acceleration curve vs linear fixed divide) --------

    [Theory]
    [InlineData(5, 5)]      // curve identity region
    [InlineData(9, 9)]      // last identity entry
    [InlineData(10, 11)]    // curve begins to accelerate
    [InlineData(13, 17)]    // deskhpsdr table values preserved verbatim
    [InlineData(30, 128)]
    [InlineData(31, 124)]   // >30 → raw * multiplier(4)
    [InlineData(-13, -17)]  // sign preserved
    public void AutoMode_AppliesTheSpeedupCurve_Verbatim(int raw, int expected)
    {
        Assert.Equal(expected, G2PanelActionRouter.EffectiveVfoTicks(raw, 0));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(13)]
    [InlineData(31)]
    [InlineData(-13)]
    public void FixedDivisor_BypassesTheCurve_RawTicksVerbatim(int raw)
    {
        // The field failure: superlinear input defeats a linear divide
        // (20-50 Hz jumps at moderate rotation). Fixed = mechanically linear.
        Assert.Equal(raw, G2PanelActionRouter.EffectiveVfoTicks(raw, 10));
    }

    [Fact]
    public void SettingsStore_PersistsVfoDivisor_AndClampsIt()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"zeus-g2div-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new G2PanelSettingsStore(
                NullLogger<G2PanelSettingsStore>.Instance, dbPath))
            {
                store.Set(enabled: true, devicePath: null, baud: 0, assumeUltra: false, vfoDivisor: 15);
                Assert.Equal(15, store.Get().VfoDivisor);
                store.Set(enabled: true, devicePath: null, baud: 0, assumeUltra: false, vfoDivisor: 999);
                Assert.Equal(60, store.Get().VfoDivisor); // clamped
            }
            using var reopened = new G2PanelSettingsStore(
                NullLogger<G2PanelSettingsStore>.Instance, dbPath);
            Assert.Equal(60, reopened.Get().VfoDivisor);
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }
}
