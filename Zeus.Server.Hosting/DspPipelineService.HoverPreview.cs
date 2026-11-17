// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Hover to listen. A dedicated preview RXA channel is fed RX1's I/Q — the
// samples RX1 is already receiving — and shifted to the frequency under the
// operator's pointer, with RX1's mode, filter and AGC. Its audio is laid over
// the normal mix, which is ducked while the preview is heard, so the operator
// hears what is at a spot on the panadapter without moving the dial.
//
// The preview is a LEASE: every pointer update renews it and it lapses on its
// own shortly after the last one, so a client that vanishes (closed tab,
// dropped remote link) can never leave the preview playing. It is silent
// while keyed. The channel is opened on first use, fed only while a preview
// is live or fading, and closed after a spell of disuse.

using Zeus.Contracts;
using Zeus.Dsp;

namespace Zeus.Server;

/// <summary>Answer to a preview request.</summary>
public sealed record HoverPreviewResult(bool Active, string? Reason);

public partial class DspPipelineService
{
    /// <summary>Main audio level under the preview: −15 dB.</summary>
    internal const float HoverDuckGain = 0.178f;
    /// <summary>The preview lapses this long after its last renewal.</summary>
    internal const int HoverLeaseMs = 1_500;
    /// <summary>An unused preview channel is closed after this long.</summary>
    internal const int HoverIdleCloseMs = 20_000;
    /// <summary>Crossfade length in/out of the preview: 30 ms.</summary>
    internal const float HoverRampStep = 1f / (0.030f * AudioOutputRateHz);
    /// <summary>Fraction of the DDC half-bandwidth a preview may reach — the
    /// decimation filter's skirts make the outermost few percent unusable.</summary>
    internal const double HoverUsableBandFraction = 0.94;

    private int _hoverChannelId = -1;            // Volatile; -1 = no channel
    private IDspEngine? _hoverEngine;             // engine the channel lives on
    private int _hoverChannelRateHz;
    private long _hoverLeaseUntilMs;              // Environment.TickCount64
    private volatile bool _hoverFeeding;          // RX1 IQ is being fed to the channel
    private volatile bool _hoverFlushPending;     // discard stale audio before the next mix
    private float _hoverMix;                      // 0..1 crossfade position (tick thread only)
    private readonly float[] _hoverAudioBuf = new float[AudioDrainCapacity];
    private Timer? _hoverIdleTimer;
    // One deliberate full re-push shortly after the channel opens: the first
    // push into a just-opened WDSP channel does not always take (the same
    // reason the secondary receivers resync once after open).
    private long _hoverResyncAtMs = -1;
    private const int HoverOpenResyncDelayMs = 300;

    // What was last pushed to the preview channel (request path, under _engineLock).
    private RxMode? _hoverAppliedMode;
    private int _hoverAppliedLow = int.MinValue;
    private int _hoverAppliedHigh = int.MinValue;
    private int _hoverAppliedShiftHz = int.MinValue;
    private double _hoverAppliedAgcTop = double.NaN;
    private double _hoverAppliedAfDb = double.NaN;
    private AgcConfig? _hoverAppliedAgc;
    private BandpassWindow? _hoverAppliedWindow;

    /// <summary>
    /// Where a preview at <paramref name="hz"/> sits in RX1's captured window,
    /// and whether it fits: the WDSP shift from the DDC centre, the engine mode
    /// and signed filter, or a reason it can't be heard. Pure, for tests.
    /// </summary>
    internal static (bool Ok, string? Reason, int ShiftHz, RxMode EngineMode, int Low, int High) PlanHoverPreview(
        StateDto s, long hz, int sampleRateHz)
    {
        var engineMode = RadioService.EffectiveEngineMode(s.Mode, hz);
        var (low, high) = SignedRxFilterFor(s, engineMode);
        if (sampleRateHz <= 0) return (false, "no receiver running", 0, engineMode, low, high);
        long shift = CwOffset.EffectiveLoHz(s.Mode, hz) - s.RadioLoHz;
        long edge = Math.Max(Math.Abs((long)low), Math.Abs((long)high));
        double reach = sampleRateHz / 2.0 * HoverUsableBandFraction;
        if (Math.Abs(shift) + edge > reach)
            return (false, "outside the receiver's window", 0, engineMode, low, high);
        return (true, null, (int)shift, engineMode, low, high);
    }

