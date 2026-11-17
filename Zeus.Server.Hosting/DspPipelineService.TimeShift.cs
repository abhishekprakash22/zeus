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
// Rewind (I/Q time-shift). While it is switched on, RX1's I/Q is kept in a
// ring (IqTimeShiftBuffer). A replay feeds the auxiliary RXA channel from the
// ring instead of from the antenna, at the pace the live samples arrive — so
// it runs a fixed delay behind real time and never drifts — and its audio
// replaces RX1's. The replay channel follows RX1's dial, mode, filter and
// AGC, so the operator can retune inside the recorded spectrum while
// listening to the past: what was said on a frequency they were not on.
//
// A replay is leased by the client (renewed every second or so) and ends on
// its own if the renewals stop; key-down ends it at once. The auxiliary
// channel is the one hover-to-listen uses; only one of the two plays.

using Zeus.Contracts;
using Zeus.Dsp;

namespace Zeus.Server;

/// <summary>Operator's rewind setting (kept radio-side).</summary>
public sealed record TimeShiftPrefs(bool Enabled = false, int Seconds = 60);

/// <summary>Snapshot of the rewind buffer and any replay.</summary>
public sealed record TimeShiftStatus(
    bool Enabled,
    int RequestedSeconds,
    double CapacitySeconds,
    double BufferedSeconds,
    int RateHz,
    long MemoryBytes,
    bool Replaying,
    bool Paused,
    double DelaySeconds,
    bool OutOfWindow,
    string? Message);

public partial class DspPipelineService
{
    internal const int TimeShiftMinSeconds = 10;
    internal const int TimeShiftMaxSeconds = 300;
    /// <summary>A replay ends this long after the client's last renewal.</summary>
    internal const int TimeShiftLeaseMs = 6_000;
    private const int TimeShiftScratchSamples = 4096;

    private readonly IqTimeShiftBuffer _tsBuf = new();
    private readonly double[] _tsScratch = new double[TimeShiftScratchSamples * 2];
    private volatile bool _tsEnabled;
    private volatile int _tsSeconds = 60;
    private volatile bool _tsReplay;
    private volatile bool _tsPaused;
    private volatile bool _tsOutOfWindow;
    private long _tsSeekMs = -1;            // Interlocked: pending "go to this delay", −1 = none
    private long _tsCursor;                 // RX thread writes; status reads
    private long _tsLeaseUntilMs;
    private long _tsLiveLoHz;               // hardware centre the live I/Q is captured at
    private long _tsTargetLoHz;             // EffectiveLo(mode, dial): where the replay passband sits
    private volatile int _tsEdgeHz;         // widest passband edge, for the window check
    private volatile int _tsAppliedShiftHz = int.MinValue;

    internal static int ClampTimeShiftSeconds(int seconds) =>
        Math.Clamp(seconds, TimeShiftMinSeconds, TimeShiftMaxSeconds);

    /// <summary>
    /// Whether a replay aimed at <paramref name="targetLoHz"/> can be heard
    /// from a block captured with the hardware centred on
    /// <paramref name="blockLoHz"/>, and the shift that places it. Pure.
    /// </summary>
    internal static (bool InWindow, int ShiftHz) PlanTimeShiftBlock(
        long targetLoHz, long blockLoHz, int edgeHz, int sampleRateHz)
    {
        long shift = targetLoHz - blockLoHz;
        double reach = sampleRateHz / 2.0 * HoverUsableBandFraction;
        bool ok = sampleRateHz > 0 && Math.Abs(shift) + Math.Abs((long)edgeHz) <= reach;
        return (ok, ok ? (int)shift : 0);
    }

    /// <summary>Where a replay cursor lands for a requested delay: that far
    /// behind the newest sample, never outside what the ring holds. Pure.</summary>
    internal static long TimeShiftCursorFor(long written, long oldest, int rateHz, long delayMs)
    {
        long back = Math.Max(0, delayMs) * rateHz / 1000;
        return Math.Clamp(written - back, oldest, written);
    }

