// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class RadioControllerFreeDvTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-radio-controller-freedv-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    [Fact]
    public async Task SetModeAsync_FreeDvWithoutModem_FallsBackToUsb()
    {
        var lf = NullLoggerFactory.Instance;
        using var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        using var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        using var radio = new RadioService(lf, dspStore, paStore);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), lf);
        var tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        var controller = new RadioController(tx, radio, new NullLogger<RadioController>());

        await controller.SetModeAsync("FreeDv");

        Assert.Equal(RxMode.USB, radio.Snapshot().Mode);
    }
}
