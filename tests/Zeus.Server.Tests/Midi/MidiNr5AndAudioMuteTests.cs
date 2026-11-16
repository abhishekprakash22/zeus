// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// MIDI: Noise Reduction 5 on/off + amount, and the transport bar's master
// audio mute, routed through the real RadioService.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Zeus.Server.Midi;

namespace Zeus.Server.Tests.Midi;

public sealed class MidiNr5AndAudioMuteTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-midi-nr5-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    private (MidiCommandDispatcher D, RadioService Radio) Build()
    {
        var lf = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(lf, dspStore, paStore);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), lf);
        var tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        var state = new MidiDispatchState(radio.Snapshot);
        return (new MidiCommandDispatcher(radio, tx, state, NullLogger<MidiCommandDispatcher>.Instance), radio);
    }

    [Fact]
    public void Nr5OnOff_TogglesNeuralNr_LikeNr1ToNr4()
    {
        var (d, radio) = Build();
        d.Dispatch(ZeusMidiCommand.Nr5OnOff, value: 127, delta: 0);
        Assert.Equal(NrMode.Nnr, radio.Snapshot().Nr?.NrMode);
        d.Dispatch(ZeusMidiCommand.Nr5OnOff, value: 127, delta: 0);
        Assert.Equal(NrMode.Off, radio.Snapshot().Nr?.NrMode);
    }

    [Fact]
    public void Nr5OnOff_FromAnotherNr_SwitchesToNr5()
    {
        var (d, radio) = Build();
        d.Dispatch(ZeusMidiCommand.Nr2OnOff, value: 127, delta: 0);
        Assert.Equal(NrMode.Emnr, radio.Snapshot().Nr?.NrMode);
        d.Dispatch(ZeusMidiCommand.Nr5OnOff, value: 127, delta: 0);
        Assert.Equal(NrMode.Nnr, radio.Snapshot().Nr?.NrMode);
    }

    [Fact]
    public void Nr5OnOff_ReleaseIsIgnored()
    {
        var (d, radio) = Build();
        d.Dispatch(ZeusMidiCommand.Nr5OnOff, value: 0, delta: 0);
        Assert.NotEqual(NrMode.Nnr, radio.Snapshot().Nr?.NrMode ?? NrMode.Off);
    }

    [Fact]
    public void Nr5Amount_TurningUpMeansMoreSuppression()
    {
        var (d, radio) = Build();
        d.Dispatch(ZeusMidiCommand.Nr5Amount, value: 0, delta: 0);
        Assert.Equal(MidiCommandDispatcher.Nr5FloorLeastDb, radio.Snapshot().Nr?.NnrMaskFloorDb);
        d.Dispatch(ZeusMidiCommand.Nr5Amount, value: 127, delta: 0);
        Assert.Equal(MidiCommandDispatcher.Nr5FloorMostDb, radio.Snapshot().Nr?.NnrMaskFloorDb);
    }

    [Fact]
    public void Nr5Amount_LeavesModeAndModelAlone()
    {
        var (d, radio) = Build();
        d.Dispatch(ZeusMidiCommand.Nr5OnOff, value: 127, delta: 0);
        radio.SetNnr(new NnrConfigSetRequest(ModelSlot: 1));
        d.Dispatch(ZeusMidiCommand.Nr5Amount, value: 64, delta: 0);
        var nr = radio.Snapshot().Nr!;
        Assert.Equal(NrMode.Nnr, nr.NrMode);
        Assert.Equal(1, nr.NnrModelSlot);
        Assert.InRange(nr.NnrMaskFloorDb!.Value, -31.0, -29.0);
    }

    [Fact]
    public void AudioMuteOnOff_TogglesMasterMute_TaggedAsMidi()
    {
        var (d, _) = Build();
        var mute = new RxAudioMuteState();
        d.AudioMute = mute;
        int changes = 0;
        mute.Changed += () => changes++;

        d.Dispatch(ZeusMidiCommand.AudioMuteOnOff, value: 127, delta: 0);
        Assert.True(mute.IsMuted);
        Assert.Equal(MuteChangeSource.Midi, mute.LastSource);
        d.Dispatch(ZeusMidiCommand.AudioMuteOnOff, value: 0, delta: 0); // release: no-op
        Assert.True(mute.IsMuted);
        d.Dispatch(ZeusMidiCommand.AudioMuteOnOff, value: 127, delta: 0);
        Assert.False(mute.IsMuted);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void AudioMuteOnOff_IsDistinctFromRx1Mute()
    {
        var (d, radio) = Build();
        var mute = new RxAudioMuteState();
        d.AudioMute = mute;
        d.Dispatch(ZeusMidiCommand.AudioMuteOnOff, value: 127, delta: 0);
        Assert.True(mute.IsMuted);
        Assert.False(radio.Snapshot().Rx1Muted);
    }

    [Fact]
    public void AudioMuteOnOff_WithoutWiredState_IsSafeNoOp()
    {
        var (d, _) = Build();
        d.Dispatch(ZeusMidiCommand.AudioMuteOnOff, value: 127, delta: 0);
    }

    [Fact]
    public void MuteFrame_CarriesStateAndSource()
    {
        var mute = new RxAudioMuteState();
        mute.SetMuted(true);
        Assert.Equal(new byte[] { 0x3F, 1, 0 }, mute.ToFrame());
        mute.SetMuted(false, MuteChangeSource.Midi);
        Assert.Equal(new byte[] { 0x3F, 0, 1 }, mute.ToFrame());
    }

    [Fact]
    public void Catalog_ListsNr5AndAudioMute_AsSupported_BesideTheirSiblings()
    {
        var all = MidiCommandCatalog.All.ToList();
        int nr4 = all.FindIndex(c => c.Command == ZeusMidiCommand.Nr4Amount);
        Assert.Equal(ZeusMidiCommand.Nr5OnOff, all[nr4 + 1].Command);
        Assert.Equal(ZeusMidiCommand.Nr5Amount, all[nr4 + 2].Command);
        var mute = all.Single(c => c.Command == ZeusMidiCommand.AudioMuteOnOff);
        Assert.True(mute.Supported);
        Assert.True(mute.IsToggle);
        Assert.Equal(MidiControlType.Button, mute.ControlType);
        Assert.True(all.Single(c => c.Command == ZeusMidiCommand.Nr5OnOff).Supported);
        Assert.Equal(MidiControlType.KnobOrSlider, all.Single(c => c.Command == ZeusMidiCommand.Nr5Amount).ControlType);
    }

    [Fact]
    public void Catalog_CoversEveryCommandExactlyOnce()
    {
        var listed = MidiCommandCatalog.All.Select(c => c.Command).ToList();
        Assert.Equal(listed.Count, listed.Distinct().Count());
        Assert.Equal(Enum.GetValues<ZeusMidiCommand>().Length, listed.Count);
    }

    [Fact]
    public void NewCommands_AreAppended_SoStoredOrdinalsDoNotMove()
    {
        // Saved bindings store the enum ordinal. ESCFormOnOff was the last
        // Thetis-parity value; the ANAN Core additions must follow it.
        int esc = (int)ZeusMidiCommand.ESCFormOnOff;
        Assert.Equal((int)ZeusMidiCommand.Nr5OnOff, esc + 1);
        Assert.Equal((int)ZeusMidiCommand.Nr5Amount, esc + 2);
        Assert.Equal((int)ZeusMidiCommand.AudioMuteOnOff, esc + 3);
    }
}
