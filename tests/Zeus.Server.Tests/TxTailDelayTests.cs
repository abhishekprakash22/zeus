// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;

namespace Zeus.Server.Tests;

// Issue #1294: releasing PTT was cutting the end of the last word off. The
// browser mic path carries measurable buffering, so dropping the wire MOX bit
// the instant the operator releases PTT chops any audio still in flight through
// the browser→WS→WDSP→IQ pipeline. The TX tail delay holds the wire bit
// asserted for a configurable window (0..500 ms, default 0) so that audio
// finishes clocking out to the radio before it drops off the air.
//
// These tests pin the semantics: the tail delays the wire MOX drop only on a
// UI-sourced voice-mode release, is skipped for CW / non-UI sources, and is
// bounded by the operator-configured value.
public sealed class TxTailDelayTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-tx-tail-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    private (TxService Tx, RadioService Radio) BuildConnectedTx()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(loggerFactory, dspStore, paStore);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), loggerFactory);
        var tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        return (tx, radio);
    }

    // Small headroom absorbs Thread.Sleep quantisation on shared CI runners
    // while still catching a "delay did not fire at all" regression.
    private const int TailSlackMs = 30;

    [Fact]
    public void TrySetMox_OffFromUiInVoiceMode_HoldsWireMoxForTailDelay()
    {
        var (tx, radio) = BuildConnectedTx();
        radio.SetMode(RxMode.USB);
        radio.SetTxMoxTailDelayMs(120);
        Assert.True(tx.TrySetMox(true, out var onErr), onErr);

        var sw = Stopwatch.StartNew();
        Assert.True(tx.TrySetMox(false, out var offErr), offErr);
        sw.Stop();

        Assert.InRange(sw.ElapsedMilliseconds, 120 - 5, 120 + 500);
    }

    [Fact]
    public void TrySetMox_OffFromUiInCwMode_SkipsTailDelay()
    {
        var (tx, radio) = BuildConnectedTx();
        radio.SetMode(RxMode.CWU);
        radio.SetTxMoxTailDelayMs(300);
        Assert.True(tx.TrySetMox(true, out var onErr), onErr);

        var sw = Stopwatch.StartNew();
        Assert.True(tx.TrySetMox(false, out var offErr), offErr);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 300 - TailSlackMs,
            $"CW un-key should not sleep for the tail delay, but took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void TrySetMox_OffWithZeroTailDelay_DropsImmediately()
    {
        var (tx, radio) = BuildConnectedTx();
        radio.SetMode(RxMode.USB);
        radio.SetTxMoxTailDelayMs(0);
        Assert.True(tx.TrySetMox(true, out var onErr), onErr);

        var sw = Stopwatch.StartNew();
        Assert.True(tx.TrySetMox(false, out var offErr), offErr);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100,
            $"Zero tail delay should drop MOX promptly, but took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void TrySetMox_OffFromHardwareSource_SkipsTailDelay()
    {
        var (tx, radio) = BuildConnectedTx();
        radio.SetMode(RxMode.USB);
        radio.SetTxMoxTailDelayMs(300);
        Assert.True(tx.TrySetMox(true, MoxSource.Hardware, out var onErr), onErr);

        var sw = Stopwatch.StartNew();
        Assert.True(tx.TrySetMox(false, MoxSource.Hardware, out var offErr), offErr);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 300 - TailSlackMs,
            $"Hardware-sourced un-key should not sleep for the tail delay, but took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void SetTxMoxTailDelayMs_ClampsAboveMaxToMax()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(loggerFactory, dspStore, paStore);

        var applied = radio.SetTxMoxTailDelayMs(9999);
        Assert.Equal(RadioService.MaxTailDelayMs, applied.TxMoxTailDelayMs);
    }

    [Fact]
    public void SetTxMoxTailDelayMs_ClampsBelowZeroToZero()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(loggerFactory, dspStore, paStore);

        var applied = radio.SetTxMoxTailDelayMs(-50);
        Assert.Equal(0, applied.TxMoxTailDelayMs);
    }
}
