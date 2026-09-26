// SPDX-License-Identifier: GPL-2.0-or-later
//
// Managed WSPR port (zeus-88xj). The encoder is checked against the native
// wsprsim symbols frozen into TestData/wspr/encoder-native.tsv before
// native/wspr was retired; everything here runs on every platform.

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

    public static TheoryData<string, string> NativeVectors()
    {
        var d = new TheoryData<string, string>();
        foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestData", "wspr", "encoder-native.tsv")))
        {
            var f = line.Split('\t');
            d.Add(f[0], f[1]);
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(NativeVectors))]
    public void Encoder_MatchesNativeSymbols(string message, string nativeSymbols)
    {
        var got = new byte[162];
        Assert.True(WsprEncoder.TryEncode(message, got));
        Assert.Equal(nativeSymbols, string.Concat(got.Select(b => (char)('0' + b))));
    }

    // The native wsprd kept ~0.8 MB on the stack and needed a 16 MB decode
    // thread (it killed the process on macOS's 512 KB default). The managed
    // decoder must run on the smallest ordinary thread.
    [Fact]
    public void Decoder_RunsOnASmallThreadStack()
    {
        const int samples = WsprDecoder.SlotSamples;
        var i = new float[samples];
        var q = new float[samples];
        var rng = new Random(3);
        for (int k = 0; k < samples; k++) { i[k] = (float)(rng.NextDouble() - 0.5); q[k] = (float)(rng.NextDouble() - 0.5); }

        List<WsprDecode>? spots = null;
        var t = new Thread(() => spots = WsprDecoder.Decode(i, q, 14_095_600), 512 * 1024);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), "WSPR decode did not finish");
        Assert.NotNull(spots);
        Assert.Empty(spots!);                       // noise: no spots, clean return
    }

    [Fact]
    public void Encoder_RefusesShapelessMessages()
    {
        var s = new byte[162];
        Assert.False(WsprEncoder.TryEncode("", s));
        Assert.False(WsprEncoder.TryEncode("HELLO", s));
        Assert.False(WsprEncoder.TryEncode("K1ABC FN42", s));     // no power
    }

    // The native encoder refused a 3-character call as type 1 (the C
    // requires the first space after position 3); the managed one must too.
    [Fact]
    public void Encoder_RefusesWhatNativeRefused() =>
        Assert.False(WsprEncoder.TryEncode("K1A FN42 30", new byte[162]));

    // ---- Fano + unpack (zeus-88xj.2) ----------------------------------------

    /// <summary>Soft symbols as wspr_decode's mode-2 demodulator hands them
    /// over (data bit 1 → above 128), optionally noisy.</summary>
    private static byte[] SoftSymbols(string message, double noiseRms = 0, int seed = 1)
    {
        var ch = new byte[162];
        Assert.True(WsprEncoder.TryEncode(message, ch));
        var rng = new Random(seed);
        var soft = new byte[162];
        for (int i = 0; i < 162; i++)
        {
            double v = (ch[i] >> 1) == 1 ? 60 : -60;
            if (noiseRms > 0)
            {
                double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
                v += noiseRms * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            }
            soft[i] = (byte)Math.Clamp(Math.Round(v) + 128, 0, 255);
        }
        return soft;
    }

    private static WsprMessage? DecodeSoft(byte[] soft, WsprHashTable table)
    {
        var sym = (byte[])soft.Clone();
        WsprUnpack.Deinterleave(sym);
        var data = new byte[11];
        if (!WsprFano.Decode(sym, 81, 60, 10_000, data, out _, out _)) return null;
        return WsprUnpack.Unpack(data, table);
    }

    [Theory]
    [InlineData("K1ABC FN42 37", "K1ABC FN42 37")]
    [InlineData("EA5IUE IM76 23", "EA5IUE IM76 23")]
    [InlineData("G4XYZ IO91 0", "G4XYZ IO91 00")]          // type 1 prints %02d
    [InlineData("PJ4/K1ABC 37", "PJ4/K1ABC 37")]
    [InlineData("K1ABC/P 30", "K1ABC/P 30")]
    [InlineData("EA5IUE/12 33", "EA5IUE/12 33")]
    [InlineData("F/G4XYZ 7", "F/G4XYZ  7")]                 // types 2/3 print %2d
    public void FanoAndUnpack_RoundTrip(string sent, string want)
    {
        var got = DecodeSoft(SoftSymbols(sent), new WsprHashTable());
        Assert.NotNull(got);
        Assert.False(got!.Value.NoPrint);
        Assert.Equal(want, got.Value.CallLocPow);
    }

    [Fact]
    public void Type3_NamesTheSender_OnceTheCallWasHeardUnhashed()
    {
        var table = new WsprHashTable();
        Assert.Equal("<...> FN42AB 37", DecodeSoft(SoftSymbols("<K1ABC> FN42AB 37"), table)!.Value.CallLocPow);
        DecodeSoft(SoftSymbols("K1ABC FN42 37"), table);                      // type 1 fills the table
        Assert.Equal("<K1ABC> FN42AB 37", DecodeSoft(SoftSymbols("<K1ABC> FN42AB 37"), table)!.Value.CallLocPow);
    }

    [Fact]
    public void Fano_CorrectsNoisySoftSymbols()
    {
        // rms 50 against ±60 signal: many raw bit errors, the code still decodes.
        int ok = 0;
        for (int seed = 0; seed < 20; seed++)
            if (DecodeSoft(SoftSymbols("EA5IUE IM76 23", 50, seed), new WsprHashTable())?.CallLocPow == "EA5IUE IM76 23")
                ok++;
        Assert.True(ok >= 18, $"{ok}/20");
    }
}
