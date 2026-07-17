// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class TxServiceTuneInterlockTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-tx-tune-interlock-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    private (RadioService Radio, TxService Tx, RecordingPipeline Pipeline) BuildConnectedTx()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(loggerFactory, dspStore, paStore);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new RecordingPipeline(radio, hub, loggerFactory);
        var tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        return (radio, tx, pipeline);
    }

    [Fact]
    public void TrySetMox_On_ClearsTuneGeneratorBeforeKeying()
    {
        var (_, tx, pipeline) = BuildConnectedTx();

        bool ok = tx.TrySetMox(true, out var err);

        Assert.True(ok, err);
        Assert.False(tx.IsTunOn);
        Assert.True(tx.IsMoxOn);
        Assert.Collection(
            pipeline.Calls,
            call => Assert.Equal("tune:False", call),
            call => Assert.Equal("mox:True", call));
    }

    [Fact]
    public void TrySetMox_On_PreemptsActiveTunBeforeKeying()
    {
        var (_, tx, pipeline) = BuildConnectedTx();
        Assert.True(tx.TrySetTun(true, out var tunErr), tunErr);
        Assert.True(tx.IsTunOn);
        pipeline.Calls.Clear();

        bool ok = tx.TrySetMox(true, out var err);

        Assert.True(ok, err);
        Assert.False(tx.IsTunOn);
        Assert.True(tx.IsMoxOn);
        Assert.Collection(
            pipeline.Calls,
            call => Assert.Equal("tune:False", call),
            call => Assert.Equal("mox:True", call));
    }

    [Fact]
    public void P1DisconnectWhileTunClearsServerTunStateWithoutHardwareUnkey()
    {
        AssertDisconnectWhileTunClearsServerState(RaiseP1DisconnectedForTest);
    }

    [Fact]
    public void P2DisconnectWhileTunClearsServerTunStateWithoutHardwareUnkey()
    {
        AssertDisconnectWhileTunClearsServerState(radio => radio.MarkProtocol2Disconnected());
    }

    private void AssertDisconnectWhileTunClearsServerState(Action<RadioService> disconnect)
    {
        var (radio, tx, pipeline) = BuildConnectedTx();
        var txActiveEdges = new List<bool>();
        var tunEvents = new List<bool>();
        tx.TxActiveChanged += txActiveEdges.Add;
        radio.TunActiveChanged += tunEvents.Add;

        Assert.True(tx.TrySetTun(true, out var tunErr), tunErr);
        Assert.True(tx.IsTunOn);
        txActiveEdges.Clear();
        tunEvents.Clear();
        pipeline.Calls.Clear();

        disconnect(radio);

        Assert.False(tx.IsTunOn);
        Assert.False(tx.IsMoxOn);
        Assert.Equal(new[] { false }, txActiveEdges);
        Assert.Equal(new[] { false }, tunEvents);
        Assert.Empty(pipeline.Calls);
    }

    private static void RaiseP1DisconnectedForTest(RadioService radio)
    {
        var handlers = (Action?)typeof(RadioService)
            .GetField("Disconnected", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(radio);
        handlers?.Invoke();
    }

    private sealed class RecordingPipeline(
        RadioService radio,
        StreamingHub hub,
        ILoggerFactory logs) : DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), logs)
    {
        public List<string> Calls { get; } = new();

        public override void SetMox(bool on) => Calls.Add($"mox:{on}");

        public override void SetTxTune(bool on) => Calls.Add($"tune:{on}");
    }
}
