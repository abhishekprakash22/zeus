// SPDX-License-Identifier: GPL-2.0-or-later
//
// The software string is how PSK Reporter's and WSPRnet's operators reach us
// if this station ever starts sending them something wrong (abhishekprakash22/
// zeus#12 review). Several spellings of one program made that handle useless,
// so these tests pin the single name and check that the reporter paths reach
// it rather than each spelling its own.

using System.Net.Http.Headers;
using System.Reflection;
using Zeus.Contracts;
using Zeus.Server.Hosting;
using Zeus.Server.Hosting.Digital;
using Zeus.Server.Wsjtx;

namespace Zeus.Server.Tests;

public sealed class SoftwareIdentityTests
{
    [Fact]
    public void Name_IsTheOneApacheLabsBuildName()
    {
        Assert.Equal("ANAN Core", SoftwareIdentity.Name);
    }

    [Fact]
    public void Version_IsTheBuildsOwn_WithoutTheMetadataSuffix()
    {
        var informational = typeof(SoftwareIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

        Assert.Equal(informational.Split('+')[0], SoftwareIdentity.Version);
        Assert.DoesNotContain('+', SoftwareIdentity.Version);
    }

    [Fact]
    public void NameWithVersion_CarriesBoth()
    {
        Assert.StartsWith("ANAN Core", SoftwareIdentity.NameWithVersion, StringComparison.Ordinal);
        if (SoftwareIdentity.Version.Length > 0)
            Assert.EndsWith(SoftwareIdentity.Version, SoftwareIdentity.NameWithVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void UserAgent_IsAValidProductToken()
    {
        // RFC 9110: a product token takes no spaces. The hyphenated spelling
        // exists only here — it must still be recognisably the same program.
        Assert.DoesNotContain(' ', SoftwareIdentity.UserAgent);
        Assert.StartsWith("ANAN-Core/", SoftwareIdentity.UserAgent, StringComparison.Ordinal);
        Assert.True(ProductInfoHeaderValue.TryParse(SoftwareIdentity.UserAgent, out _));
    }

    [Fact]
    public void PskReporterAndWsprnet_ReportTheSharedName()
    {
        // One helper feeds both networks; the review asked for this exact string.
        Assert.Equal(SoftwareIdentity.NameWithVersion, SpottingService.SoftwareVersion());
    }

    [Fact]
    public void N1mmBroadcast_ReportsTheSharedName()
    {
        var entry = new LogEntry(
            Id: "qso-1",
            QsoDateTimeUtc: new DateTime(2026, 9, 21, 4, 6, 0, DateTimeKind.Utc),
            Callsign: "G4ABC",
            Name: null,
            FrequencyMhz: 14.074,
            Band: "20M",
            Mode: "FT8",
            RstSent: "-12",
            RstRcvd: "-07",
            Grid: null,
            Country: null,
            Dxcc: null,
            CqZone: null,
            ItuZone: null,
            State: null,
            Comment: null,
            CreatedUtc: new DateTime(2026, 9, 21, 4, 6, 1, DateTimeKind.Utc));

        var xml = N1mmContactInfoEncoder.EncodeXml(entry, myCall: "EA5IUE");

        Assert.Contains($"<app>{SoftwareIdentity.Name}</app>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain(">Zeus<", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyAdifExport_NamesTheProgramAndTheRealVersion()
    {
        var adif = LogbookPluginMappings.EmptyAdifExport();

        // The ADIF length prefix counts bytes, so it has to track the name.
        Assert.Contains("<PROGRAMID:9>ANAN Core", adif, StringComparison.Ordinal);
        Assert.DoesNotContain("PROGRAMVERSION:5>1.0.0", adif, StringComparison.Ordinal);
        if (SoftwareIdentity.Version.Length > 0)
            Assert.Contains(SoftwareIdentity.Version, adif, StringComparison.Ordinal);
    }
}
