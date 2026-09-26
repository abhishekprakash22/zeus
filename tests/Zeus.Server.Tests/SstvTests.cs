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
    [InlineData("Robot 36", 300.0)]   // two 150 ms lines, modelled as one
    [InlineData("Robot 72", 300.0)]
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
    [InlineData("Robot 36", 0.12f)]
    public void RoundTrip_SurvivesNoiseOffsetAndSlant(string name, float noise)
    {
        var mode = SstvModes.ByName(name)!;
        var img = TestCard(mode);
        // +120 Hz mistune, sender clock +0.3 % (a visibly slanted picture if
        // uncorrected: ~130 px of skew over a Martin 1 frame), noise.
        var r = Decode(mode, img, offsetHz: 120, clockScale: 1.003, noise: noise);

        Assert.Equal(SstvEndReason.Complete, r.Reason);
        // Within 5 Hz: one luma step is 800/255 ≈ 3 Hz, and at this SNR the
        // leader's median sits a couple of Hz toward the demodulator's centre.
        Assert.InRange(r.Image.OffsetHz, 115, 125);
        Assert.Equal(0.003, r.Image.ClockError, 3);
        Assert.InRange(MeanAbsError(mode, img, r.Image.Rgb), 0, 18);
    }

    // Weak-signal floor (zeus-rhtp.6). Gaussian noise, SNR measured in a
    // 2.5 kHz bandwidth. Before the contrast sync search and the median VIS
    // read, 8 dB lost most pictures and 6 dB started almost none; now both
    // start AND complete. Fixed seeds keep CI deterministic.
    [Theory]
    [InlineData("Martin 1", 6.0)]
    [InlineData("Scottie 1", 6.0)]
    [InlineData("PD 120", 6.0)]
    [InlineData("Robot 36", 6.0)]
    public void WeakSignal_StartsAndCompletes(string name, double snrDb)
    {
        var mode = SstvModes.ByName(name)!;
        for (int seed = 0; seed < 2; seed++)
        {
            var audio = SstvEncoder.Encode(mode, TestCard(mode), Rate, 0.5f, 25, 1.0005);
            var buf = new float[Rate + audio.Length + Rate];
            audio.CopyTo(buf, Rate);
            AddGaussian(buf, snrDb, seed);
            var dec = new SstvDecoder();
            SstvImage? done = null;
            dec.ImageEnded += (i, why) => { if (why == SstvEndReason.Complete) done = i; };
            for (int i = 0; i < buf.Length; i += 600)
                dec.Process(buf.AsSpan(i, Math.Min(600, buf.Length - i)));
            Assert.NotNull(done);
            Assert.Same(mode, done!.Mode);
        }
    }

    // Tuned in mid-picture (or the VIS lost to a fade): no header, only the
    // sync train. The decoder must recognise the mode from pulse length and
    // line period and start there; the rows it paints are the sender's rows
    // from the cut onward. Robot 36 is cut on an ODD line to prove it finds
    // line parity from the separator tone.
    [Theory]
    [InlineData("Martin 1", 40)]
    [InlineData("Scottie 1", 40)]
    [InlineData("PD 120", 30)]
    [InlineData("Robot 36", 41)]
    [InlineData("Robot 72", 20)]
    [InlineData("Martin 2", 100)]
    public void TunedInMidPicture_StartsFromTheSyncTrain(string name, int fromRow)
    {
        var mode = SstvModes.ByName(name)!;
        var card = TestCard(mode);
        var audio = SstvEncoder.Encode(mode, card, Rate, 0.5f, 20);
        double lineMs = mode.LineMs;
        double cutMs = SstvEncoder.VisMs + mode.LeadInMs + (fromRow / mode.RowsPerLine) * lineMs
                       + (mode == SstvModes.R36 && fromRow % 2 == 1 ? 150 : 0) + 37;   // mid-line
        var tail = audio.AsSpan((int)(cutMs * Rate / 1000)).ToArray();
        // Trailing silence long enough for the decoder to call the picture
        // lost once the sender stops (25 whole lines without sync, plus slack).
        var buf = Pad(tail, 0.5, 30 * lineMs / 1000.0, 0.02f);

        var dec = new SstvDecoder();
        SstvImage? got = null;
        dec.ImageEnded += (i, why) => { if (why != SstvEndReason.FalseStart) got = i; };
        for (int i = 0; i < buf.Length; i += 600)
            dec.Process(buf.AsSpan(i, Math.Min(600, buf.Length - i)));

        Assert.NotNull(got);
        Assert.True(got!.ViaSync);
        Assert.Same(mode, got.Mode);

        // Decoded row r is source row k + r for some small k (where the train
        // was anchored); find k and check the overlap looks right.
        int rows = Math.Min(got.RowsDone, mode.Height - fromRow - 2 * mode.RowsPerLine) - 4;
        Assert.True(rows > mode.Height / 4, $"only {got.RowsDone} rows");
        double best = double.MaxValue;
        for (int k = fromRow; k <= fromRow + 4 * mode.RowsPerLine; k++)
        {
            if (k + rows > mode.Height) break;
            double err = 0; long n = 0;
            int mx = mode.Width * 3 / 100 + 1;
            for (int r = 0; r < rows; r++)
            for (int x = mx; x < mode.Width - mx; x++)
            for (int c = 0; c < 3; c++)
            {
                err += Math.Abs(card[((k + r) * mode.Width + x) * 3 + c] - got.Rgb[(r * mode.Width + x) * 3 + c]);
                n++;
            }
            best = Math.Min(best, err / n);
        }
        Assert.InRange(best, 0, 12);
    }

    [Fact]
    public void Noise_FromTheVeryFirstSample_NeverReadsBeforeTheTrack()
    {
        // Regression: the sync-train pulse hunt's moving average started at
        // zero, "saw" a pulse at sample 0 and read the track before its start.
        var buf = new float[5 * Rate];
        AddGaussian(buf, 0, 4, std: 0.3);
        var dec = new SstvDecoder();
        for (int i = 0; i < buf.Length; i += 600)
            dec.Process(buf.AsSpan(i, Math.Min(600, buf.Length - i)));
        dec.Process(buf);                                   // and one big block
    }

    [Fact]
    public void GaussianNoise_ThreeMinutes_NoFalseStart()
    {
        var dec = new SstvDecoder();
        int started = 0;
        dec.ImageStarted += _ => started++;
        var buf = new float[180 * Rate];
        AddGaussian(buf, 0, 9, std: 0.3);
        for (int i = 0; i < buf.Length; i += 600)
            dec.Process(buf.AsSpan(i, Math.Min(600, buf.Length - i)));
        Assert.Equal(0, started);
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

    [Fact]
    public void Robot36_SeparatorsTellEvenFromOddLines()
    {
        // MMSSTV identifies Robot 36's chroma by the separator tone: 1500 Hz
        // before R-Y (even lines), 2300 Hz before B-Y (odd lines), each
        // followed by a 1900 Hz porch. A transmitter that gets this wrong
        // sends pictures with swapped colours to half the world.
        var mode = SstvModes.R36;
        const int rate = 48_000;
        var audio = SstvEncoder.Encode(mode, TestCard(mode), rate);

        // Frequency of a segment from its zero crossings (interpolated), away
        // from the edges — exact even for the 1.5 ms porches, which any
        // filter-based demodulator would smear into their neighbours.
        double Tone(double fromMs, double toMs)
        {
            int a = (int)((fromMs + 0.15) * rate / 1000), b = (int)((toMs - 0.15) * rate / 1000);
            var xs = new List<double>();
            for (int i = a; i < b; i++)
                if (audio[i] <= 0 && audio[i + 1] > 0)
                    xs.Add(i + audio[i] / (audio[i] - audio[i + 1]));
            return rate * (xs.Count - 1) / (xs[^1] - xs[0]);
        }

        double l = SstvEncoder.VisMs + 10 * mode.LineMs;        // a double line well inside
        Assert.InRange(Tone(l + 100, l + 104.5), 1495, 1505);   // even separator
        Assert.InRange(Tone(l + 104.5, l + 106), 1890, 1910);   // porch
        Assert.InRange(Tone(l + 150, l + 159), 1195, 1205);     // odd line's sync
        Assert.InRange(Tone(l + 250, l + 254.5), 2295, 2305);   // odd separator
        Assert.InRange(Tone(l + 254.5, l + 256), 1890, 1910);   // porch
    }

    // ---- re-render ----------------------------------------------------------

    [Fact]
    public void Rerender_WithoutAdjustments_ReproducesThePicture()
    {
        var mode = SstvModes.S1;
        var r = Decode(mode, TestCard(mode), clockScale: 1.002);
        Assert.NotNull(r.Image.Recording);

        var again = SstvDecoder.Rerender(r.Image);
        Assert.Equal(r.Image.Id, again.Id);
        Assert.Equal(r.Image.RowsDone, again.RowsDone);
        Assert.Equal(r.Image.Rgb, again.Rgb);
    }

    [Fact]
    public void Rerender_ShiftMovesThePictureRight_ByThatManyPixels()
    {
        var mode = SstvModes.M1;
        var card = TestCard(mode);
        var r = Decode(mode, card);
        var shifted = SstvDecoder.Rerender(r.Image, shiftPx: 20);
        Assert.Equal(20, shifted.ShiftPx);

        // The grey ramp (bottom half) is monotonic in x: shifted[x] ≈ orig[x-20].
        int y = mode.Height * 3 / 4, w = mode.Width;
        double err = 0; int n = 0;
        for (int x = 40; x < w - 20; x++, n++)
            err += Math.Abs(shifted.Rgb[(y * w + x) * 3 + 1] - r.Image.Rgb[(y * w + x - 20) * 3 + 1]);
        Assert.InRange(err / n, 0, 4);
    }

    [Fact]
    public void Rerender_AdjustmentsAreAbsolute_NotCumulative()
    {
        var mode = SstvModes.PD90;
        var r = Decode(mode, TestCard(mode));
        var slanted = SstvDecoder.Rerender(r.Image, slantPpm: 4000);
        Assert.True(MeanAbsError(mode, TestCard(mode), slanted.Rgb) >
                    MeanAbsError(mode, TestCard(mode), r.Image.Rgb) + 10);
        // Re-render the ADJUSTED image back to zero: identical to the original.
        var back = SstvDecoder.Rerender(slanted, slantPpm: 0);
        Assert.Equal(r.Image.Rgb, back.Rgb);
    }

    [Fact]
    public void Rerender_DecodeAs_AnotherMode_AndBack()
    {
        var mode = SstvModes.M1;
        var r = Decode(mode, TestCard(mode));
        var asM2 = SstvDecoder.Rerender(r.Image, SstvModes.M2);
        Assert.Same(SstvModes.M2, asM2.Mode);
        var back = SstvDecoder.Rerender(asM2, SstvModes.M1);
        Assert.Equal(r.Image.Rgb, back.Rgb);
    }

    // ---- FSK ID -------------------------------------------------------------

    [Theory]
    [InlineData("EA4ABC", 0)]
    [InlineData("EA4ABC/P", 120)]
    [InlineData("K1A", -80)]
    public void FskId_FollowingThePicture_IsDecoded(string call, double offsetHz)
    {
        var mode = SstvModes.M2;
        var audio = SstvEncoder.Encode(mode, TestCard(mode), Rate, 0.5f, offsetHz, fskId: call);
        audio = Pad(audio, 1, 5, 0.05f);
        var dec = new SstvDecoder();
        string? got = null;
        SstvImage? gotFor = null, ended = null;
        dec.ImageEnded += (i, _) => ended = i;
        dec.CallsignDecoded += (i, c) => { gotFor = i; got = c; };
        for (int i = 0; i < audio.Length; i += 2048)
            dec.Process(audio.AsSpan(i, Math.Min(2048, audio.Length - i)));

        Assert.Equal(call, got);
        Assert.Same(ended, gotFor);
    }

    [Fact]
    public void FskId_IsFound_WhateverTheBlockSize()
    {
        // The service normally feeds ~50 ms blocks, but a worker catching up
        // after a stall hands over seconds at once. The ID search is anchored
        // to where the picture ended, not to the block — so one block holding
        // the whole transmission must still yield the callsign.
        var mode = SstvModes.M2;
        var audio = Pad(SstvEncoder.Encode(mode, TestCard(mode), Rate, 0.5f, fskId: "EA5IUE"), 1, 5, 0.05f);
        var dec = new SstvDecoder();
        string? got = null;
        dec.CallsignDecoded += (_, c) => got = c;
        dec.Process(audio);
        Assert.Equal("EA5IUE", got);
    }

    [Fact]
    public void FskId_Absent_ReportsNothing()
    {
        var mode = SstvModes.M2;
        var audio = Pad(SstvEncoder.Encode(mode, TestCard(mode), Rate), 1, 5, 0.05f);
        var dec = new SstvDecoder();
        int calls = 0;
        dec.CallsignDecoded += (_, _) => calls++;
        dec.Process(audio);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void FskId_AfterAPictureStartedMidTransmission_IsDecoded()
    {
        // Seen on the air: pictures started from the sync train can never
        // complete — they began mid-transmission — so they end SignalLost, and
        // their FSK ID used to be ignored.
        var mode = SstvModes.M1;
        var audio = SstvEncoder.Encode(mode, TestCard(mode), Rate, 0.5f, 15, fskId: "IU2DYL");
        int cut = (int)((SstvEncoder.VisMs + 60 * mode.LineMs + 37) * Rate / 1000);
        var buf = Pad(audio.AsSpan(cut).ToArray(), 0.5, 30 * mode.LineMs / 1000.0, 0.02f);
        var dec = new SstvDecoder();
        string? call = null;
        SstvEndReason? why = null;
        dec.ImageEnded += (_, r) => why ??= r;
        dec.CallsignDecoded += (_, c) => call = c;
        for (int i = 0; i < buf.Length; i += 600)
            dec.Process(buf.AsSpan(i, Math.Min(600, buf.Length - i)));
        Assert.Equal(SstvEndReason.SignalLost, why);
        Assert.Equal("IU2DYL", call);
    }

    [Fact]
    public void FskId_BurstThatDoesNotRead_IsReportedWithWhy()
    {
        var mode = SstvModes.M2;
        var audio = SstvEncoder.Encode(mode, TestCard(mode), Rate, 0.5f, fskId: "EA5IUE");
        // Stamp a steady 1900 Hz over two characters in the middle of the ID:
        // the guard and start bit still look right, the frame no longer adds up.
        double idStartMs = SstvEncoder.VisMs + mode.DurationMs + 300 + 100 + SstvDecoder.FskBitMs;
        int a0 = (int)((idStartMs + 3 * 6 * SstvDecoder.FskBitMs) * Rate / 1000);
        int a1 = a0 + (int)(2 * 6 * SstvDecoder.FskBitMs * Rate / 1000);
        for (int i = a0; i < a1; i++) audio[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 1900 * i / Rate);
        var buf = Pad(audio, 1, 5, 0.02f);
        var dec = new SstvDecoder();
        string? call = null, why = null;
        dec.CallsignDecoded += (_, c) => call = c;
        dec.FskIdUnreadable += (_, w) => why = w;
        dec.Process(buf);
        Assert.Null(call);
        Assert.NotNull(why);
    }

    [Theory]
    [InlineData("ea4abc", "EA4ABC")]
    [InlineData("  ea4abc/qrp-long ", "EA4ABC/QR")]   // 9-char cap (QSSTV)
    [InlineData("hi!\"", "HI")]                     // ! and " are control codes
    [InlineData("   ", null)]
    public void FskId_Normalise(string input, string? want) =>
        Assert.Equal(want, SstvEncoder.NormaliseFskId(input));

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

    /// <summary>Gaussian noise for a 0.5-amplitude signal at <paramref name="snrDb"/>
    /// in 2.5 kHz (or a fixed <paramref name="std"/>).</summary>
    private static void AddGaussian(float[] buf, double snrDb, int seed, double? std = null)
    {
        double sd = std ?? Math.Sqrt(0.125 / Math.Pow(10, snrDb / 10) * (Rate / 2.0 / 2500.0));
        var rng = new Random(seed);
        for (int i = 0; i < buf.Length; i++)
        {
            double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
            buf[i] += (float)(sd * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }
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
