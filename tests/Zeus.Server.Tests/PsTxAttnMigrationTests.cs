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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Xunit;

namespace Zeus.Server.Tests;

/// <summary>
/// PsSettingsStore.MigrateTxAttnPoison — the one-time cleanup of persisted
/// single-ADC TX feedback-attenuation entries poisoned by the
/// PR #1249/#1283 testers builds (a defective auto-acquire ratcheted the
/// value to 31 dB — a deaf feedback ADC — and re-applied it on every
/// connect; #1248/#1285). The migration must wipe ONLY the single-ADC
/// attn keys covered by that poison class, exactly once, and never eat a
/// value calibrated after the poison window.
/// </summary>
public class PsTxAttnMigrationTests : IDisposable
{
    private readonly string _dbPath;

    public PsTxAttnMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-psmig-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private PsSettingsStore OpenStore()
        => new(NullLogger<PsSettingsStore>.Instance, _dbPath);

    private static string C10P2Key => RadioService.GetPsBoardKey(true, HpsdrBoardKind.HermesC10, OrionMkIIVariant.G2);
    private static string C10P1Key => RadioService.GetPsBoardKey(false, HpsdrBoardKind.HermesC10, OrionMkIIVariant.G2);
    private static string HermesIIP2Key => RadioService.GetPsBoardKey(true, HpsdrBoardKind.HermesII, OrionMkIIVariant.G2);
    private static string HermesIIP1Key => RadioService.GetPsBoardKey(false, HpsdrBoardKind.HermesII, OrionMkIIVariant.G2);
    private static string G2Key => RadioService.GetPsBoardKey(true, HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2);

    [Fact]
    public void Migration_V0ClearsHermesC10AndHermesII_KeepsOtherBoards()
    {
        // Seed a store shaped like the poisoned single-ADC field records:
        // HermesC10 and HermesII attn ratcheted on both protocols, plus a
        // healthy G2 value and an operator hwPeak calibration that must survive.
        using (var store = OpenStore())
        {
            var entry = store.Get() ?? new PsSettingsEntry();
            entry.TxAttnByBoard[C10P2Key] = 31;
            entry.TxAttnByBoard[C10P1Key] = 31;
            entry.TxAttnByBoard[HermesIIP2Key] = 30;
            entry.TxAttnByBoard[HermesIIP1Key] = 29;
            entry.TxAttnByBoard[G2Key] = 12;
            entry.HwPeakByBoard[C10P2Key] = 0.31;   // operator cal — NOT poison
            entry.TxAttnMigration = 0;              // pre-migration record
            store.Upsert(entry);
        }

        // Re-open — the constructor migration pass runs.
        using (var store = OpenStore())
        {
            var entry = store.Get();
            Assert.NotNull(entry);
            Assert.False(entry!.TxAttnByBoard.ContainsKey(C10P2Key)); // wiped
            Assert.False(entry.TxAttnByBoard.ContainsKey(C10P1Key));  // wiped
            Assert.False(entry.TxAttnByBoard.ContainsKey(HermesIIP2Key));
            Assert.False(entry.TxAttnByBoard.ContainsKey(HermesIIP1Key));
            Assert.Equal(12, entry.TxAttnByBoard[G2Key]);             // survives
            Assert.Equal(0.31, entry.HwPeakByBoard[C10P2Key]);        // hwPeak untouched
            Assert.Equal(PsSettingsStore.TxAttnMigrationCurrent, entry.TxAttnMigration);
        }
    }

    [Fact]
    public void Migration_V1ClearsOnlyHermesII_PostHardeningHermesC10Survives()
    {
        using (var store = OpenStore())
        {
            var entry = store.Get() ?? new PsSettingsEntry();
            entry.TxAttnByBoard[C10P2Key] = 24;
            entry.TxAttnByBoard[C10P1Key] = 25;
            entry.TxAttnByBoard[HermesIIP2Key] = 30;
            entry.TxAttnByBoard[HermesIIP1Key] = 29;
            entry.TxAttnByBoard[G2Key] = 12;
            entry.TxAttnMigration = 1;              // already passed the HermesC10 step
            store.Upsert(entry);
        }

        using (var store = OpenStore())
        {
            var entry = store.Get();
            Assert.NotNull(entry);
            Assert.Equal(24, entry!.TxAttnByBoard[C10P2Key]);
            Assert.Equal(25, entry.TxAttnByBoard[C10P1Key]);
            Assert.False(entry.TxAttnByBoard.ContainsKey(HermesIIP2Key));
            Assert.False(entry.TxAttnByBoard.ContainsKey(HermesIIP1Key));
            Assert.Equal(12, entry.TxAttnByBoard[G2Key]);
            Assert.Equal(PsSettingsStore.TxAttnMigrationCurrent, entry.TxAttnMigration);
        }
    }

    [Fact]
    public void Migration_V2IsIdempotent_PostMigrationCalibrationSurvives()
    {
        // First open stamps the (empty) store at the current version.
        using (var store = OpenStore())
        {
            var entry = store.Get() ?? new PsSettingsEntry();
            entry.TxAttnMigration = PsSettingsStore.TxAttnMigrationCurrent;
            // A value calibrated AFTER the poison window — the two-tone servo's
            // post-calibration persist writes exactly this shape.
            entry.TxAttnByBoard[C10P2Key] = 24;
            entry.TxAttnByBoard[HermesIIP2Key] = 23;
            entry.TxAttnByBoard[G2Key] = 12;
            store.Upsert(entry);
        }

        // Every subsequent open must leave it alone.
        using (var store = OpenStore())
        {
            var entry = store.Get();
            Assert.NotNull(entry);
            Assert.Equal(24, entry!.TxAttnByBoard[C10P2Key]);
            Assert.Equal(23, entry.TxAttnByBoard[HermesIIP2Key]);
            Assert.Equal(12, entry.TxAttnByBoard[G2Key]);
            Assert.Equal(PsSettingsStore.TxAttnMigrationCurrent, entry.TxAttnMigration);
        }
    }

    [Fact]
    public void PersistPsState_CarriesMigrationMarker_SoRestartDoesNotRewipe()
    {
        // RadioService.PersistPsState rebuilds the whole entry — it must carry
        // TxAttnMigration forward (and stamp brand-new entries at the current
        // version) or the next startup re-runs the wipe and eats a freshly
        // calibrated HermesC10 value.
        using (var store = OpenStore())
        {
            var radio = new RadioService(
                NullLoggerFactory.Instance,
                new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath),
                new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath),
                filterPresetStore: null, txIqSource: null,
                preferredRadioStore: null, psStore: store);
            // Simulate a P2 G2E connect so the board key is cached, then a
            // calibrated attenuation persist (what the two-tone servo does).
            radio.ApplyPsHwPeakForConnection(isProtocol2: true, board: HpsdrBoardKind.HermesC10);
            radio.SetPsTxAttenuationDb(24);

            var entry = store.Get();
            Assert.NotNull(entry);
            Assert.Equal(24, entry!.TxAttnByBoard[C10P2Key]);
            Assert.Equal(PsSettingsStore.TxAttnMigrationCurrent, entry.TxAttnMigration);
        }

        // Restart: the calibrated value must survive the migration pass.
        using (var store = OpenStore())
        {
            var entry = store.Get();
            Assert.NotNull(entry);
            Assert.Equal(24, entry!.TxAttnByBoard[C10P2Key]);
        }
    }
}
