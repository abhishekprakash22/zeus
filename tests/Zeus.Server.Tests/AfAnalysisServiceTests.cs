// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class AfAnalysisServiceTests
{
    private long _now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private AfAnalysisService Make() => new(null, () => _now);

    private static float[] Sine(double hz, double amp, int n, int rate = 48_000) =>
        Enumerable.Range(0, n).Select(i => (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();

    [Fact]
    public void Without_a_lease_nothing_is_computed()
    {
        var svc = Make();
        svc.Feed(0, Sine(1000, 0.5, 4800));
        Assert.Null(svc.LastFrame);
    }

    [Fact]
    public void A_leased_receiver_gets_a_frame_with_its_spectrum_scope_and_levels()
    {
        var svc = Make();
        svc.Subscribe(new[] { 0 });
        svc.Feed(0, Sine(1000, 0.5, 4800));
        var f = svc.LastFrame!;
        Assert.Equal(AfAnalysisService.FrameBytes, f.Length);
        Assert.Equal((byte)MsgType.AfAnalysis, f[0]);
        Assert.Equal(0, f[1]);                                   // source = RX1
        Assert.Equal(0, f[2]);                                   // not muted
        Assert.Equal(8000, f[3] | (f[4] << 8));
        Assert.Equal(AfAnalysisService.BinCount, f[5]);
        Assert.Equal(AfAnalysisService.ScopeCount, f[6] | (f[7] << 8));

        // The 1 kHz tone peaks at bin 1000 / 31.25 = 32 (±1).
        var bins = f.AsSpan(AfAnalysisService.HeaderBytes, AfAnalysisService.BinCount).ToArray();
        int peak = Array.IndexOf(bins, bins.Max());
        Assert.InRange(peak, 31, 33);
        // ~ −6 dBFS amplitude reads near the top of the 0..255 scale.
        Assert.InRange(bins[peak], 200, 255);

        int lv = AfAnalysisService.HeaderBytes + AfAnalysisService.BinCount + AfAnalysisService.ScopeCount;
        Assert.InRange((sbyte)f[lv], -11, -7);       // RMS of a 0.5 sine ≈ −9 dBFS
        Assert.InRange((sbyte)f[lv + 1], -8, -4);    // peak ≈ −6 dBFS
    }

    [Fact]
    public void A_muted_receiver_reads_as_silence_and_says_so()
    {
        var svc = Make();
        svc.Subscribe(new[] { 1 });
        svc.Feed(1, Sine(700, 0.5, 4800), muted: true);
        var f = svc.LastFrame!;
        Assert.Equal(1, f[1]);
        Assert.Equal(1, f[2] & 1);
        var scope = f.AsSpan(AfAnalysisService.HeaderBytes + AfAnalysisService.BinCount, AfAnalysisService.ScopeCount);
        foreach (var b in scope) Assert.Equal(0, b);
    }

    [Fact]
    public void Frames_are_rate_limited_and_leases_lapse()
    {
        var svc = Make();
        svc.Subscribe(new[] { 0 });
        svc.Feed(0, Sine(500, 0.3, 4800));
        var first = svc.LastFrame;
        svc.Feed(0, Sine(500, 0.3, 960));               // same instant: no new frame
        Assert.Same(first, svc.LastFrame);
        _now += TimeSpan.FromMilliseconds(100).Ticks;
        svc.Feed(0, Sine(500, 0.3, 960));
        Assert.NotSame(first, svc.LastFrame);

        _now += TimeSpan.FromSeconds(7).Ticks;          // lease (6 s) lapsed
        var last = svc.LastFrame;
        svc.Feed(0, Sine(500, 0.3, 4800));
        Assert.Same(last, svc.LastFrame);
        Assert.False(svc.IsLeased(0));
    }

    [Fact]
    public void The_TX_mic_has_its_own_source()
    {
        var svc = Make();
        svc.Subscribe(new[] { 255 });
        svc.FeedTx(Sine(300, 0.4, 4800));
        Assert.Equal(AfAnalysisService.TxSource, svc.LastFrame![1]);
        svc.Feed(0, Sine(300, 0.4, 4800));               // RX1 not leased
        Assert.Equal(AfAnalysisService.TxSource, svc.LastFrame![1]);
    }

    [Theory]
    [InlineData(-120.0, 0)]
    [InlineData(-60.0, 128)]
    [InlineData(0.0, 255)]
    [InlineData(10.0, 255)]
    [InlineData(-200.0, 0)]
    public void Decibels_map_onto_a_byte(double db, int expected)
    {
        Assert.Equal(expected, AfAnalysisService.DbToByte(db));
    }

    [Theory]
    [InlineData("aspec", "aspec")]
    [InlineData("cw", "cw")]
    [InlineData("bogus", "pass")]
    [InlineData(null, "pass")]
    public void Filter_views_normalise(string? view, string expected)
    {
        Assert.Equal(expected, UiPrefsEndpoints.NormalizeFilterView(view));
    }
}
