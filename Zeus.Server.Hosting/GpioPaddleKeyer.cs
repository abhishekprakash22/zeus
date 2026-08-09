// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// GpioPaddleKeyer — a paddle plugged into the COMPUTER, piHPSDR-style.
//
// The iambic state machine is a C# port of the logic lineage documented in
// piHPSDR's src/iambic.c: Phil Harman VK6PH's Hermes iambic.v (2014),
// adapted to C by Rick Koch N1GP (2016), overhauled by Christoph van
// Wüllen DL1YCF (2018) — GPL like us. The essential design survives intact:
// dot/dash MEMORY is set only in the GPIO event handler; dot/dash HELD is
// sampled at the start of the opposite element; Mode A clears HELD (not
// MEMORY) when both paddles release — which is exactly the difference the
// ear knows as Mode A vs Mode B on letters like C.
//
// Where piHPSDR then keys the radio over the protocol, Zeus has something
// better in-house: CwEngine's host-CW path synthesizes the keyed carrier
// itself (raised-cosine envelope, phase-continuous IQ into TxIqRing, radio
// in SetHostCwKeying mode). This keyer drives that same machinery in real
// time: the state machine sets a key flag; a streaming synth thread follows
// it with a 5 ms envelope slew, so element edges are click-free regardless
// of GPIO timing jitter. Sidetone rides CwSidetoneSource exactly as CWX
// sends do — the radio-side audio path, not a GPIO buzzer.
//
// Wiring (BCM numbering, defaults): DOT -> GPIO23, DASH -> GPIO24, common
// -> GND. Internal pull-ups, contacts active-low. A straight key goes
// across either contact with KeyerMode=Straight.
//
// Discipline: disabled by default (GPIO probing must be opted into),
// session claims MoxSource.Cwx and releases only its own claim after the
// hang time; the UI MOX stays master override (TxActiveChanged drop).

using System.Device.Gpio;
using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server;

public sealed class GpioPaddleKeyer : IDisposable
{
    private const int SampleRateHz = CwEngine.SampleRateHz;
    private const int ChunkSamples = 480;          // 10 ms
    private const int RampMs = 5;
    private const int HangMs = 800;                // TX hold after last element

    private readonly TxService _tx;
    private readonly RadioService _radio;
    private readonly TxIqRing _ring;
    private readonly CwSettingsStore _settings;
    private readonly CwSidetoneSource? _sidetone;
    private readonly ILogger<GpioPaddleKeyer> _log;

    private readonly object _lock = new();
    private GpioController? _gpio;
    private int _dotPin, _dashPin;
    private bool _swap;
    private Thread? _keyerThread;
    private Thread? _synthThread;
    private CancellationTokenSource? _cts;
    private readonly ManualResetEventSlim _wake = new(false);

    // Paddle state (GPIO callback is the only writer of the memories —
    // the iambic.c contract).
    private volatile bool _dotClosed, _dashClosed;
    private volatile bool _dotMemory, _dashMemory;

    // Key line the synth follows.
    private volatile bool _keyDown;
    private long _lastElementAtMs;
    private volatile bool _sessionActive;

    public string? LastError { get; private set; }
    public bool Running => _cts is not null;

    public GpioPaddleKeyer(
        TxService tx, RadioService radio, TxIqRing ring,
        CwSettingsStore settings, ILogger<GpioPaddleKeyer> log,
        CwSidetoneSource? sidetone = null)
    {
        _tx = tx;
        _radio = radio;
        _ring = ring;
        _settings = settings;
        _sidetone = sidetone;
        _log = log;
    }

