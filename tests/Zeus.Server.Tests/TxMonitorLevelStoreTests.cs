// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// MON level persistence: default 0 dB, attenuation-only clamp, survives a
// restart, Changed fires only on a real change.

using Microsoft.Extensions.Logging.Abstractions;

namespace Zeus.Server.Tests;

public class TxMonitorLevelStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-monlvl-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private TxMonitorLevelStore BuildStore() =>
        new(NullLogger<TxMonitorLevelStore>.Instance, _dbPath);

    [Fact]
    public void FreshDb_DefaultsToUnity()
    {
        using var store = BuildStore();
        Assert.Equal(0.0, store.LevelDb);
    }

    [Fact]
    public void Set_RoundTripsThroughDb()
    {
        using (var store = BuildStore())
            store.Set(-18);
        using (var reopened = BuildStore())
            Assert.Equal(-18.0, reopened.LevelDb);
    }

    [Theory]
    [InlineData(6.0, 0.0)]
    [InlineData(-80.0, -40.0)]
    [InlineData(double.NaN, 0.0)]
    [InlineData(-12.0, -12.0)]
    public void Set_ClampsToAttenuationOnly(double requested, double expected)
    {
        using var store = BuildStore();
        Assert.Equal(expected, store.Set(requested));
        Assert.Equal(expected, store.LevelDb);
    }

    [Fact]
    public void Changed_FiresOnlyOnTransition()
    {
        using var store = BuildStore();
        int fired = 0;
        store.Changed += _ => fired++;
        store.Set(-10);
        store.Set(-10);
        store.Set(-20);
        Assert.Equal(2, fired);
    }
}