    /// <summary>Start or move the preview to <paramref name="hz"/> and renew
    /// its lease. Called from the REST endpoint at pointer rate (a few Hz).</summary>
    public HoverPreviewResult RequestHoverPreview(long hz)
    {
        lock (_engineLock)
        {
            var engine = _engine;
            if (engine is null) return new HoverPreviewResult(false, "no radio connected");
            var s = _radio.Snapshot();
            int rate = Volatile.Read(ref _sampleRateHz);
            var plan = PlanHoverPreview(s, hz, rate);
            if (!plan.Ok)
            {
                // Let a live preview fade rather than hang on at the old spot.
                Volatile.Write(ref _hoverLeaseUntilMs, 0);
                return new HoverPreviewResult(false, plan.Reason);
            }

            if (_tsReplay)
                return new HoverPreviewResult(false, "rewind is playing");

            int chan = EnsureAuxChannelLocked(engine, rate);
            ApplyAuxConfigLocked(engine, chan, s, plan.EngineMode, plan.Low, plan.High);
            if (_hoverAppliedShiftHz != plan.ShiftHz)
            {
                engine.SetCtunShift(chan, plan.ShiftHz);
                _hoverAppliedShiftHz = plan.ShiftHz;
            }

            if (!_hoverFeeding) _hoverFlushPending = true; // drop audio left from the last preview
            Volatile.Write(ref _hoverChannelId, chan);
            Volatile.Write(ref _hoverLeaseUntilMs, Environment.TickCount64 + HoverLeaseMs);
            _hoverFeeding = true;

            _hoverIdleTimer ??= new Timer(_ => CloseIdleHoverChannel(), null, Timeout.Infinite, Timeout.Infinite);
            _hoverIdleTimer.Change(HoverIdleCloseMs, Timeout.Infinite);
            return new HoverPreviewResult(true, null);
        }
    }

    /// <summary>
    /// The auxiliary RXA channel shared by hover-to-listen and rewind (only one
    /// of them plays at a time): open it if needed, replacing one left over
    /// from a swapped engine or a different DDC rate. Caller holds _engineLock.
    /// </summary>
    private int EnsureAuxChannelLocked(IDspEngine engine, int rate)
    {
        int chan = Volatile.Read(ref _hoverChannelId);
        bool stale = chan >= 0 && (!ReferenceEquals(engine, _hoverEngine) || rate != _hoverChannelRateHz);
        if (stale)
        {
            // Engine swapped (its channels went with it) or the DDC rate
            // changed: the old channel is wrong either way.
            CloseHoverChannelLocked(closeOnEngine: ReferenceEquals(engine, _hoverEngine));
            chan = -1;
        }
        if (chan < 0)
        {
            chan = engine.OpenChannel(rate, Width);
            _hoverEngine = engine;
            _hoverChannelRateHz = rate;
            ResetHoverApplied();
            _hoverResyncAtMs = Environment.TickCount64 + HoverOpenResyncDelayMs;
            Volatile.Write(ref _hoverChannelId, chan);
            _log.LogInformation("dsp.aux opened channel={Channel} rate={Rate}", chan, rate);
        }
        else if (_hoverResyncAtMs >= 0 && Environment.TickCount64 >= _hoverResyncAtMs)
        {
            _hoverResyncAtMs = -1;
            ResetHoverApplied();
        }
        return chan;
    }

    /// <summary>Give the auxiliary channel RX1's demodulator settings (mode,
    /// signed filter, filter window, AGC, AF gain), pushing only what changed.
    /// The frequency shift is the caller's. Caller holds _engineLock.</summary>
    private void ApplyAuxConfigLocked(IDspEngine engine, int chan, StateDto s, RxMode engineMode, int low, int high)
    {
        if (_hoverAppliedMode != engineMode)
        {
            engine.SetMode(chan, engineMode);
            _hoverAppliedMode = engineMode;
        }
        if (_hoverAppliedLow != low || _hoverAppliedHigh != high)
        {
            engine.SetFilter(chan, low, high);
            _hoverAppliedLow = low;
            _hoverAppliedHigh = high;
        }
        if (_hoverAppliedWindow != s.RxFilterWindow)
        {
            engine.SetRxBandpassWindow(chan, s.RxFilterWindow);
            _hoverAppliedWindow = s.RxFilterWindow;
        }
        var agc = s.Agc ?? new AgcConfig(AgcMode.Med);
        if (_hoverAppliedAgc != agc)
        {
            engine.SetAgc(chan, agc);
            _hoverAppliedAgc = agc;
        }
        double agcTop = s.AgcTopDb + s.AgcOffsetDb;
        if (_hoverAppliedAgcTop != agcTop)
        {
            engine.SetAgcTop(chan, agcTop);
            _hoverAppliedAgcTop = agcTop;
        }
        // FreeDV holds RX1's panel gain at unity; this channel is plain
        // SSB/CW/AM, so it takes the operator's AF setting directly.
        if (_hoverAppliedAfDb != s.RxAfGainDb)
        {
            engine.SetRxAfGainDb(chan, s.RxAfGainDb);
            _hoverAppliedAfDb = s.RxAfGainDb;
        }
    }

    /// <summary>End the preview now (pointer left the spectrum, click, key-down).
    /// The audio fades out over the crossfade; the channel stays for reuse.</summary>
    public void StopHoverPreview() => Volatile.Write(ref _hoverLeaseUntilMs, 0);

    private void ResetHoverApplied()
    {
        _hoverAppliedMode = null;
        _hoverAppliedLow = int.MinValue;
        _hoverAppliedHigh = int.MinValue;
        _hoverAppliedShiftHz = int.MinValue;
        _hoverAppliedAgcTop = double.NaN;
        _hoverAppliedAfDb = double.NaN;
        _hoverAppliedAgc = null;
        _hoverAppliedWindow = null;
    }

