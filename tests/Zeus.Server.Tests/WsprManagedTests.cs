// SPDX-License-Identifier: GPL-2.0-or-later
//
// Managed WSPR port (zeus-88xj) against the native wsprd oracle. Where the
// native library is not staged (Windows today) the oracle comparisons skip;
// the self-contained vectors still run everywhere.

using System.Text;
using Zeus.Server.Hosting.Digital;
using Zeus.Server.Hosting.Digital.Wspr;

namespace Zeus.Server.Tests;

public sealed class WsprManagedTests
{
    // Published lookup3 vectors (lookup3.c driver5 / hashlittle).
    [Theory]
    [InlineData("", 0u, 0xdeadbeefu)]
    [InlineData("", 0xdeadbeefu, 0xbd5b7ddeu)]
    [InlineData("Four score and seven years ago", 0u, 0x17770551u)]
    [InlineData("Four score and seven years ago", 1u, 0xcd628161u)]
    public void Lookup3_MatchesPublishedVectors(string key, uint init, uint want) =>
        Assert.Equal(want, Lookup3.HashLittle(Encoding.ASCII.GetBytes(key), init));

    public static TheoryData<string> Messages => new()
    {
        // type 1
        "K1ABC FN42 37", "EA5IUE IM76 23", "G4XYZ IO91 0", "VK2ZZ QF56 60",
        "W1AW FN31 33", "9A1A JN75 10", "A61AB LL75 27",
        // type 2 — prefixes and 1/2-character suffixes
        "PJ4/K1ABC 37", "F/G4XYZ 30", "VK9/W1AW 23", "K1ABC/P 30", "K1ABC/7 40", "EA5IUE/12 33",
        // type 3 — hashed call, 6-char grid (and a prefixed call)
        "<K1ABC> FN42AB 37", "<EA5IUE> IM76HE 23", "<PJ4/K1ABC> FK52UD 37",
    };

    [SkippableTheory]
    [MemberData(nameof(Messages))]
    public void Encoder_MatchesNative(string message)
    {
        Skip.IfNot(WsprNative.Available, "native wsprd not staged for this platform");
        var want = new byte[162];
        Assert.True(WsprNative.Encode(message, want));
        var got = new byte[162];
        Assert.True(WsprEncoder.TryEncode(message, got));
        Assert.Equal(want, got);
    }

    [Fact]
    public void Encoder_RefusesShapelessMessages()
    {
        var s = new byte[162];
        Assert.False(WsprEncoder.TryEncode("", s));
        Assert.False(WsprEncoder.TryEncode("HELLO", s));
        Assert.False(WsprEncoder.TryEncode("K1ABC FN42", s));     // no power
    }

    // Shapes the native encoder refuses, the managed one must refuse too
    // (a 3-character call is not type 1: the C requires the first space
    // after position 3).
    // (Only shapes the C survives: "HELLO" makes native get_wspr_channel_symbols
    // read a NULL grid token and crash the process — the managed encoder just
    // returns false, see Encoder_RefusesShapelessMessages.)
    [SkippableTheory]
    [InlineData("K1A FN42 30")]
    public void Encoder_RefusesWhatNativeRefuses(string message)
    {
        Skip.IfNot(WsprNative.Available, "native wsprd not staged for this platform");
        var s = new byte[162];
        Assert.False(WsprNative.Encode(message, s));
        Assert.False(WsprEncoder.TryEncode(message, s));
    }

    [Fact]
    public void Encoder_SymbolsCarryTheSyncVectorInTheirLowBit()
    {
        var s = new byte[162];
        Assert.True(WsprEncoder.TryEncode("K1ABC FN42 37", s));
        for (int i = 0; i < 162; i++)
        {
            Assert.InRange(s[i], 0, 3);
            Assert.Equal(WsprEncoder.Sync[i], (byte)(s[i] & 1));
        }
    }
}
