// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.Hosting.Support;

namespace Zeus.Server.Tests;

public sealed class SupportAgreementEndpointTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-support-agreement-endpoint-{Guid.NewGuid():N}.db");

    private SupportAvailabilityStore NewStore() =>
        new(NullLogger<SupportAvailabilityStore>.Instance, _dbPath);

    [Fact]
    public void BuildSupportAvailabilityResponse_IncludesAgreementVersions()
    {
        using var store = NewStore();

        var dto = ZeusEndpoints.BuildSupportAvailabilityResponse(store);

        Assert.False(dto.Available);
        Assert.False(dto.AutoShareCrashes);
        Assert.Equal(0, dto.AgreementVersion);
        Assert.Equal(
            SupportAvailabilityStore.CurrentAgreementVersion,
            dto.CurrentAgreementVersion);
    }

    [Fact]
    public void AnswerSupportAgreement_OptIn_TurnsOnBothFlags_AndReturnsVersions()
    {
        using var store = NewStore();

        var dto = ZeusEndpoints.AnswerSupportAgreement(
            new SupportAgreementRequest(OptIn: true),
            store,
            NullLogger.Instance);

        Assert.True(dto.Available);
        Assert.True(dto.AutoShareCrashes);
        Assert.Equal(
            SupportAvailabilityStore.CurrentAgreementVersion,
            dto.AgreementVersion);
        Assert.Equal(
            SupportAvailabilityStore.CurrentAgreementVersion,
            dto.CurrentAgreementVersion);
    }

    [Fact]
    public void AnswerSupportAgreement_OptOut_PreservesExistingFlags()
    {
        using var store = NewStore();
        store.Set(available: true, autoShareCrashes: true);

        var dto = ZeusEndpoints.AnswerSupportAgreement(
            new SupportAgreementRequest(OptIn: false),
            store,
            NullLogger.Instance);

        Assert.True(dto.Available);
        Assert.True(dto.AutoShareCrashes);
        Assert.Equal(
            SupportAvailabilityStore.CurrentAgreementVersion,
            dto.AgreementVersion);
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
        try { if (File.Exists(_dbPath + "-log")) File.Delete(_dbPath + "-log"); } catch { /* best effort */ }
    }
}