    /// <summary>Switch the rewind buffer on or off and set its length.</summary>
    public TimeShiftStatus ConfigureTimeShift(TimeShiftPrefs prefs)
    {
        _tsSeconds = ClampTimeShiftSeconds(prefs.Seconds);
        Volatile.Write(ref _tsLiveLoHz, _radio.Snapshot().RadioLoHz);
        if (!prefs.Enabled) _tsReplay = false;
        _tsEnabled = prefs.Enabled;
        return GetTimeShiftStatus();
    }

    public TimeShiftStatus GetTimeShiftStatus(string? message = null)
    {
        int rate = _tsBuf.RateHz;
        long written = _tsBuf.Written;
        long oldest = _tsBuf.Oldest;
        bool replay = _tsReplay;
        double perSec = rate > 0 ? rate : 1;
        return new TimeShiftStatus(
            Enabled: _tsEnabled,
            RequestedSeconds: _tsSeconds,
            CapacitySeconds: rate > 0 ? _tsBuf.CapacitySamples / perSec : 0,
            BufferedSeconds: rate > 0 ? (written - oldest) / perSec : 0,
            RateHz: rate,
            MemoryBytes: _tsBuf.MemoryBytes,
            Replaying: replay,
            Paused: replay && _tsPaused,
            DelaySeconds: replay && rate > 0
                ? Math.Max(0, written - Math.Max(oldest, Volatile.Read(ref _tsCursor))) / perSec
                : 0,
            OutOfWindow: replay && _tsOutOfWindow,
            Message: message);
    }

    /// <summary>Start a replay <paramref name="delaySeconds"/> behind real
    /// time, or move a running one there. Renews the lease.</summary>
    public TimeShiftStatus StartTimeShiftReplay(double delaySeconds)
    {
        lock (_engineLock)
        {
            var engine = _engine;
            if (engine is null) return GetTimeShiftStatus("no radio connected");
            if (!_tsEnabled) return GetTimeShiftStatus("rewind is switched off");
            int rate = Volatile.Read(ref _sampleRateHz);
            if (rate <= 0 || _tsBuf.RateHz != rate || _tsBuf.Written == 0)
                return GetTimeShiftStatus("nothing recorded yet");
            if (!double.IsFinite(delaySeconds) || delaySeconds < 0) delaySeconds = 0;

            var s = _radio.Snapshot();
            int chan = EnsureAuxChannelLocked(engine, rate);
            ApplyTimeShiftStateLocked(engine, chan, s);

            if (!_tsReplay)
            {
                // Taking the channel over from hover-to-listen (if it was on).
                Volatile.Write(ref _hoverLeaseUntilMs, 0);
                _hoverFeeding = false;
                _hoverFlushPending = true;
                _hoverAppliedShiftHz = int.MinValue; // the RX thread owns the shift during a replay
                _tsAppliedShiftHz = int.MinValue;
                _tsPaused = false;
            }
            Interlocked.Exchange(ref _tsSeekMs, (long)Math.Round(delaySeconds * 1000.0));
            Volatile.Write(ref _tsLeaseUntilMs, Environment.TickCount64 + TimeShiftLeaseMs);
            _tsReplay = true;

            _hoverIdleTimer ??= new Timer(_ => CloseIdleHoverChannel(), null, Timeout.Infinite, Timeout.Infinite);
            _hoverIdleTimer.Change(HoverIdleCloseMs, Timeout.Infinite);
            return GetTimeShiftStatus();
        }
    }

    /// <summary>Hold or resume a replay. While held the audio is silent and
    /// the delay grows with real time (until the ring runs out behind it).</summary>
    public TimeShiftStatus PauseTimeShiftReplay(bool paused)
    {
        if (_tsReplay)
        {
            _tsPaused = paused;
            Volatile.Write(ref _tsLeaseUntilMs, Environment.TickCount64 + TimeShiftLeaseMs);
        }
        return GetTimeShiftStatus();
    }

    /// <summary>Keep a replay alive; the client calls this about once a second.</summary>
    public TimeShiftStatus RenewTimeShiftReplay()
    {
        if (_tsReplay) Volatile.Write(ref _tsLeaseUntilMs, Environment.TickCount64 + TimeShiftLeaseMs);
        return GetTimeShiftStatus();
    }

    /// <summary>Back to live reception.</summary>
    public TimeShiftStatus StopTimeShiftReplay()
    {
        _tsReplay = false;
        _tsPaused = false;
        return GetTimeShiftStatus();
    }