    /// <summary>Bring the keyer up or down to match saved settings. Safe to
    /// call repeatedly; reconfigures pins when they changed.</summary>
    public void Apply()
    {
        var s = _settings.Get();
        lock (_lock)
        {
            bool want = s.PaddleGpioEnabled && OperatingSystem.IsLinux();
            if (!want) { StopLocked(); return; }
            if (Running && _dotPin == s.PaddleDotPin && _dashPin == s.PaddleDashPin && _swap == s.PaddleSwap)
                return;
            StopLocked();
            try
            {
                _gpio = new GpioController();
                _dotPin = s.PaddleDotPin;
                _dashPin = s.PaddleDashPin;
                _swap = s.PaddleSwap;
                foreach (int pin in new[] { _dotPin, _dashPin })
                {
                    _gpio.OpenPin(pin, PinMode.InputPullUp);
                    _gpio.RegisterCallbackForPinValueChangedEvent(
                        pin, PinEventTypes.Falling | PinEventTypes.Rising, OnPinEvent);
                }
                _cts = new CancellationTokenSource();
                _keyerThread = new Thread(() => KeyerLoop(_cts.Token)) { IsBackground = true, Name = "paddle-keyer" };
                _synthThread = new Thread(() => SynthLoop(_cts.Token)) { IsBackground = true, Name = "paddle-synth" };
                _keyerThread.Start();
                _synthThread.Start();
                LastError = null;
                _log.LogInformation("paddle keyer up: dot=GPIO{Dot} dash=GPIO{Dash} swap={Swap}", _dotPin, _dashPin, _swap);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;   // e.g. no GPIO chip on this host
                _log.LogWarning("paddle keyer unavailable: {Err}", ex.Message);
                StopLocked();
            }
        }
    }

    private void StopLocked()
    {
        _cts?.Cancel();
        _wake.Set();
        _keyerThread = null;
        _synthThread = null;
        _cts = null;
        try { _gpio?.Dispose(); } catch { }
        _gpio = null;
    }

    private void OnPinEvent(object sender, PinValueChangedEventArgs e)
    {
        bool closed = e.ChangeType == PinEventTypes.Falling;   // active-low
        bool isDotContact = e.PinNumber == (_swap ? _dashPin : _dotPin);
        if (isDotContact)
        {
            _dotClosed = closed;
            if (closed) _dotMemory = true;   // the ONLY writer — iambic.c contract
        }
        else
        {
            _dashClosed = closed;
            if (closed) _dashMemory = true;
        }
        _wake.Set();
    }

    // ---- the iambic machine ------------------------------------------------

    private void KeyerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _wake.Wait(100);
            _wake.Reset();
            if (ct.IsCancellationRequested) break;

            var s = _settings.Get();
            int dotMs = Math.Max(20, 1200 / Math.Clamp(s.Wpm, 1, 60));

            if (s.KeyerMode == CwKeyerMode.Straight)
            {
                bool down = _dotClosed || _dashClosed;
                if (down && !_sessionActive && !StartSession()) { _dotMemory = _dashMemory = false; continue; }
                SetKey(down);
                _dotMemory = _dashMemory = false;
                if (!down) MaybeEndSession();
                continue;
            }

