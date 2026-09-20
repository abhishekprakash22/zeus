// SPDX-License-Identifier: GPL-2.0-or-later
//
// Spotting uploaders: the WSPRnet query, the PSK Reporter IPFIX datagram, the
// message parsers that decide who gets reported, and the opt-in gate. Nothing
// here touches the network — the encoders are pure and the store is a temp DB.

using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.Hosting.Digital;

namespace Zeus.Server.Tests;

public sealed class SpottingUploaderTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-spotting-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private static readonly SpottingSettings Me =
        new(PskReporterEnabled: true, WsprnetEnabled: true, Callsign: "EA5IUE", Grid: "IM76");

    // ---- WSPRnet -------------------------------------------------------------

    [Fact]
    public void WsprnetUrl_CarriesTheSlotAndBothStations()
    {
        var spot = new WsprSpotDtoOut(SnrDb: -21.4, DtSec: 0.3, FreqMhz: 7.040123, DriftHz: -1.2,
            Message: "G4ABC IO91 37");
        var url = WsprnetUploader.BuildSpotUrl(
            spot, WsprnetUploader.ParseMessage(spot.Message)!, dialMhz: 7.038600,
            slotStartUtc: new DateTime(2026, 9, 20, 4, 6, 0, DateTimeKind.Utc), Me, "ANAN-Core 1.76");

        Assert.StartsWith("http://wsprnet.org/post?", url);
        foreach (var expected in new[]
        {
            "function=wspr", "rcall=EA5IUE", "rgrid=IM76", "rqrg=7.038600",
            "date=260920", "time=0406", "sig=-21", "dt=0.3", "drift=-1",
            "tqrg=7.040123", "tcall=G4ABC", "tgrid=IO91", "dbm=37", "mode=2",
        })
        {
            Assert.Contains(expected, url);
        }
        Assert.Contains("version=ANAN-Core%201.76", url);
    }

    [Theory]
    // Type 1: call, grid, power.
    [InlineData("G4ABC IO91 37", "G4ABC", "IO91", 37)]
    // Type 2: compound call, no grid.
    [InlineData("PJ4/K1ABC 30", "PJ4/K1ABC", null, 30)]
    // Type 3: hashed call in angle brackets + six-character grid.
    [InlineData("<PJ4/K1ABC> FK52UD 33", "PJ4/K1ABC", "FK52UD", 33)]
    public void WsprMessage_Parses(string message, string call, string? grid, int dbm)
    {
        var p = WsprnetUploader.ParseMessage(message);
        Assert.NotNull(p);
        Assert.Equal(call, p!.Callsign);
        Assert.Equal(grid, p.Grid);
        Assert.Equal(dbm, p.PowerDbm);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("G4ABC HELLO THERE")]
    public void WsprMessage_RejectsWhatItCannotAttribute(string message)
    {
        Assert.Null(WsprnetUploader.ParseMessage(message));
    }

    // ---- PSK Reporter --------------------------------------------------------

    [Fact]
    public void PskDatagram_HasIpfixHeaderBothTemplatesAndBothSets()
    {
        var spots = new[]
        {
            new PskSpot("G4ABC", 14_075_500, -12, "FT8", 1_789_000_000),
            new PskSpot("DL1XYZ", 14_074_900, 3, "FT8", 1_789_000_000),
        };
        var dg = PskReporterUploader.BuildDatagram(
            "EA5IUE", "IM76", "ANAN-Core", spots,
            sequence: 7, observationId: 0x1234_5678, exportTimeUnix: 1_789_000_123);

        Assert.Equal(0x000A, BinaryPrimitives.ReadUInt16BigEndian(dg.AsSpan(0, 2)));   // IPFIX version
        Assert.Equal(dg.Length, BinaryPrimitives.ReadUInt16BigEndian(dg.AsSpan(2, 2)));
        Assert.Equal(1_789_000_123u, BinaryPrimitives.ReadUInt32BigEndian(dg.AsSpan(4, 4)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(dg.AsSpan(8, 4)));
        Assert.Equal(0x1234_5678u, BinaryPrimitives.ReadUInt32BigEndian(dg.AsSpan(12, 4)));
        Assert.Equal(0, dg.Length % 4);                                                // sets stay aligned

        var text = Encoding.ASCII.GetString(dg);
        Assert.Contains("EA5IUE", text);
        Assert.Contains("IM76", text);
        Assert.Contains("ANAN-Core", text);
        Assert.Contains("G4ABC", text);
        Assert.Contains("DL1XYZ", text);

        // Both template sets present: 0x9992 (receiver) and 0x9993 (sender).
        Assert.Contains(FindSetHeaders(dg), h => h == 0x9992);
        Assert.Contains(FindSetHeaders(dg), h => h == 0x9993);
    }

    [Fact]
    public void PskDatagram_WithoutSpots_OmitsTheSenderTemplate()
    {
        var dg = PskReporterUploader.BuildDatagram(
            "EA5IUE", "IM76", "ANAN-Core", Array.Empty<PskSpot>(), 0, 1, 1_789_000_000);

        Assert.Equal(dg.Length, BinaryPrimitives.ReadUInt16BigEndian(dg.AsSpan(2, 2)));
        Assert.DoesNotContain(FindSetHeaders(dg), h => h == 0x9993);
        Assert.Contains(FindSetHeaders(dg), h => h == 0x9992);
    }

    private static List<ushort> FindSetHeaders(byte[] dg)
    {
        var found = new List<ushort>();
        for (int i = 16; i + 1 < dg.Length; i++)
        {
            ushort v = BinaryPrimitives.ReadUInt16BigEndian(dg.AsSpan(i, 2));
            if (v is 0x9992 or 0x9993) found.Add(v);
        }
        return found;
    }

    [Theory]
    [InlineData("CQ EA5IUE IM76", "EA5IUE")]
    [InlineData("CQ DX G4ABC IO91", "G4ABC")]
    [InlineData("EA5IUE G4ABC -15", "G4ABC")]          // the second call is the one on the air
    [InlineData("EA5IUE <G4ABC> RR73", "G4ABC")]
    public void SenderCallsign_IsTheStationTransmitting(string message, string expected)
    {
        Assert.Equal(expected, PskReporterUploader.ExtractSenderCallsign(message));
    }

    [Theory]
    [InlineData("")]
    [InlineData("CQ")]
    [InlineData("TNX 73 GL")]
    [InlineData("EA5IUE RR73")]
    [InlineData("EA5IUE IM76")]          // a grid is not a callsign
    public void SenderCallsign_IsNullWhenThereIsNone(string message)
    {
        Assert.Null(PskReporterUploader.ExtractSenderCallsign(message));
    }

    // ---- opt-in gate ---------------------------------------------------------

    [Fact]
    public void Settings_DefaultOff_PersistAndNormalise()
    {
        using var store = new SpottingSettingsStore(
            NullLogger<SpottingSettingsStore>.Instance, _dbPath);

        var initial = store.Get();
        Assert.False(initial.PskReporterEnabled);
        Assert.False(initial.WsprnetEnabled);
        Assert.False(initial.IdentityResolved);

        SpottingSettings? seen = null;
        store.Changed += s => seen = s;
        var saved = store.Set(new SpottingSettings(true, true, " ea5iue ", "im76hd"));

        Assert.Equal("EA5IUE", saved.Callsign);      // trimmed + upper-cased
        Assert.Equal("IM76HD", saved.Grid);
        Assert.True(saved.IdentityResolved);
        Assert.Equal(saved, seen);                   // uploaders are told to re-read
        Assert.Equal(saved, store.Get());            // and it survives a re-read
    }

    [Fact]
    public async Task NothingIsSent_WithoutCallsignAndGrid()
    {
        // Enabled but anonymous: both uploaders must stay silent. Neither call
        // touches the network, which is the point — they return before that.
        var anonymous = new SpottingSettings(true, true, "", "");
        Assert.False(anonymous.IdentityResolved);

        var psk = new PskReporterUploader(NullLogger.Instance);
        psk.Add(new PskSpot("G4ABC", 14_075_000, -10, "FT8", 1_789_000_000));
        await psk.FlushAsync(anonymous, "ANAN-Core", CancellationToken.None);
        Assert.Equal(0, psk.Uploaded);
        Assert.Equal(1, psk.PendingCount);           // kept, not sent

        var wsprnet = new WsprnetUploader(new HttpClient(), NullLogger.Instance);
        var batch = new WsprSpotBatch(0, 1_789_000_000_000, 7.0386,
            new[] { new WsprSpotDtoOut(-20, 0.2, 7.040, 0, "G4ABC IO91 37") });
        await wsprnet.UploadSlotAsync(batch, anonymous, "ANAN-Core", CancellationToken.None);
        Assert.Equal(0, wsprnet.Uploaded);
    }
}
