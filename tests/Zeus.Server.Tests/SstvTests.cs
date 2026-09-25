// SPDX-License-Identifier: GPL-2.0-or-later
//
// SSTV decoder tests. No radio, no native code: SstvEncoder synthesises the
// audio, the decoder must get the picture back — through noise, a mistuned
// receiver and an off-nominal sender clock (the slant case).

using Zeus.Server.Hosting.Digital.Sstv;

namespace Zeus.Server.Tests;

public sealed class SstvTests
{
    private const int Rate = SstvDecoder.SampleRate;

    // Published line times (ms), JL Barber / slowrx. The table builds these
    // from sync/porch/pixel figures; a typo there must fail here.
    [Theory]
    [InlineData("Martin 1", 446.446)]
    [InlineData("Martin 2", 226.798)]
    [InlineData("Scottie 1", 428.22)]
    [InlineData("Scottie 2", 277.692)]
    [InlineData("Scottie DX", 1050.3)]
    [InlineData("PD 50", 388.16)]
    [InlineData("PD 90", 703.04)]
    [InlineData("PD 120", 508.48)]
    [InlineData("PD 160", 804.416)]
    [InlineData("PD 180", 754.24)]
    [InlineData("PD 240", 1000.0)]
    [InlineData("PD 290", 937.28)]
    public void LineTimes_MatchPublishedSpec(string name, double lineMs)
    {
        var m = SstvModes.ByName(name);
        Assert.NotNull(m);
        Assert.Equal(lineMs, m!.LineMs, 3);
    }

    [Fact]
    public void VisCodes_AreUnique()
    {
        Assert.Equal(SstvModes.All.Count, SstvModes.All.Select(m => m.VisCode).Distinct().Count());
    }

    public static IEnumerable<object[]> AllModes() => SstvModes.All.Select(m => new object[] { m.Name });

    [Theory]
    [MemberData(nameof(AllModes))]
    public void RoundTrip_Clean_EveryMode(string name)
    {
        var mode = SstvModes.ByName(name)!;
        var img = TestCard(mode);
        var r = Decode(mode, img);

        Assert.Equal(SstvEndReason.Complete, r.Reason);
        Assert.Same(mode, r.Image.Mode);
        Assert.Equal(mode.Height, r.Image.RowsDone);
        Assert.InRange(MeanAbsError(mode, img, r.Image.Rgb), 0, 10);
    }

    // Noise is scaled to the pixel time: PD 120's 0.19 ms pixels average ~2
    // samples each, Martin 1's 0.46 ms ~5.5, so equal noise is not an equal
    // test. (Timing and clock recovery are exact at any of these levels.)
    [Theory]
    [InlineData("Martin 1", 0.25f)]
    [InlineData("Scottie 1", 0.25f)]
    [InlineData("PD 120", 0.12f)]
    public void RoundTrip_SurvivesNoiseOffsetAndSlant(string name, float noise)
    {
        var mode = SstvModes.ByName(name)!;
        var img = TestCard(mode);
        // +120 Hz mistune, sender clock +0.3 % (a visibly slanted picture if
        // uncorrected: ~130 px of skew over a Martin 1 frame), noise.
        var r = Decode(mode, img, offsetHz: 120, clockScale: 1.003, noise: noise);

        Assert.Equal(SstvEndReason.Complete, r.Reason);
        Assert.Equal(120, r.Image.OffsetHz, 0);
        Assert.Equal(0.003, r.Image.ClockError, 3);
        Assert.InRange(MeanAbsError(mode, img, r.Image.Rgb), 0, 18);
    }