            // Iambic A/B, faithful to the ported design.
            bool dotHeld = false, dashHeld = false;
            while (!ct.IsCancellationRequested)
            {
                bool wantDot = _dotMemory || _dotClosed || dotHeld;
                bool wantDash = _dashMemory || _dashClosed || dashHeld;
                if (!wantDot && !wantDash) break;

                if (wantDot)
                {
                    _dotMemory = false;
                    dashHeld = _dashClosed;            // sampled at element start
                    _dashMemory = false;               // consumed into held
                    if (!SendElement(dotMs, dotMs, ct)) return;
                    dashHeld |= _dashMemory;           // squeezed during the dot
                }
                wantDash = _dashMemory || _dashClosed || dashHeld;
                if (wantDash && !ct.IsCancellationRequested)
                {
                    _dashMemory = false;
                    dotHeld = _dotClosed;
                    _dotMemory = false;
                    if (!SendElement(3 * dotMs, dotMs, ct)) return;
                    dotHeld |= _dotMemory;
                }
                if (s.KeyerMode == CwKeyerMode.IambicA && !_dotClosed && !_dashClosed)
                {
                    // Mode A: releasing both clears the HELD state (memories
                    // survive) — the element in progress completed above and
                    // nothing extra is sent.
                    dotHeld = dashHeld = false;
                }
            }
            MaybeEndSession();
        }
        SetKey(false);
        EndSessionNow();
    }

    private bool SendElement(int downMs, int gapMs, CancellationToken ct)
    {
        if (!_sessionActive && !StartSession()) return true;   // stay alive, drop element
        SetKey(true);
        PreciseWait(downMs, ct);
        SetKey(false);
        _lastElementAtMs = Environment.TickCount64;
        PreciseWait(gapMs, ct);
        return !ct.IsCancellationRequested;
    }

    private static void PreciseWait(int ms, CancellationToken ct)
    {
        long due = Environment.TickCount64 + ms;
        while (!ct.IsCancellationRequested)
        {
            long left = due - Environment.TickCount64;
            if (left <= 0) return;
            Thread.Sleep(left > 3 ? (int)(left - 2) : 0);
        }
    }

    private void SetKey(bool down)
    {
        if (_keyDown == down) return;
        _keyDown = down;
        if (_sidetone is not null)
        {
            if (down) _sidetone.Down();
            else _sidetone.Up();
        }
    }

    // ---- TX session (Cwx claim + host-CW mode + hang) ----------------------

    private bool StartSession()
    {
        if (!_tx.TrySetMox(true, MoxSource.Cwx, out var err))
        {
            LastError = err ?? "MOX refused";
            return false;
        }
        _radio.AlignLoForCwTx();
        _radio.SetHostCwKeying(true);
        _sessionActive = true;
        _lastElementAtMs = Environment.TickCount64;
        return true;
    }

    private void MaybeEndSession()
    {
        if (!_sessionActive) return;
        if (Environment.TickCount64 - _lastElementAtMs < HangMs)
        {
            _wake.Set();   // re-run soon to re-check the hang window
            Thread.Sleep(25);
            if (_dotClosed || _dashClosed || _dotMemory || _dashMemory) return;
            if (Environment.TickCount64 - _lastElementAtMs < HangMs) return;
        }
        EndSessionNow();
    }

    private void EndSessionNow()
    {
        if (!_sessionActive) return;
        _sessionActive = false;
        _radio.SetHostCwKeying(false);
        if (_tx.MoxOwner == MoxSource.Cwx)
            _tx.TrySetMox(false, MoxSource.Cwx, out _);
    }

    // ---- streaming synth: the key line, made into clean RF -----------------

    private void SynthLoop(CancellationToken ct)
    {
        var iq = new float[2 * ChunkSamples];
        double phase = 0;
        double env = 0;
        double slew = 1.0 / (RampMs * SampleRateHz / 1000.0);
        long produced = 0;
        long t0 = Environment.TickCount64;
        while (!ct.IsCancellationRequested)
        {
            if (!_sessionActive)
            {
                env = 0;
                produced = 0;
                t0 = Environment.TickCount64;
                Thread.Sleep(5);
                continue;
            }
            var s = _settings.Get();
            double phaseStep = 2.0 * Math.PI * s.SidetoneHz / SampleRateHz;
            double target = _keyDown ? 1.0 : 0.0;
            for (int i = 0; i < ChunkSamples; i++)
            {
                env += Math.Clamp(target - env, -slew, slew);
                iq[2 * i] = (float)(env * Math.Cos(phase));
                iq[2 * i + 1] = (float)(env * Math.Sin(phase));
                phase += phaseStep;
                if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
            }
            _ring.Write(iq);
            produced += ChunkSamples;
            long dueMs = t0 + produced * 1000 / SampleRateHz;
            long wait = dueMs - Environment.TickCount64;
            if (wait > 0) Thread.Sleep((int)Math.Min(wait, 20));
        }
    }

    public void Dispose()
    {
        lock (_lock) StopLocked();
        EndSessionNow();
        _wake.Dispose();
    }
}