    /// <summary>State-change hook (under _engineLock): track the live hardware
    /// centre, and keep a running replay on RX1's dial, mode and filter.</summary>
    private void TimeShiftOnStateChangedLocked(StateDto s)
    {
        Volatile.Write(ref _tsLiveLoHz, s.RadioLoHz);
        if (!_tsReplay) return;
        var engine = _engine;
        int chan = Volatile.Read(ref _hoverChannelId);
        if (engine is null || chan < 0 || !ReferenceEquals(engine, _hoverEngine)) return;
        ApplyTimeShiftStateLocked(engine, chan, s);
    }

    private void ApplyTimeShiftStateLocked(IDspEngine engine, int chan, StateDto s)
    {
        var engineMode = RadioService.EffectiveEngineMode(s.Mode, s.VfoHz);
        var (low, high) = SignedRxFilterFor(s, engineMode);
        ApplyAuxConfigLocked(engine, chan, s, engineMode, low, high);
        _tsEdgeHz = Math.Max(Math.Abs(low), Math.Abs(high));
        Volatile.Write(ref _tsTargetLoHz, CwOffset.EffectiveLoHz(s.Mode, s.VfoHz));
    }

    /// <summary>
    /// RX-loop thread, once per RX1 I/Q block: record it, and when a replay is
    /// running feed the auxiliary channel the same number of samples from the
    /// ring — the live sample clock paces the replay.
    /// </summary>
    private void TimeShiftOnRx1Iq(IDspEngine engine, ReadOnlySpan<double> iq)
    {
        if (!_tsEnabled)
        {
            if (_tsBuf.CapacitySamples != 0) _tsBuf.Release();
            return;
        }
        int rate = Volatile.Read(ref _sampleRateHz);
        if (rate <= 0) return;
        if (_tsBuf.Ensure(rate, _tsSeconds))
        {
            // New rate or length: the ring starts over, and a replay of the
            // old contents has nothing left to play.
            _tsReplay = false;
        }
        _tsBuf.Append(iq, Volatile.Read(ref _tsLiveLoHz));
        if (!_tsReplay) return;

        int chan = Volatile.Read(ref _hoverChannelId);
        if (chan < 0 || !ReferenceEquals(engine, Volatile.Read(ref _hoverEngine))) return;

        long written = _tsBuf.Written;
        long oldest = _tsBuf.Oldest;
        long cursor = _tsCursor;
        long seek = Interlocked.Exchange(ref _tsSeekMs, -1);
        if (seek >= 0) cursor = TimeShiftCursorFor(written, oldest, rate, seek);
        cursor = Math.Clamp(cursor, oldest, written);

        if (!_tsPaused)
        {
            int remaining = iq.Length / 2;
            long target = Volatile.Read(ref _tsTargetLoHz);
            int edge = _tsEdgeHz;
            while (remaining > 0)
            {
                int got = _tsBuf.Read(
                    cursor, _tsScratch.AsSpan(0, Math.Min(remaining, TimeShiftScratchSamples) * 2), out long blockLo);
                if (got <= 0) break;
                var plan = PlanTimeShiftBlock(target, blockLo, edge, rate);
                if (plan.InWindow)
                {
                    if (plan.ShiftHz != _tsAppliedShiftHz)
                    {
                        engine.SetCtunShift(chan, plan.ShiftHz);
                        _tsAppliedShiftHz = plan.ShiftHz;
                    }
                    engine.FeedIq(chan, _tsScratch.AsSpan(0, got * 2));
                }
                _tsOutOfWindow = !plan.InWindow;
                cursor += got;
                remaining -= got;
            }
        }
        Volatile.Write(ref _tsCursor, cursor);
    }

    /// <summary>DSP tick: is a replay playing this tick? Ends it on key-down
    /// and when the client's lease has lapsed.</summary>
    private bool TimeShiftReplayForTick(bool keyed)
    {
        if (!_tsReplay) return false;
        if (keyed || Environment.TickCount64 >= Volatile.Read(ref _tsLeaseUntilMs))
        {
            _tsReplay = false;
            _tsPaused = false;
            return false;
        }
        return true;
    }
}
