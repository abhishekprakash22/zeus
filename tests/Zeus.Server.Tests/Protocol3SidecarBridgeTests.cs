using System.Text.Json;
using System.Text.Json.Nodes;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class Protocol3SidecarBridgeTests
{
    [Fact]
    public void ValidateDiagnosticsSnapshot_AcceptsN9DspTenStreamSidecar()
    {
        var snapshot = Protocol3SidecarBridge.ValidateDiagnosticsSnapshot(
            Diagnostics(streams: 10, engine: "n9dsp", status: "ok"),
            new Uri("http://127.0.0.1:2074/api/diagnostics/v2"),
            expectedRxStreams: 10);

        Assert.Equal("ok", snapshot.Status);
        Assert.Equal("n9dsp", snapshot.DspEngine);
        Assert.Equal(10, snapshot.RxActiveStreams);
        Assert.Equal(10, snapshot.RxExpectedStreams);
        Assert.Equal(10, snapshot.DspActiveChannels);
    }

    [Fact]
    public void ValidateDiagnosticsSnapshot_AcceptsDegradedN9DspTenStreamSidecar()
    {
        var snapshot = Protocol3SidecarBridge.ValidateDiagnosticsSnapshot(
            Diagnostics(streams: 10, engine: "n9dsp", status: "degraded"),
            new Uri("http://127.0.0.1:2074/api/diagnostics/v2"),
            expectedRxStreams: 10);

        Assert.Equal("degraded", snapshot.Status);
        Assert.Equal("n9dsp", snapshot.DspEngine);
        Assert.Equal(10, snapshot.RxActiveStreams);
    }

    [Fact]
    public void ValidateDiagnosticsSnapshot_RejectsNonN9DspEngine()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Protocol3SidecarBridge.ValidateDiagnosticsSnapshot(
                Diagnostics(streams: 10, engine: "wdsp", status: "ok"),
                new Uri("http://127.0.0.1:2074/api/diagnostics/v2"),
                expectedRxStreams: 10));

        Assert.Contains("expected 'n9dsp'", ex.Message);
    }

    [Fact]
    public void ValidateDiagnosticsSnapshot_RejectsSixStreamSidecar()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Protocol3SidecarBridge.ValidateDiagnosticsSnapshot(
                Diagnostics(streams: 6, engine: "n9dsp", status: "ok"),
                new Uri("http://127.0.0.1:2074/api/diagnostics/v2"),
                expectedRxStreams: 10));

        Assert.Contains("requires 10", ex.Message);
    }

    [Fact]
    public void ValidateDiagnosticsSnapshot_RejectsNonLoopbackDiagnosticsUrl()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Protocol3SidecarBridge.ValidateDiagnosticsSnapshot(
                Diagnostics(streams: 10, engine: "n9dsp", status: "ok"),
                new Uri("http://192.168.1.25:2074/api/diagnostics/v2"),
                expectedRxStreams: 10));

        Assert.Contains("loopback", ex.Message);
    }

    [Fact]
    public void BuildReceiverSettingsJson_ProjectsLiveRxControlsForN9Dsp()
    {
        var state = new StateDto(
            ConnectionStatus.Connected,
            "192.168.1.25:1030",
            VfoHz: 7_209_000,
            Mode: RxMode.LSB,
            FilterLowHz: -3_900,
            FilterHighHz: -100,
            SampleRate: 192_000,
            AgcTopDb: 66,
            Agc: new AgcConfig(AgcMode.Med),
            Squelch: new SquelchConfig(Enabled: true, Level: 30),
            Nr: new NrConfig(
                NrMode: NrMode.Sbnr,
                AnfEnabled: true,
                SnbEnabled: true,
                NbpNotchesEnabled: true,
                NbMode: NbMode.Nb1,
                NbThreshold: 18,
                Nr4ReductionAmount: 14),
            RxAfGainDb: 7,
            RadioLoHz: 7_205_000,
            CtunEnabled: true,
            MaxReceivers: 10);

        using var doc = JsonDocument.Parse(Protocol3SidecarBridge.BuildReceiverSettingsJson(
            state,
            rxStreams: 1,
            sampleRateHz: 192_000));
        var rx = doc.RootElement[0];

        Assert.True(rx.GetProperty("enabled").GetBoolean());
        Assert.Equal((int)RxMode.LSB, rx.GetProperty("mode").GetInt32());
        Assert.Equal(-3_900, rx.GetProperty("passbandLowHz").GetInt32());
        Assert.Equal(-100, rx.GetProperty("passbandHighHz").GetInt32());
        Assert.Equal(7_205_000, rx.GetProperty("centerFrequencyHz").GetInt64());
        Assert.Equal(4_000, rx.GetProperty("tuningOffsetHz").GetInt32());
        Assert.Equal(7.0, rx.GetProperty("afGainDb").GetDouble(), precision: 6);
        Assert.Equal(1, rx.GetProperty("enableRxAgc").GetInt32());
        Assert.Equal(2, rx.GetProperty("rxAgcProfile").GetInt32());
        Assert.Equal(66.0, rx.GetProperty("agcMaxGainDb").GetDouble(), precision: 6);
        Assert.Equal(1, rx.GetProperty("enableRxNoiseBlanker").GetInt32());
        Assert.Equal(1, rx.GetProperty("enableRxSpectralBlanker").GetInt32());
        Assert.Equal(1, rx.GetProperty("enableRxNotch").GetInt32());
        Assert.Equal(1, rx.GetProperty("enableRxSquelch").GetInt32());
        Assert.Equal(-110.0, rx.GetProperty("rxSquelchThresholdDb").GetDouble(), precision: 6);
        Assert.Equal(0, rx.GetProperty("enableRxAnr").GetInt32());
        Assert.Equal(1, rx.GetProperty("enableRxEmnr").GetInt32());
        Assert.Equal(2, rx.GetProperty("enableRxMultiNotch").GetInt32());
    }

    [Fact]
    public void BuildReceiverSettingsJson_CanInvertCtunTuningOffsetForP3Dsp()
    {
        var state = new StateDto(
            ConnectionStatus.Connected,
            "192.168.1.25:1030",
            VfoHz: 14_243_000,
            Mode: RxMode.USB,
            FilterLowHz: 19,
            FilterHighHz: 2_864,
            SampleRate: 192_000,
            RadioLoHz: 14_207_741,
            CtunEnabled: true,
            MaxReceivers: 1);

        using var doc = JsonDocument.Parse(Protocol3SidecarBridge.BuildReceiverSettingsJson(
            state,
            rxStreams: 1,
            sampleRateHz: 192_000,
            invertRxTuningOffset: true));
        var rx = doc.RootElement[0];

        Assert.Equal(14_207_741, rx.GetProperty("centerFrequencyHz").GetInt64());
        Assert.Equal(-35_259, rx.GetProperty("tuningOffsetHz").GetInt32());
    }

    [Fact]
    public void BuildReceiverSettingsJson_UsesRadioLoOffsetWhenCtunFlagIsFalse()
    {
        var state = new StateDto(
            ConnectionStatus.Connected,
            "192.168.1.25:1030",
            VfoHz: 7_209_000,
            Mode: RxMode.USB,
            FilterLowHz: 150,
            FilterHighHz: 2_850,
            SampleRate: 192_000,
            RadioLoHz: 7_205_000,
            CtunEnabled: false,
            MaxReceivers: 1);

        using var doc = JsonDocument.Parse(Protocol3SidecarBridge.BuildReceiverSettingsJson(
            state,
            rxStreams: 1,
            sampleRateHz: 192_000));
        var rx = doc.RootElement[0];

        Assert.Equal((int)RxMode.USB, rx.GetProperty("mode").GetInt32());
        Assert.Equal(150, rx.GetProperty("passbandLowHz").GetInt32());
        Assert.Equal(2_850, rx.GetProperty("passbandHighHz").GetInt32());
        Assert.Equal(7_205_000, rx.GetProperty("centerFrequencyHz").GetInt64());
        Assert.Equal(4_000, rx.GetProperty("tuningOffsetHz").GetInt32());
    }

    [Fact]
    public void BuildReceiverSettingsJson_UsesEffectiveCwLoForTuningOffset()
    {
        var state = new StateDto(
            ConnectionStatus.Connected,
            "192.168.1.25:1030",
            VfoHz: 14_046_000,
            Mode: RxMode.CWU,
            FilterLowHz: 300,
            FilterHighHz: 700,
            SampleRate: 192_000,
            RadioLoHz: 14_045_000,
            CtunEnabled: true,
            MaxReceivers: 1);

        using var doc = JsonDocument.Parse(Protocol3SidecarBridge.BuildReceiverSettingsJson(
            state,
            rxStreams: 1,
            sampleRateHz: 192_000));
        var rx = doc.RootElement[0];

        Assert.Equal((int)RxMode.CWU, rx.GetProperty("mode").GetInt32());
        Assert.Equal(14_045_000, rx.GetProperty("centerFrequencyHz").GetInt64());
        Assert.Equal(400, rx.GetProperty("tuningOffsetHz").GetInt32());
    }

    [Fact]
    public void BuildReceiverSettingsJson_MapsFreeDvBelowTenMHzToSignedLsbPassband()
    {
        var state = new StateDto(
            ConnectionStatus.Connected,
            "192.168.1.25:1030",
            VfoHz: 7_177_000,
            Mode: RxMode.FreeDv,
            FilterLowHz: 100,
            FilterHighHz: 2_850,
            SampleRate: 192_000,
            RadioLoHz: 7_177_000,
            CtunEnabled: true,
            MaxReceivers: 1);

        using var doc = JsonDocument.Parse(Protocol3SidecarBridge.BuildReceiverSettingsJson(
            state,
            rxStreams: 1,
            sampleRateHz: 192_000));
        var rx = doc.RootElement[0];

        Assert.Equal((int)RxMode.LSB, rx.GetProperty("mode").GetInt32());
        Assert.Equal(-2_850, rx.GetProperty("passbandLowHz").GetInt32());
        Assert.Equal(-100, rx.GetProperty("passbandHighHz").GetInt32());
    }

    [Fact]
    public void BuildReceiverSettingsJson_DisablesAgcOnlyForFixedMode()
    {
        var state = new StateDto(
            ConnectionStatus.Connected,
            "192.168.1.25:1030",
            VfoHz: 7_209_000,
            Mode: RxMode.LSB,
            FilterLowHz: -3_900,
            FilterHighHz: -100,
            SampleRate: 192_000,
            Agc: new AgcConfig(AgcMode.Fixed),
            MaxReceivers: 1);

        using var doc = JsonDocument.Parse(Protocol3SidecarBridge.BuildReceiverSettingsJson(
            state,
            rxStreams: 1,
            sampleRateHz: 192_000));
        var rx = doc.RootElement[0];

        Assert.Equal(0, rx.GetProperty("enableRxAgc").GetInt32());
        Assert.Equal(0, rx.GetProperty("rxAgcProfile").GetInt32());
    }

    [Fact]
    public void TryParseSidecarPidRecord_AcceptsOwnedLoopbackDiagnosticsRecord()
    {
        var text = """
            pid=12345
            startUnixMs=1783095000123
            diagnosticsUrl=http://127.0.0.1:49737/api/diagnostics/v2
            """;

        Assert.True(Protocol3SidecarBridge.TryParseSidecarPidRecord(text, out var record));
        Assert.Equal(12345, record.ProcessId);
        Assert.Equal(1783095000123, record.StartUnixMs);
        Assert.Equal("http://127.0.0.1:49737/api/diagnostics/v2", record.DiagnosticsUrl);
    }

    [Fact]
    public void TryParseSidecarPidRecord_RejectsUnsafeOrMalformedRecords()
    {
        Assert.False(Protocol3SidecarBridge.TryParseSidecarPidRecord(
            """
            pid=12345
            startUnixMs=1783095000123
            diagnosticsUrl=http://192.168.1.25:49737/api/diagnostics/v2
            """,
            out _));

        Assert.False(Protocol3SidecarBridge.TryParseSidecarPidRecord(
            """
            pid=12345
            diagnosticsUrl=http://127.0.0.1:49737/api/diagnostics/v2
            """,
            out _));
    }

    [Fact]
    public void StartUnixMsMatches_AllowsSmallProcessTimestampRoundingOnly()
    {
        Assert.True(Protocol3SidecarBridge.StartUnixMsMatches(1783095000123, 1783095001000));
        Assert.False(Protocol3SidecarBridge.StartUnixMsMatches(1783095000123, 1783095002000));
    }

    [Fact]
    public void TryParseWidebandDisplayFrame_AcceptsFullSpanProjection()
    {
        var root = DisplayFrames(width: 4096, centerHz: 30_000_000, spanHz: 60_000_000);

        Assert.True(Protocol3SidecarBridge.TryParseWidebandDisplayFrame(root, 2048, out var frame));

        Assert.NotNull(frame);
        Assert.Equal(2048, frame.PanDb.Length);
        Assert.Equal(2048, frame.WfDb.Length);
        Assert.Equal(30_000_000, frame.CenterHz);
        Assert.Equal(60_000_000f / 2048f, frame.HzPerPixel, precision: 3);
        Assert.Equal(1, frame.PanDb[0]);
        Assert.Equal(4095, frame.PanDb[^1]);
    }

    [Fact]
    public void TryParseWidebandDisplayFrame_RejectsDdcProjection()
    {
        var root = DisplayFrames(width: 2048, centerHz: 7_202_000, spanHz: 1_536_000);

        Assert.False(Protocol3SidecarBridge.TryParseWidebandDisplayFrame(root, 2048, out var frame));
        Assert.Null(frame);
    }

    private static JsonObject Diagnostics(int streams, string engine, string status) => new()
    {
        ["protocol"] = "p3",
        ["source"] = "radioSession",
        ["p3"] = new JsonObject
        {
            ["status"] = status,
            ["rxExpectedStreams"] = streams,
            ["rxActiveStreams"] = streams,
            ["dsp"] = new JsonObject
            {
                ["engine"] = engine,
                ["activeChannels"] = streams,
            },
        },
    };

    private static JsonObject DisplayFrames(int width, long centerHz, double spanHz)
    {
        var pan = new JsonArray();
        var wf = new JsonArray();
        for (var i = 0; i < width; i++)
        {
            pan.Add(i);
            wf.Add(-100);
        }

        var hzPerPixel = spanHz / width;
        return new JsonObject
        {
            ["schema"] = "n9war.zeus.protocol3.display_frames.v1",
            ["protocol"] = "p3",
            ["frames"] = new JsonArray
            {
                new JsonObject
                {
                    ["contract"] = "Zeus.Contracts.DisplayFrame.v1",
                    ["source"] = "n9dsp-sidecar-wideband-displayFrame",
                    ["ready"] = true,
                    ["rxId"] = 0,
                    ["bodyFlags"] = "PanValid|WfValid",
                    ["width"] = width,
                    ["centerHz"] = centerHz,
                    ["hzPerPixel"] = hzPerPixel,
                    ["spanHz"] = spanHz,
                    ["rfStartFrequencyHz"] = centerHz - (spanHz / 2.0),
                    ["rfEndFrequencyHz"] = centerHz + (spanHz / 2.0),
                    ["lowToHigh"] = true,
                    ["panDb"] = pan,
                    ["wfDb"] = wf,
                },
            },
        };
    }
}
