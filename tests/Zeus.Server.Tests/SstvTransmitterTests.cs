// SPDX-License-Identifier: GPL-2.0-or-later
//
// SstvTransmitter safety: it keys only on request, only under its own MOX
// source, refuses bad input / a busy transmitter / a non-SSB receiver, and
// lets go at once on HALT or when the operator takes MOX away (UI is the
// master override). Waveform correctness is SstvTests' job.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Protocol1;
using Zeus.Server;
using Zeus.Server.Hosting.Digital;
using Zeus.Server.Hosting.Digital.Sstv;

namespace Zeus.Server.Tests;

public sealed class SstvTransmitterTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-sstvtx-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    private (RadioService radio, TxService tx, SstvTransmitter sstv) Build()
    {
        var lf = NullLoggerFactory.Instance;
        var radio = new RadioService(lf,
            new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath),
            new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa"));
        radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);
        radio.SetMode(RxMode.USB);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), lf);
        var tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        var ingest = new TxAudioIngest(new TxIqRing(), pipeline, tx, hub, new NullLogger<TxAudioIngest>());
        var digital = new DigitalService(null!, NullLogger<DigitalService>.Instance);
        var sstv = new SstvTransmitter(ingest, tx, digital, NullLogger<SstvTransmitter>.Instance, radio);
        return (radio, tx, sstv);
    }

    private static SstvTxRequest Picture(SstvMode m, string? fsk = null) =>
        new(m.Name, Convert.ToBase64String(new byte[m.Width * m.Height * 3]), fsk);

    private static void WaitFor(Func<bool> cond, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond() && DateTime.UtcNow < until) Thread.Sleep(10);
        Assert.True(cond());
    }

    [Fact]
    public void Rejects_BadRequests_WithoutKeying()
    {
        var (radio, tx, sstv) = Build();
        Assert.Contains("unknown", sstv.Start(new SstvTxRequest("Martin 9", "", null)));
        Assert.Contains("320x256", sstv.Start(new SstvTxRequest("Martin 1", Convert.ToBase64String(new byte[10]), null)));
        Assert.Contains("base64", sstv.Start(new SstvTxRequest("Martin 1", "not base64!", null)));
        radio.SetMode(RxMode.AM);
        Assert.Contains("AM", sstv.Start(Picture(SstvModes.R36)));
        Assert.False(tx.IsMoxOn);
        Assert.False(sstv.Transmitting);
    }

    [Fact]
    public void Refuses_WhenSomeoneElseIsTransmitting()
    {
        var (_, tx, sstv) = Build();
        Assert.True(tx.TrySetMox(true, MoxSource.UI, out _));
        Assert.Contains("already keyed", sstv.Start(Picture(SstvModes.R36)));
        Assert.Equal(MoxSource.UI, tx.MoxOwner);
    }

    [Fact]
    public void Keys_AsSstv_AndHaltUnkeys()
    {
        var (_, tx, sstv) = Build();
        Assert.Null(sstv.Start(Picture(SstvModes.R36, "EA4ABC")));
        WaitFor(() => tx.MoxOwner == MoxSource.Sstv);
        Assert.True(sstv.Transmitting);
        Assert.Contains("already", sstv.Start(Picture(SstvModes.R36)));   // one at a time

        sstv.Halt();
        WaitFor(() => !sstv.Transmitting);
        Assert.False(tx.IsMoxOn);
        Assert.Equal("halted", sstv.Status().LastError);
    }

    [Fact]
    public void OperatorMoxOff_EndsThePicture()
    {
        var (_, tx, sstv) = Build();
        Assert.Null(sstv.Start(Picture(SstvModes.R36)));
        WaitFor(() => tx.MoxOwner == MoxSource.Sstv);

        Assert.True(tx.TrySetMox(false, MoxSource.UI, out _));    // UI master override
        WaitFor(() => !sstv.Transmitting);
        Assert.False(tx.IsMoxOn);
        Assert.Contains("MOX taken away", sstv.Status().LastError);
    }
}
