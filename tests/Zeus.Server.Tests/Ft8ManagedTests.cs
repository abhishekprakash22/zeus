// SPDX-License-Identifier: GPL-2.0-or-later
//
// Managed FT8/FT4 port (zeus-wi0p). The pack/encode and unpack paths are
// integer code, so they must match ft8_lib exactly: the expected values are
// the native library's own output, frozen into TestData/ft8 by
// native/ft8/tests/encoder_dump.c (see docs/designs/ft8-managed-port.md).

using Zeus.Server.Hosting.Digital;
using Zeus.Server.Hosting.Digital.Ft8;

namespace Zeus.Server.Tests;

public sealed class Ft8ManagedTests
{
    private static string TestData(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "ft8", name);

    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    private static string Digits(ReadOnlySpan<byte> b)
    {
        var c = new char[b.Length];
        for (int i = 0; i < b.Length; i++) c[i] = (char)('0' + b[i]);
        return new string(c);
    }

    public static TheoryData<string, int, string, string, string> EncoderVectors()
    {
        var d = new TheoryData<string, int, string, string, string>();
        foreach (var line in File.ReadAllLines(TestData("encoder-native.tsv")))
        {
            var f = line.Split('\t');
            d.Add(f[0], int.Parse(f[1]), f[2], f[3], f[4]);
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(EncoderVectors))]
    public void Encoder_MatchesNative(string message, int rc, string payload, string ft8Tones, string ft4Tones)
    {
        var p = new byte[FtxMessage.PayloadBytes];
        Assert.Equal(rc, (int)FtxMessage.Encode(message, null, p));
        if (rc != 0) return;

        Assert.Equal(payload, Hex(p));

        var t8 = new byte[FtxConstants.Ft8Nn];
        FtxEncoder.Ft8Tones(p, t8);
        Assert.Equal(ft8Tones, Digits(t8));

        var t4 = new byte[FtxConstants.Ft4Nn];
        FtxEncoder.Ft4Tones(p, t4);
        Assert.Equal(ft4Tones, Digits(t4));
    }

    [Fact]
    public void Unpack_MatchesNativeOnRandomPayloads()
    {
        var lines = File.ReadAllLines(TestData("unpack-native.tsv"));
        Assert.True(lines.Length >= 1000);
        foreach (var line in lines)
        {
            var f = line.Split('\t');
            var p = Convert.FromHexString(f[0]);
            var rc = FtxMessage.Decode(p, null, out string text);
            Assert.True(int.Parse(f[1]) == (int)rc, $"{f[0]}: rc {(int)rc}, native {f[1]}");
            if (rc == FtxMessageRc.Ok) Assert.True(f[2] == text, $"{f[0]}: '{text}', native '{f[2]}'");
        }
    }

