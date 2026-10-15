// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

using Zeus.Contracts;
using Zeus.Dsp.Wdsp;

namespace Zeus.Dsp.Tests;

/// <summary>
/// CESSB (WDSP osctrl) only ever runs as the second half of the speech
/// processor (COMP -> bp1 -> osctrl -> bp2), is switched in a transition-safe
/// order, follows digital-mode bypass with the compressor, and its design
/// bandwidth follows the TX filter. Driven through the engine's TX-control
/// seam with a recording fake — no native calls.
/// </summary>
public class CessbTopologyTests
{
    private sealed class RecordingTxControl : IWdspTxControlNative
    {
        public readonly List<string> Calls = new();
        public bool BandwidthExportMissing { get; set; }

        public void SetTXAMode(int channel, int mode) => Calls.Add($"mode={mode}");
        public void SetTXACompressorRun(int channel, int run) => Calls.Add($"comp={run}");
        public void SetTXACFCOMPRun(int channel, int run) { }
        public void SetTXACFCOMPprofile(int channel, int nfreqs, double[] f, double[] g, double[] e) { }
        public void SetTXACFCOMPPrecomp(int channel, double precomp) { }
        public void SetTXACFCOMPPrePeq(int channel, double prepeq) { }
        public void SetTXACFCOMPPeqRun(int channel, int run) { }
        public void SetTXAPHROTRun(int channel, int run) { }
        public void SetTXAPHROTCorner(int channel, double frequency) { }
        public void SetTXAPHROTNstages(int channel, int nstages) { }
        public void SetTXAPHROTAutoMode(int channel, int autoMode) { }
        public void SetTXAPHROTReverse(int channel, int reverse) { }
        public void SetTXALevelerSt(int channel, int state) { }
        public void SetTXAosctrlRun(int channel, int run) => Calls.Add($"cessb={run}");

        public void SetTXAosctrlBandwidth(int channel, double bandwidth)
        {
            if (BandwidthExportMissing) throw new EntryPointNotFoundException("SetTXAosctrlBandwidth");
            Calls.Add($"bw={bandwidth}");
        }

        public string[] Only(string prefix) => Calls.Where(c => c.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

        /// <summary>The comp/cessb writes in order, e.g. ["cessb=0", "comp=0"].</summary>
        public string[] Processor() =>
            Calls.Where(c => c.StartsWith("comp=", StringComparison.Ordinal) || c.StartsWith("cessb=", StringComparison.Ordinal)).ToArray();
    }

    private static (WdspDspEngine Engine, RecordingTxControl Fake) Make(bool compressorEnabled)
    {
        var fake = new RecordingTxControl();
        var engine = new WdspDspEngine(null, fake, registerNativeResolver: false);
        engine.OpenTxChannelForTests(7, RxMode.USB, compressorEnabled);
        return (engine, fake);
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(0, true, 0)]   // never standalone: no COMP, no CESSB
    [InlineData(1, false, 0)]
    [InlineData(1, true, 1)]
    public void CESSB_runs_only_with_the_compressor(int compressorRun, bool cessbEnabled, int expected)
    {
        Assert.Equal(expected, WdspDspEngine.EffectiveCessbRun(compressorRun, cessbEnabled));
    }

    [Fact]
    public void Digital_bypass_switches_CESSB_off_before_COMP_and_back_on_after_it()
    {
        var (engine, fake) = Make(compressorEnabled: true);

        engine.SetTxDigitalBypass(true);
        // Turning off: osctrl first, so no block ever sees a standalone CESSB.
        Assert.Equal(new[] { "cessb=0", "comp=0" }, fake.Processor());

        fake.Calls.Clear();
        engine.SetTxDigitalBypass(false);
        // Turning on: COMP first, then osctrl.
        Assert.Equal(new[] { "comp=1", "cessb=1" }, fake.Processor());
    }

    [Fact]
    public void With_the_compressor_off_CESSB_stays_off_in_every_mode()
    {
        var (engine, fake) = Make(compressorEnabled: false);
        engine.SetTxMode(RxMode.LSB);
        engine.SetTxMode(RxMode.DIGU);
        engine.SetTxMode(RxMode.USB);
        Assert.DoesNotContain("cessb=1", fake.Calls);
    }

    [Fact]
    public void Digital_modes_bypass_the_processor_and_voice_restores_it()
    {
        var (engine, fake) = Make(compressorEnabled: true);
        engine.SetTxMode(RxMode.DIGU);
        Assert.Equal(new[] { "cessb=0", "comp=0" }, fake.Processor());
        fake.Calls.Clear();
        engine.SetTxMode(RxMode.USB);
        Assert.Equal(new[] { "comp=1", "cessb=1" }, fake.Processor());
    }

    [Theory]
    [InlineData(150, 2850, 2850)]
    [InlineData(-2850, -150, 2850)]   // LSB filters are negative
    [InlineData(100, 1800, 2400)]     // narrow: floor
    [InlineData(50, 3600, 3600)]      // ESSB
    [InlineData(0, 6000, 4000)]       // ceiling
    public void CESSB_bandwidth_follows_the_TX_filter_edge(int low, int high, int expected)
    {
        Assert.Equal(expected, WdspDspEngine.CessbBandwidthForFilter(low, high));
    }

    [Fact]
    public void Bandwidth_is_sent_only_when_it_changes()
    {
        var (engine, fake) = Make(compressorEnabled: true);
        engine.ApplyCessbBandwidthLocked(7, 3000);   // native default — nothing to send
        engine.ApplyCessbBandwidthLocked(7, 3600);
        engine.ApplyCessbBandwidthLocked(7, 3600);
        engine.ApplyCessbBandwidthLocked(7, 2850);
        Assert.Equal(new[] { "bw=3600", "bw=2850" }, fake.Only("bw="));
    }

    [Fact]
    public void An_older_libwdsp_without_the_export_keeps_3000_Hz_and_is_not_probed_again()
    {
        var (engine, fake) = Make(compressorEnabled: true);
        fake.BandwidthExportMissing = true;
        engine.ApplyCessbBandwidthLocked(7, 3600);   // throws inside → latched missing
        fake.BandwidthExportMissing = false;
        engine.ApplyCessbBandwidthLocked(7, 2850);   // must not probe again
        Assert.Empty(fake.Only("bw="));
    }
}