    [Fact]
    public void Noise_Alone_NeverStartsAPicture()
    {
        var dec = new SstvDecoder();
        int started = 0;
        dec.ImageStarted += _ => started++;
        var rng = new Random(3);
        var noise = new float[60 * Rate];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() - 0.5);
        dec.Process(noise);
        Assert.Equal(0, started);
    }

    [Fact]
    public void SenderStoppingMidPicture_EndsAsSignalLost_WithPartialRows()
    {
        var mode = SstvModes.M1;
        var audio = SstvEncoder.Encode(mode, TestCard(mode), Rate);
        int cut = (int)((SstvEncoder.VisMs + 100 * mode.LineMs) * Rate / 1000);
        var dec = new SstvDecoder();
        SstvEndReason? reason = null;
        SstvImage? got = null;
        dec.ImageEnded += (i, why) => { got = i; reason = why; };
        dec.Process(Pad(audio.AsSpan(0, cut).ToArray(), 0.5, 30, 0.05f));

        Assert.Equal(SstvEndReason.SignalLost, reason);
        Assert.InRange(got!.RowsDone, 95, 101);
    }

    [Fact]
    public void RowsStreamLive_InOrder()
    {
        var mode = SstvModes.S2;
        var audio = Pad(SstvEncoder.Encode(mode, TestCard(mode), Rate), 1, 1, 0.01f);
        var dec = new SstvDecoder();
        var rows = new List<int>();
        dec.RowsDecoded += (_, first, count) => rows.Add(first);
        // Feed in small blocks, as the audio thread does.
        for (int i = 0; i < audio.Length; i += 512)
            dec.Process(audio.AsSpan(i, Math.Min(512, audio.Length - i)));
        Assert.Equal(Enumerable.Range(0, mode.Height), rows);
    }

    // ---- helpers ------------------------------------------------------------

    private sealed record Result(SstvImage Image, SstvEndReason Reason);

    private static Result Decode(SstvMode mode, byte[] img,
        double offsetHz = 0, double clockScale = 1, float noise = 0.01f)
    {
        var audio = SstvEncoder.Encode(mode, img, Rate, 0.5f, offsetHz, clockScale);
        audio = Pad(audio, 1.3, 1, noise);
        var dec = new SstvDecoder();
        SstvImage? done = null;
        SstvEndReason reason = SstvEndReason.FalseStart;
        dec.ImageEnded += (i, why) => { if (why != SstvEndReason.FalseStart) { done = i; reason = why; } };
        for (int i = 0; i < audio.Length; i += 4096)
            dec.Process(audio.AsSpan(i, Math.Min(4096, audio.Length - i)));
        Assert.NotNull(done);
        return new Result(done!, reason);
    }

    /// <summary>Lead/trail silence, then uniform noise over everything.</summary>
    private static float[] Pad(float[] sig, double leadS, double trailS, float noise)
    {
        int lead = (int)(leadS * Rate), trail = (int)(trailS * Rate);
        var o = new float[lead + sig.Length + trail];
        sig.CopyTo(o, lead);
        var rng = new Random(42);
        for (int i = 0; i < o.Length; i++) o[i] += noise * (float)(2 * rng.NextDouble() - 1);
        return o;
    }

    /// <summary>Colour bars over the top half, a horizontal grey ramp below —
    /// large flat areas (so the edge-blur of FM doesn't dominate the error)
    /// plus every colour channel exercised.</summary>
    private static byte[] TestCard(SstvMode m)
    {
        var rgb = new byte[m.Width * m.Height * 3];
        (byte, byte, byte)[] bars =
            [(255, 255, 255), (255, 255, 0), (0, 255, 255), (0, 255, 0),
             (255, 0, 255), (255, 0, 0), (0, 0, 255), (0, 0, 0)];
        for (int y = 0; y < m.Height; y++)
        for (int x = 0; x < m.Width; x++)
        {
            int i = (y * m.Width + x) * 3;
            if (y < m.Height / 2)
            {
                var (r, g, b) = bars[x * bars.Length / m.Width];
                rgb[i] = r; rgb[i + 1] = g; rgb[i + 2] = b;
            }
            else
            {
                byte v = (byte)(x * 255 / (m.Width - 1));
                rgb[i] = rgb[i + 1] = rgb[i + 2] = v;
            }
        }
        return rgb;
    }

    /// <summary>Mean |error| per channel, ignoring 3 % margins on each side
    /// (FM transition blur at the scan edges is not what we're testing).</summary>
    private static double MeanAbsError(SstvMode m, byte[] want, byte[] got)
    {
        int mx = m.Width * 3 / 100 + 1;
        double sum = 0; long n = 0;
        for (int y = 0; y < m.Height; y++)
        for (int x = mx; x < m.Width - mx; x++)
        for (int c = 0; c < 3; c++)
        {
            int i = (y * m.Width + x) * 3 + c;
            sum += Math.Abs(want[i] - got[i]); n++;
        }
        return sum / n;
    }
}