    [Theory]
    [InlineData("CQ EA5IUE IM76")]
    [InlineData("EA5IUE W1AW -15")]
    [InlineData("W1AW EA5IUE R-09")]
    [InlineData("EA5IUE W1AW RR73")]
    [InlineData("CQ POTA EA5IUE IM76")]
    [InlineData("CQ 123 K1ABC FN42")]
    [InlineData("PA3XYZ/P GM4ABC/P JO22")]
    [InlineData("TNX BOB 73 GL")]
    public void Encode_ThenDecode_RoundTrips(string message)
    {
        var p = new byte[FtxMessage.PayloadBytes];
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Encode(message, null, p));
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Decode(p, null, out string text));
        Assert.Equal(message, text);
    }

    // A non-standard call is sent in full once; later messages carry only its
    // hash, which the session-long table resolves.
    [Fact]
    public void CallsignTable_ResolvesAHashedCallFromAnEarlierMessage()
    {
        var table = new FtxCallsignTable();
        var p = new byte[FtxMessage.PayloadBytes];

        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Encode("K1ABC PJ4/W9XYZ", null, p));
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Decode(p, table, out string before));
        Assert.Equal("K1ABC <...>", before);

        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Encode("CQ PJ4/W9XYZ", null, p));
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Decode(p, table, out string cq));
        Assert.Equal("CQ PJ4/W9XYZ", cq);

        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Encode("K1ABC PJ4/W9XYZ", null, p));
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Decode(p, table, out string after));
        Assert.Equal("K1ABC <PJ4/W9XYZ>", after);
    }

    [Fact]
    public void Crc_OfAnEncodedMessage_ChecksOut()
    {
        var p = new byte[FtxMessage.PayloadBytes];
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Encode("CQ K1ABC FN42", null, p));
        var a91 = new byte[FtxConstants.LdpcKBytes];
        FtxCrc.Add(p, a91);
        ushort sent = FtxCrc.Extract(a91);
        a91[9] &= 0xF8;
        a91[10] = 0;
        a91[11] = 0;
        Assert.Equal(sent, FtxCrc.Compute(a91, 96 - 14));
    }

    [Theory]
    [InlineData(0.5f, 0.5204998778)]
    [InlineData(1.0f, 0.8427007929)]
    [InlineData(2.0f, 0.9953222650)]
    [InlineData(2.9f, 0.9999589021)]
    [InlineData(3.1f, 0.9999883513)]
    [InlineData(4.0f, 0.9999999846)]
    [InlineData(-1.0f, -0.8427007929)]
    [InlineData(-3.5f, -0.9999992569)]
    [InlineData(0.0f, 0.0)]
    public void Erf_MatchesReferenceValues(float x, double want) =>
        Assert.Equal(want, FtxSynth.Erf(x), 1e-7);

    // Waveform against the native synth. The float maths differ only in
    // library sinf/erff rounding, so the samples agree closely; the hard check
    // is that the native decoder reads the managed audio back.
    [SkippableTheory]
    [InlineData("CQ EA5IUE IM76", false, 1500f, 48000)]
    [InlineData("W1AW EA5IUE R-09", false, 800f, 48000)]
    [InlineData("CQ EA5IUE IM76", true, 1200f, 48000)]
    [InlineData("EA5IUE W1AW RR73", true, 2000f, 12000)]
    [InlineData("CQ PJ4/K1ABC", false, 1000f, 12000)]
    public void Synth_MatchesNative(string message, bool isFt4, float audioHz, int rate)
    {
        Skip.IfNot(Ft8Native.Available, "libzeus_ft8 not staged for this platform");

        var native = Ft8Native.Synth(message, isFt4, audioHz, rate, out string? error);
        Assert.NotNull(native);
        var managed = FtxSynth.Synth(message, isFt4, audioHz, rate, null, out var rc);
        Assert.Equal(FtxMessageRc.Ok, rc);
        Assert.NotNull(managed);
        Assert.Equal(FtxSynth.WaveSamples(isFt4, rate), managed!.Length);
        Assert.Equal(native!.Length, managed.Length);

        double maxDiff = 0;
        for (int i = 0; i < managed.Length; i++) maxDiff = Math.Max(maxDiff, Math.Abs(managed[i] - native[i]));
        Assert.True(maxDiff < 1e-3, $"max |managed - native| = {maxDiff}");

        // Place it in a slot (0.5 s in, light noise) and let native decode it.
        int slot = (int)((isFt4 ? 7.5 : 15.0) * rate);
        var audio = new float[slot];
        var rng = new Random(1);
        for (int i = 0; i < slot; i++) audio[i] = (float)(rng.NextDouble() - 0.5) * 0.02f;
        int start = rate / 2;
        for (int i = 0; i < managed.Length && start + i < slot; i++) audio[start + i] += 0.3f * managed[i];
        var decodes = Ft8Native.Decode(audio, rate, isFt4);
        Assert.Contains(decodes, d => d.Text == message);
    }

    [Fact]
    public void Synth_RefusesWhatTheEncoderRefuses()
    {
        Assert.Null(FtxSynth.Synth("THIS IS TOO LONG FOR FREE TEXT", false, 1500f, 48000, null, out var rc));
        Assert.Equal(FtxMessageRc.ErrorType, rc);
    }

    // ---- decoder -----------------------------------------------------------

    /// <summary>A slot with several transmissions at chosen levels, offsets
    /// and frequencies, in Gaussian noise (fixed seed).</summary>
    internal static float[] SynthSlot(bool isFt4, int rate, int seed, double noiseRms,
                                      params (string msg, float hz, float dt, float amp)[] txs)
    {
        int slot = (int)((isFt4 ? 7.5 : 15.0) * rate);
        var audio = new float[slot];
        var rng = new Random(seed);
        for (int i = 0; i < slot; i += 2)
        {
            // Box-Muller
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            double r = Math.Sqrt(-2 * Math.Log(u1)) * noiseRms;
            audio[i] = (float)(r * Math.Cos(2 * Math.PI * u2));
            if (i + 1 < slot) audio[i + 1] = (float)(r * Math.Sin(2 * Math.PI * u2));
        }
        foreach (var (msg, hz, dt, amp) in txs)
        {
            var w = FtxSynth.Synth(msg, isFt4, hz, rate, null, out var rc)!;
            Assert.Equal(FtxMessageRc.Ok, rc);
            int start = (int)(dt * rate);
            for (int i = 0; i < w.Length; i++)
            {
                int j = start + i;
                if (j >= 0 && j < slot) audio[j] += amp * w[i];
            }
        }
        return audio;
    }

    public static TheoryData<bool, int> DecoderSlots => new()
    {
        { false, 1 }, { false, 2 }, { false, 3 }, { true, 4 }, { true, 5 },
    };

    [SkippableTheory]
    [MemberData(nameof(DecoderSlots))]
    public void Decoder_MatchesNativeOnSynthesizedSlots(bool isFt4, int seed)
    {
        Skip.IfNot(Ft8Native.Available, "libzeus_ft8 not staged for this platform");
        const int rate = 48000;
        float t0 = isFt4 ? 0.3f : 0.5f;
        var audio = SynthSlot(isFt4, rate, seed, 0.1,
            ("CQ EA5IUE IM76", 600f + 7 * seed, t0, 0.20f),
            ("W1AW EA5IUE R-09", 900f, t0 + 0.1f, 0.05f),
            ("EA5IUE W1AW RR73", 1210f, t0 - 0.2f, 0.02f),
            ("CQ DX K1ABC FN42", 1523f, t0 + 0.4f, 0.012f),
            ("K1ABC W9XYZ EN37", 1876f + seed, t0, 0.008f),
            ("W9XYZ K1ABC -11", 2140f, t0 + 0.8f, 0.006f),
            ("CQ POTA EA5HYW IM98", 2400f, t0 - 0.4f, 0.004f),
            ("TNX BOB 73 GL", 350f, t0 + 0.2f, 0.003f));

        var native = Ft8Native.Decode(audio, rate, isFt4);
        var managed = FtxDecoder.Decode(audio, rate, isFt4, null);

        string Show(IEnumerable<string> xs) => string.Join(" | ", xs);
        var n = native.Select(d => $"{d.Text}@{d.FreqHz}/{d.DtSec:F2}/{d.SnrDb}/{d.Score}").ToList();
        var m = managed.Select(d => $"{d.Text}@{(int)Math.Round(d.FreqHz)}/{Math.Round(d.DtSec, 2):F2}/{d.SnrDb}/{d.Score}").ToList();
        Assert.True(n.SequenceEqual(m), $"native:  {Show(n)}\nmanaged: {Show(m)}");
        Assert.True(managed.Count >= 4, $"only {managed.Count} decodes: {Show(m)}");
    }
}