    private void CloseHoverChannelLocked(bool closeOnEngine)
    {
        int chan = Volatile.Read(ref _hoverChannelId);
        _hoverFeeding = false;
        Volatile.Write(ref _hoverChannelId, -1);
        var engine = _hoverEngine;
        _hoverEngine = null;
        ResetHoverApplied();
        if (chan < 0 || !closeOnEngine || engine is null) return;
        try { engine.CloseChannel(chan); }
        catch (Exception ex) { _log.LogDebug(ex, "dsp.hover close failed channel={Channel}", chan); }
        _log.LogInformation("dsp.hover closed channel={Channel}", chan);
    }

    private void CloseIdleHoverChannel()
    {
        lock (_engineLock)
        {
            if (Volatile.Read(ref _hoverChannelId) < 0) return;
            if (_tsReplay)
            {
                // Rewind is using the channel; look again later.
                _hoverIdleTimer?.Change(HoverIdleCloseMs, Timeout.Infinite);
                return;
            }
            // Renewed since the timer was armed: its own re-arm will come back.
            if (Environment.TickCount64 < Volatile.Read(ref _hoverLeaseUntilMs) + HoverIdleCloseMs - 500) return;
            CloseHoverChannelLocked(closeOnEngine: ReferenceEquals(_engine, _hoverEngine));
        }
    }

    /// <summary>RX-loop thread: hand RX1's I/Q to the preview channel while a
    /// preview is live or fading. One volatile read when there is none.</summary>
    private void FeedHoverPreview(IDspEngine engine, ReadOnlySpan<double> rx1Iq)
    {
        if (!_hoverFeeding || _tsReplay) return; // during a rewind the channel is fed from the ring
        int chan = Volatile.Read(ref _hoverChannelId);
        if (chan < 0 || !ReferenceEquals(engine, Volatile.Read(ref _hoverEngine))) return;
        engine.FeedIq(chan, rx1Iq);
    }

    /// <summary>DSP tick: lay the preview over the mixed receive audio.</summary>
    private void MixHoverPreviewTick(IDspEngine engine, float[] audioBuf, int count, bool keyed)
    {
        int chan = Volatile.Read(ref _hoverChannelId);
        if (chan < 0 || !ReferenceEquals(engine, Volatile.Read(ref _hoverEngine)))
        {
            _hoverMix = 0f;
            return;
        }
        if (count <= 0) return;
        bool replay = TimeShiftReplayForTick(keyed);
        bool live = replay || (!keyed && Environment.TickCount64 < Volatile.Read(ref _hoverLeaseUntilMs));
        if (!live && _hoverMix <= 0f)
        {
            // Fully faded out: stop paying for the channel until the next preview.
            _hoverFeeding = false;
            return;
        }

        int want = Math.Min(count, _hoverAudioBuf.Length);
        if (_hoverFlushPending)
        {
            _hoverFlushPending = false;
            DiscardHoverAudio(engine, chan, engine.AudioBufferedSamples(chan));
        }
        else
        {
            // Bound latency the way the secondaries do: the preview must track
            // the pointer, not play from a backlog.
            int buffered = engine.AudioBufferedSamples(chan);
            if (buffered > want * 6) DiscardHoverAudio(engine, chan, buffered - want * 2);
        }
        int n = engine.ReadAudio(chan, _hoverAudioBuf.AsSpan(0, want));
        // Hover: duck only once preview audio is actually flowing, so opening
        // the channel never punches a hole in the main audio. Rewind: the past
        // REPLACES the present outright — and stays silent, not live, while
        // paused or aimed outside the recorded window, so what is heard is
        // never ambiguous.
        float target = replay || (live && n > 0) ? 1f : 0f;
        _hoverMix = MixHoverPreview(
            audioBuf.AsSpan(0, count), _hoverAudioBuf.AsSpan(0, Math.Max(0, n)),
            _hoverMix, target, HoverRampStep, replay ? 0f : HoverDuckGain);
    }

    private void DiscardHoverAudio(IDspEngine engine, int chan, int samples)
    {
        while (samples > 0)
        {
            int got = engine.ReadAudio(chan, _hoverAudioBuf.AsSpan(0, Math.Min(samples, _hoverAudioBuf.Length)));
            if (got <= 0) break;
            samples -= got;
        }
    }

    /// <summary>
    /// Crossfade the preview over the main audio in place. <paramref name="mix"/>
    /// moves toward <paramref name="target"/> by <paramref name="step"/> per
    /// sample; at mix = 1 the main audio is at <paramref name="duck"/> and the
    /// preview at full level. Returns the new mix. Pure, for tests.
    /// </summary>
    internal static float MixHoverPreview(
        Span<float> main, ReadOnlySpan<float> preview, float mix, float target, float step, float duck)
    {
        for (int i = 0; i < main.Length; i++)
        {
            if (mix < target) mix = Math.Min(target, mix + step);
            else if (mix > target) mix = Math.Max(target, mix - step);
            float p = i < preview.Length ? preview[i] : 0f;
            main[i] = main[i] * (1f - mix * (1f - duck)) + p * mix;
        }
        return mix;
    }
}
