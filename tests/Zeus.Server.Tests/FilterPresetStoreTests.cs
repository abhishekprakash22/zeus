using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class FilterPresetStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-filter-presets-{Guid.NewGuid():N}.db");
    private readonly string? _previousPrefsPath;

    public FilterPresetStoreTests()
    {
        _previousPrefsPath = Environment.GetEnvironmentVariable("ZEUS_PREFS_PATH");
        Environment.SetEnvironmentVariable("ZEUS_PREFS_PATH", _dbPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ZEUS_PREFS_PATH", _previousPrefsPath);
        foreach (var suffix in new[] { "", "-log" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { }
        }
    }

    [Fact]
    public void SeedDefaults_PreservesSsbLowEndVar1()
    {
        using var store = NewStore();

        var usb = store.GetVarOverride(RxMode.USB, "VAR1");
        var lsb = store.GetVarOverride(RxMode.LSB, "VAR1");

        Assert.True(usb.HasValue);
        Assert.Equal(100, usb.Value.LowHz);
        Assert.Equal(2800, usb.Value.HighHz);
        Assert.True(lsb.HasValue);
        Assert.Equal(-2800, lsb.Value.LowHz);
        Assert.Equal(-100, lsb.Value.HighHz);
    }

    [Fact]
    public void SeedDefaults_MigratesLegacySsbVar1Seed()
    {
        using (var seed = NewStore())
        {
            seed.UpsertVarOverride(RxMode.USB, "VAR1", 150, 2850);
            seed.UpsertVarOverride(RxMode.LSB, "VAR1", -2850, -150);
        }

        using var reopened = NewStore();

        var usb = reopened.GetVarOverride(RxMode.USB, "VAR1");
        var lsb = reopened.GetVarOverride(RxMode.LSB, "VAR1");
        Assert.True(usb.HasValue);
        Assert.Equal(100, usb.Value.LowHz);
        Assert.Equal(2800, usb.Value.HighHz);
        Assert.True(lsb.HasValue);
        Assert.Equal(-2800, lsb.Value.LowHz);
        Assert.Equal(-100, lsb.Value.HighHz);
    }

    [Fact]
    public void SeedDefaults_DoesNotOverwriteOperatorVar1()
    {
        using (var seed = NewStore())
        {
            seed.UpsertVarOverride(RxMode.USB, "VAR1", 80, 3200);
        }

        using var reopened = NewStore();

        var usb = reopened.GetVarOverride(RxMode.USB, "VAR1");
        Assert.True(usb.HasValue);
        Assert.Equal(80, usb.Value.LowHz);
        Assert.Equal(3200, usb.Value.HighHz);
    }

    private static FilterPresetStore NewStore() =>
        new(NullLogger<FilterPresetStore>.Instance);
}
