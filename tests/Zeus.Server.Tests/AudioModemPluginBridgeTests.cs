// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class AudioModemPluginBridgeTests
{
    [Fact]
    public async Task DeactivationWaitsForFreeDvTailDrainBeforeFlush()
    {
        var modem = new RecordingModem();
        var draining = 1;
        var release = Task.Run(async () =>
        {
            await Task.Delay(80);
            Volatile.Write(ref draining, 0);
        });

        var started = Environment.TickCount64;
        var ok = AudioModemPluginBridge.DisengageModemAfterTailDrain(
            modem,
            () => Volatile.Read(ref draining) != 0,
            NullLogger.Instance,
            "com.example.test",
            timeoutMs: 1000,
            pollMs: 10);
        var elapsedMs = Environment.TickCount64 - started;

        await release;
        Assert.True(ok);
        Assert.True(elapsedMs >= 60);
        Assert.Equal(1, modem.SyncCount);
        Assert.Equal(1, modem.FlushRxCount);
        Assert.Equal(1, modem.FlushTxCount);
    }

    private sealed class RecordingModem : IAudioModemPlugin
    {
        public int SyncCount;
        public int FlushRxCount;
        public int FlushTxCount;
        public bool Active => true;
        public bool NativeAvailable => true;
        public void SyncMode(byte rxModeByte) => SyncCount++;
        public void ProcessRx(Span<float> block48k) { }
        public void ProcessTx(Span<float> block48k) { }
        public void FlushRx() => FlushRxCount++;
        public void FlushTx() => FlushTxCount++;
        public int FinishTx() => 0;
        public int DrainTx(Span<float> block48k) => 0;
    }
}
