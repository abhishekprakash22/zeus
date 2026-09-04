// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeus.Contracts;

namespace Zeus.Server.Tests;

// NR5 (NNR, WDSP 2.1.0) tunables round-trip through DspSettingsStore the same
// way the NR4 block does (Nr4SettingsPersistenceTests): null means "engine
// default", and the Nnr mode itself must survive a store round-trip rather
// than being normalised to Off.
public class NnrSettingsPersistenceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-nnr-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private DspSettingsStore BuildStore() =>
        new(NullLogger<DspSettingsStore>.Instance, _dbPath);

    [Fact]
    public void SetNnrConfig_PersistsModeAndFields()
    {
        var cfg = new NrConfig(NrMode: NrMode.Nnr, NnrMaskFloorDb: -32.5, NnrModelSlot: 1);

        using (var store = BuildStore())
        {
            store.Upsert(cfg);
        }

        using (var store = BuildStore())
        {
            var back = store.Get();
            Assert.NotNull(back);
            Assert.Equal(NrMode.Nnr, back!.NrMode);
            Assert.Equal(-32.5, back.NnrMaskFloorDb);
            Assert.Equal(1, back.NnrModelSlot);
        }
    }

    [Fact]
    public void GetNnrConfig_NullFields_ReturnNullForDefaultFallback()
    {
        using (var store = BuildStore())
        {
            store.Upsert(new NrConfig(NrMode: NrMode.Nnr));
        }

        using (var store = BuildStore())
        {
            var back = store.Get();
            Assert.NotNull(back);
            Assert.Equal(NrMode.Nnr, back!.NrMode);
            Assert.Null(back.NnrMaskFloorDb);
            Assert.Null(back.NnrModelSlot);
        }
    }

    [Fact]
    public void UpsertNnr_ThenUpsertOtherMode_KeepsNnrTunables()
    {
        using (var store = BuildStore())
        {
            store.Upsert(new NrConfig(NrMode: NrMode.Nnr, NnrMaskFloorDb: -40.0, NnrModelSlot: 0));
            var cur = store.Get()!;
            store.Upsert(cur with { NrMode = NrMode.Emnr });
        }

        using (var store = BuildStore())
        {
            var back = store.Get()!;
            Assert.Equal(NrMode.Emnr, back.NrMode);
            Assert.Equal(-40.0, back.NnrMaskFloorDb);
            Assert.Equal(0, back.NnrModelSlot);
        }
    }
}
