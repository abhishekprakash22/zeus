// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// One receiver's Auto-AGC-T servo state and tick logic — the noise-floor
/// window, its source tag, the throttle timer, the fast-attack VFO memory and
/// the resulting offset. Extracted from <see cref="RadioService"/> unchanged
/// so RX1 and RX2 (and RX3+) each run the identical Thetis-faithful loop on
/// their own state, fed by their own panadapter floor and their own S-meter.
/// The caller (RadioService) owns the lock, the state DTO and the DSP
/// hand-off; this class owns nothing but the servo.
/// </summary>
internal sealed class AutoAgcServo
{
    private readonly int _windowSamples;
    private readonly int _minSamples;
    private readonly double _percentile;
    private readonly double _deadbandDb;
    private readonly double _fastAttackVfoDeltaHz;
    private readonly long _spectrumStaleMs;

    private readonly double[] _window;
    private int _windowIdx;
    private int _windowFill;
    // Which source is currently filling the floor window (0 none / 1 spectrum /
    // 2 S-meter fallback) and the last time a real spectrum floor arrived.
    private int _windowSource;
    private long _lastSpectrumFloorMs = long.MinValue;
    private long _lastTickMs = long.MinValue;
    private long _lastVfoHz = long.MinValue;

    /// <summary>Live offset the servo has settled on (dB above the baseline).</summary>
    public double OffsetDb { get; private set; }

    public AutoAgcServo(
        int windowSamples, int minSamples, double percentile,
        double deadbandDb, double fastAttackVfoDeltaHz, long spectrumStaleMs)
    {
        _windowSamples = windowSamples;
        _minSamples = minSamples;
        _percentile = percentile;
        _deadbandDb = deadbandDb;
        _fastAttackVfoDeltaHz = fastAttackVfoDeltaHz;
        _spectrumStaleMs = spectrumStaleMs;
        _window = new double[windowSamples];
    }

    /// <summary>Drop the floor window so the next samples re-seed from scratch
    /// (Thetis fast-attack: band change, attenuator step, preamp toggle, TX
    /// pause). The rolling spectrum-availability timestamp is kept.</summary>
    public void ResetWindow()
    {
        _windowFill = 0;
        _windowIdx = 0;
        _windowSource = 0;
    }

    /// <summary>Manual take-over or Auto off: zero the offset and forget the
    /// throttle so the next arm recalibrates from nothing.</summary>
    public void Disarm()
    {
        OffsetDb = 0.0;
        _lastTickMs = long.MinValue;
        ResetWindow();
    }

    /// <summary>Auto on: restart the throttle + window so the loop recalibrates.</summary>
    public void Arm()
    {
        _lastTickMs = long.MinValue;
        ResetWindow();
    }

    /// <summary>
    /// One servo tick. Returns true when <see cref="OffsetDb"/> moved (beyond the
    /// deadband) and the caller should publish the new offset; <paramref
    /// name="noiseFloorDbm"/> reports the floor estimate used (NaN when the
    /// window wasn't ready). <paramref name="topFromFloor"/> is the WDSP
    /// threshold→max-gain conversion for THIS receiver's passband and rate.
    /// </summary>
    public bool Tick(
        double signalDbm, double spectrumFloorDbm, long nowMs, long vfoHz,
        double baselineTopDb, Func<double, double> topFromFloor,
        out double noiseFloorDbm)
    {
        noiseFloorDbm = double.NaN;
        bool hasSpectrumFloor = double.IsFinite(spectrumFloorDbm) && spectrumFloorDbm > -250.0;
        bool hasSignalMeter = double.IsFinite(signalDbm) && signalDbm > -250.0;
        if (!hasSpectrumFloor && !hasSignalMeter) return false;

        // Paused longer than the analysis window (TX, just-armed, RX dropout):
        // the window may hold stale samples — clear before re-accumulating.
        if (_lastTickMs != long.MinValue && nowMs - _lastTickMs > _windowSamples * 500)
            ResetWindow();

        // Fast-attack: a band-scale VFO move makes the old band's samples
        // meaningless — drop the window and re-seed to the new band's floor.
        if (_lastVfoHz != long.MinValue && Math.Abs(vfoHz - _lastVfoHz) > _fastAttackVfoDeltaHz)
            ResetWindow();
        _lastVfoHz = vfoHz;

        if (_lastTickMs != long.MinValue && nowMs - _lastTickMs < 500)
            return false;
        _lastTickMs = nowMs;

        // Floor source for this tick: a real spectrum floor always wins; a
        // BRIEF spectrum dropout holds the window; only a SUSTAINED outage
        // falls back to the S-meter. Sources are never mixed in one window.
        if (hasSpectrumFloor) _lastSpectrumFloorMs = nowMs;
        bool spectrumRecent = _lastSpectrumFloorMs != long.MinValue &&
                              nowMs - _lastSpectrumFloorMs < _spectrumStaleMs;
        int floorSource;        // 0 hold, 1 spectrum, 2 S-meter fallback
        double floorSample;
        if (hasSpectrumFloor) { floorSource = 1; floorSample = spectrumFloorDbm; }
        else if (spectrumRecent) { floorSource = 0; floorSample = 0.0; }
        else if (hasSignalMeter) { floorSource = 2; floorSample = signalDbm; }
        else { floorSource = 0; floorSample = 0.0; }
        if (floorSource != 0)
        {
            if (_windowSource != 0 && _windowSource != floorSource)
                ResetWindow();
            _windowSource = floorSource;
            _window[_windowIdx] = floorSample;
            _windowIdx = (_windowIdx + 1) % _window.Length;
            if (_windowFill < _window.Length) _windowFill++;
        }

        double desiredOffset = OffsetDb;
        if (_windowFill >= _minSamples)
        {
            // Robust floor: a low percentile of the window rejects transient
            // signal energy. stackalloc keeps the 500 ms loop allocation-free.
            Span<double> sorted = stackalloc double[_windowFill];
            for (int i = 0; i < _windowFill; i++) sorted[i] = _window[i];
            sorted.Sort();
            int floorIndex = Math.Clamp(
                (int)Math.Round((sorted.Length - 1) * _percentile), 0, sorted.Length - 1);
            noiseFloorDbm = sorted[floorIndex];
            // Seat the AGC knee at the settled floor; the offset carries the
            // resulting max-gain relative to the operator baseline.
            desiredOffset = topFromFloor(noiseFloorDbm) - baselineTopDb;
        }

        double delta = desiredOffset - OffsetDb;
        // Deadband: ignore sub-threshold wobble. Above it, JUMP to the target —
        // the floor window is the smoother.
        if (Math.Abs(delta) < _deadbandDb) return false;
        OffsetDb = desiredOffset;
        return true;
    }
}
