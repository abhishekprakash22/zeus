// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// WsprService — WSPR IN CORE, completing the Digital suite (same story as
// FT8/FT4: the plugin registry died, the seams shipped, the mode did not).
//
// RX: taps DspPipelineService.RxAudioAvailable on the audio thread through an
// allocation-free ÷4 decimator into a 12 kHz ring; a slot watcher wakes just
// after each even-UTC 120 s boundary, snapshots exactly one slot, and a
// worker converts it to the decoder contract (mix 1500 Hz → ÷32 → 375 Hz
// complex baseband, 45000 samples) and calls the vendored K9AN wsprd via
// WsprNative. Spots publish as the `wsprspot` SSE batch the frontend store
// already speaks. Slot alignment follows the FT8 pipeline's hard rule: hand
// the decoder EXACTLY the slot that ended, computed from the digital clock —
// "close enough" slot maths is not close enough.
//
// TX: an autonomous beacon (the mode has no QSO turn-taking). Once armed it
// owns itself: each even 120 s boundary it rolls txPercent, and on a hit
// keys MOX (MoxSource.WsprBeacon), streams the 162-symbol 4-FSK waveform
// (110.6 s, tone spacing 12000/8192 = 1.4648 Hz, WSJT-X-nominal 1 s in-slot
// start) through the same TxAudioIngest path as the FT8 keyer, and unkeys.
// Channel symbols come from the NATIVE encoder (bit-exact WSJT-X packing) —
// no hand-ported bit twiddling. Safety: HALT/disarm aborts mid-signal within
// one audio block; a per-transmission watchdog caps overruns; a 30-minute
// auto-disarm bounds an abandoned beacon (the frontend's pagehide disarm is
// the first line, this is the backstop it documents).

using System.Buffers.Binary;
using Zeus.Contracts;

namespace Zeus.Server.Hosting.Digital;

public sealed record WsprSpotDtoOut(
    double SnrDb, double DtSec, double FreqMhz, double DriftHz, string Message);

public sealed record WsprSpotBatch(
    int Receiver, long SlotStartUnixMs, double DialFreqMhz, WsprSpotDtoOut[] Spots);

public sealed class WsprService : IHostedService, IDisposable
{
    public const int SlotMs = 120_000;
    private const int RxRateHz = 12_000;
    private const int SlotSamples12k = RxRateHz * (SlotMs / 1000);      // 1_440_000
    private const int RingLen = 1 << 22;                                 // 4.19 M ≈ 2.9 slots
    private const int TxStartDelayMs = 1_000;                            // WSJT-X nominal
    private const int WatchdogSlackMs = 3_000;
    private const int ArmWatchdogMs = 30 * 60_000;
    private const float Amplitude = 0.90f;

    private readonly DspPipelineService _pipeline;
    private readonly DigitalService _digital;
    private readonly TxAudioIngest _ingest;
    private readonly TxService _tx;
    private readonly ILogger<WsprService> _log;

    // ---- RX state -----------------------------------------------------------
    private readonly object _rxLock = new();
    private readonly float[] _ring = new float[RingLen];
    private long _ringWrite;                       // total 12 kHz samples written
    private readonly Decim4 _decim = new();
    private volatile bool _enabled;
    private int _receiver;
    private double _dialFreqMhz = 14.0956;
    private long _currentSlot = -1;
    private Thread? _slotThread;
    private CancellationTokenSource? _cts;

    // ---- TX (beacon) state --------------------------------------------------
    private volatile bool _armed;
    private long _armedAtMs;
    private volatile bool _transmitting;
    private string _call = "";
    private string _grid4 = "";
    private int _dBm = 30;
    private int _audioHz = 1500;
    private double _txPercent = 0.20;
    private Thread? _beaconThread;
    private readonly Random _rng = new();
    private readonly float[] _block = new float[TxMicBlockResampler.OutputBlockSamples];
    private readonly byte[] _payload = new byte[TxMicBlockResampler.OutputBlockSamples * sizeof(float)];

    public WsprService(
        DspPipelineService pipeline, DigitalService digital,
        TxAudioIngest ingest, TxService tx, ILogger<WsprService> log)
    {
        _pipeline = pipeline;
        _digital = digital;
        _ingest = ingest;
        _tx = tx;
        _log = log;
    }

    public bool NativeAvailable => WsprNative.Available;
    public bool Enabled => _enabled;
    public bool Armed => _armed;
    public bool Transmitting => _transmitting;

    public object StatusDto() => new
    {
        enabled = _enabled,
        nativeAvailable = NativeAvailable,
        receiver = _receiver,
        dialFreqMhz = _dialFreqMhz,
        armed = _armed,
        transmitting = _transmitting,
        call = _call,
        grid4 = _grid4,
        dBm = _dBm,
        audioHz = _audioHz,
        txPercent = _txPercent,
    };

    // ---- control ------------------------------------------------------------

    public bool Enable(int receiver, double dialFreqMhz)
    {
        if (!NativeAvailable) return false;
        lock (_rxLock)
        {
            _receiver = receiver;
            _dialFreqMhz = dialFreqMhz;
            _ringWrite = 0;
            _decim.Reset();
            _currentSlot = -1;
            _enabled = true;
        }
        _log.LogInformation("wspr: RX enabled (rx={Rx}, dial={Dial} MHz)", receiver, dialFreqMhz);
        return true;
    }

    public void Disable()
    {
        _enabled = false;
        _log.LogInformation("wspr: RX disabled");
    }

    public void TxSettings(string? call, string? grid4, int? dBm, int? audioHz, double? txPercent)
    {
        if (call is not null) _call = call.Trim().ToUpperInvariant();
        if (grid4 is not null) _grid4 = grid4.Trim().ToUpperInvariant();
        if (dBm is int p) _dBm = Math.Clamp(p, 0, 60);
        if (audioHz is int hz) _audioHz = Math.Clamp(hz, 1400, 1600);
        if (txPercent is double pct) _txPercent = Math.Clamp(pct, 0.0, 1.0);
        PublishTxStatus();
    }

    public bool Arm(bool enabled)
    {
        if (enabled && (!NativeAvailable || _call.Length == 0 || _grid4.Length < 4))
            return false;
        _armed = enabled;
        if (enabled) Interlocked.Exchange(ref _armedAtMs, (long)_digital.Clock.UtcNowMs);
        _log.LogInformation("wspr beacon: {State}", enabled ? "ARMED" : "disarmed");
        PublishTxStatus();
        return _armed == enabled;
    }

    // ---- lifecycle ----------------------------------------------------------

    public Task StartAsync(CancellationToken ct)
    {
        _cts = new CancellationTokenSource();
        _pipeline.RxAudioAvailable += OnRxAudio;
        _slotThread = new Thread(() => SlotLoop(_cts.Token))
        { IsBackground = true, Name = "wspr-slots" };
        _beaconThread = new Thread(() => BeaconLoop(_cts.Token))
        { IsBackground = true, Name = "wspr-beacon" };
        _slotThread.Start();
        _beaconThread.Start();
        _log.LogInformation("wspr: in core (native={Native})", NativeAvailable);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _armed = false;
        _enabled = false;
        _pipeline.RxAudioAvailable -= OnRxAudio;
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _cts?.Cancel();

    // ---- RX path ------------------------------------------------------------

    /// <summary>RX AUDIO THREAD — no allocation, no locks held long, no throw.
    /// ÷4 decimate into the 12 kHz ring. (Same discipline as DigitalService.)</summary>
    private void OnRxAudio(int receiver, int sampleRateHz, ReadOnlyMemory<float> samples)
    {
        if (!_enabled || receiver != _receiver || sampleRateHz != 48_000) return;
        if (!Monitor.TryEnter(_rxLock)) return;    // snapshot in progress → drop one block
        try
        {
            var span = samples.Span;
            long w = _ringWrite;
            int produced = _decim.Process(span, _ring, w, RingLen);
            _ringWrite = w + produced;
        }
        finally { Monitor.Exit(_rxLock); }
    }

    private void SlotLoop(CancellationToken ct)
    {
        var spots = new ZeusWsprSpot[64];
        while (!ct.IsCancellationRequested)
        {
            Thread.Sleep(200);
            if (!_enabled || !NativeAvailable) continue;

            double now = _digital.Clock.UtcNowMs;
            long slot = (long)Math.Floor(now / SlotMs);
            if (slot == _currentSlot) continue;
            long ended = slot - 1;
            bool first = _currentSlot < 0;
            _currentSlot = slot;
            if (first) continue;                    // partial slot — never decode it

            // Snapshot EXACTLY the ended slot: the ring index corresponding to
            // the boundary is (total written) − (ms since boundary)·rate.
            float[] slotAudio;
            double dialMhz;
            int receiver;
            lock (_rxLock)
            {
                double msSince = now - slot * (double)SlotMs;
                long end = _ringWrite - (long)(msSince * RxRateHz / 1000.0);
                long start = end - SlotSamples12k;
                if (start < 0 || end > _ringWrite || _ringWrite - start > RingLen)
                    continue;                       // ring not full / overwritten
                slotAudio = new float[SlotSamples12k];
                for (long i = 0; i < SlotSamples12k; i++)
                    slotAudio[i] = _ring[(start + i) & (RingLen - 1)];
                dialMhz = _dialFreqMhz;
                receiver = _receiver;
            }

            try
            {
                var batch = DecodeSlot(slotAudio, dialMhz, receiver, ended, spots);
                _digital.Events.PublishWsprSpot(batch);
                if (batch.Spots.Length > 0)
                    _log.LogInformation("wspr: slot {Slot} → {N} spot(s)", ended, batch.Spots.Length);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "wspr: decode failed for slot {Slot}", ended);
            }
        }
    }

    private unsafe WsprSpotBatch DecodeSlot(
        float[] audio12k, double dialMhz, int receiver, long slot, ZeusWsprSpot[] spots)
    {
        // 12 kHz real → 375 Hz complex baseband (WSPR window centre 1500 Hz
        // → 0 Hz), the vendored decoder's input contract. Worker thread —
        // allocation is fine here.
        var (idat, qdat) = MixAndDecimate32(audio12k);
        int n;
        fixed (float* pi = idat)
        fixed (float* pq = qdat)
        fixed (ZeusWsprSpot* ps = spots)
            n = WsprNative.Decode(pi, pq, idat.Length,
                (int)Math.Round(dialMhz * 1e6), ps, spots.Length);

        var outSpots = new WsprSpotDtoOut[Math.Max(0, n)];
        for (int i = 0; i < outSpots.Length; i++)
        {
            string msg;
            unsafe { fixed (byte* pm = spots[i].Message)
                msg = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)pm) ?? ""; }
            outSpots[i] = new WsprSpotDtoOut(
                Math.Round(spots[i].SnrDb, 1), Math.Round(spots[i].DtSec, 2),
                spots[i].FreqHz, Math.Round(spots[i].DriftHz, 2), msg);
        }
        return new WsprSpotBatch(receiver, slot * (long)SlotMs, dialMhz, outSpots);
    }

    /// <summary>12 kHz real → 375 Hz complex: mix by −1500 Hz, 1024-tap
    /// Hamming-sinc lowpass (fc 160 Hz), decimate ÷32.</summary>
    internal static (float[] I, float[] Q) MixAndDecimate32(float[] x)
    {
        int n = x.Length;
        var bi = new double[n];
        var bq = new double[n];
        double w = 2 * Math.PI * 1500.0 / RxRateHz;
        for (int i = 0; i < n; i++)
        {
            bi[i] = x[i] * Math.Cos(w * i);
            bq[i] = -x[i] * Math.Sin(w * i);
        }
        const int taps = 1024;
        var h = new double[taps];
        double fc = 160.0 / RxRateHz, sum = 0;
        for (int i = 0; i < taps; i++)
        {
            double m = i - (taps - 1) / 2.0;
            double sinc = m == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * m) / (Math.PI * m);
            double win = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (taps - 1));
            h[i] = sinc * win; sum += h[i];
        }
        for (int i = 0; i < taps; i++) h[i] /= sum;

        int outLen = Math.Min(WsprNative.SlotSamples, n / 32);
        var I = new float[outLen];
        var Q = new float[outLen];
        for (int o = 0; o < outLen; o++)
        {
            int c = o * 32;
            double ai = 0, aq = 0;
            int lo = Math.Max(0, c - taps + 1);
            for (int k = lo; k <= c && k < n; k++)
            {
                double hh = h[c - k];
                ai += bi[k] * hh; aq += bq[k] * hh;
            }
            I[o] = (float)ai; Q[o] = (float)aq;
        }
        return (I, Q);
    }

    // ---- TX beacon ----------------------------------------------------------

    private void BeaconLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Thread.Sleep(50);
            if (!_armed) continue;

            // 30-minute abandoned-beacon backstop.
            if (_digital.Clock.UtcNowMs - Interlocked.Read(ref _armedAtMs) > ArmWatchdogMs)
            {
                _log.LogWarning("wspr beacon: 30-minute watchdog — auto-disarm");
                Arm(false);
                continue;
            }

            double now = _digital.Clock.UtcNowMs;
            long slot = (long)Math.Floor(now / SlotMs) + 1;      // next boundary
            double boundary = slot * (double)SlotMs;
            if (boundary - now > 500) continue;                  // wake near boundary

            bool txThisSlot = _rng.NextDouble() < _txPercent;
            if (!WaitUntil(boundary, ct)) continue;
            if (!_armed) continue;
            if (!txThisSlot) { Thread.Sleep(1000); continue; }

            float[]? wave = BuildWaveform();
            if (wave is null) { _log.LogWarning("wspr beacon: encode failed — disarming"); Arm(false); continue; }
            Transmit(wave, ct);
        }
    }

    private float[]? BuildWaveform()
    {
        Span<byte> sym = stackalloc byte[WsprNative.SymbolCount];
        if (!WsprNative.Encode($"{_call} {_grid4} {_dBm}", sym)) return null;

        // 48 kHz: 32768 samples/symbol (256/375 s exactly), spacing 1.4648 Hz.
        const int rate = 48_000;
        const int spSym = 32_768;
        var wave = new float[WsprNative.SymbolCount * spSym];
        double phase = 0, spacing = 12_000.0 / 8_192.0;
        for (int s = 0; s < WsprNative.SymbolCount; s++)
        {
            double f = _audioHz + (sym[s] - 1.5) * spacing;
            double dp = 2 * Math.PI * f / rate;
            int b = s * spSym;
            for (int i = 0; i < spSym; i++)
            {
                phase += dp;
                wave[b + i] = (float)Math.Sin(phase);
            }
        }
        return wave;
    }

    /// <summary>Key MOX, stream paced in real time, unkey — the FT8 keyer's
    /// exact pattern (SignalJammer block pump), separate MOX source.</summary>
    private void Transmit(float[] wave, CancellationToken ct)
    {
        if (!_tx.TrySetMox(true, MoxSource.WsprBeacon, out var err))
        {
            _log.LogWarning("wspr beacon: MOX refused: {Err}", err ?? "unknown");
            return;
        }
        _transmitting = true;
        PublishTxStatus();
        _log.LogInformation("wspr beacon: TX '{Call} {Grid} {Pwr}' @{Hz} Hz",
            _call, _grid4, _dBm, _audioHz);
        try
        {
            if (!ct.WaitHandle.WaitOne(TxStartDelayMs) && _armed)
                Pump(wave, ct);
        }
        finally
        {
            if (_tx.MoxOwner == MoxSource.WsprBeacon)
            {
                try
                {
                    Thread.Sleep(60);
                    _tx.TrySetMox(false, MoxSource.WsprBeacon, out _);
                }
                catch (Exception ex) { _log.LogWarning(ex, "wspr beacon: MOX release failed"); }
            }
            _transmitting = false;
            PublishTxStatus();
        }
    }

    private void Pump(float[] wave, CancellationToken ct)
    {
        int rate = TxMicBlockResampler.OutputSampleRate;
        int blockSamples = TxMicBlockResampler.OutputBlockSamples;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long periodTicks = (long)(System.Diagnostics.Stopwatch.Frequency * blockSamples / (double)rate);
        long deadline = clock.ElapsedTicks;
        long watchdog = (long)(System.Diagnostics.Stopwatch.Frequency
            * ((wave.Length / (double)rate) + WatchdogSlackMs / 1000.0));

        int offset = 0;
        while (offset < wave.Length)
        {
            if (ct.IsCancellationRequested || !_armed)
            {
                if (!_armed)
                    _log.LogInformation("wspr beacon: halted mid-signal at {Sec:F1} s", offset / (double)rate);
                return;
            }
            if (clock.ElapsedTicks > watchdog)
            {
                _log.LogError("wspr beacon: WATCHDOG — transmission overran, aborting");
                return;
            }

            int take = Math.Min(blockSamples, wave.Length - offset);
            for (int i = 0; i < take; i++)
            {
                float s = wave[offset + i] * Amplitude;
                _block[i] = float.IsFinite(s) ? Math.Clamp(s, -0.95f, 0.95f) : 0f;
            }
            for (int i = take; i < blockSamples; i++) _block[i] = 0f;
            for (int i = 0; i < blockSamples; i++)
                BinaryPrimitives.WriteSingleLittleEndian(
                    _payload.AsSpan(i * sizeof(float), sizeof(float)), _block[i]);
            _ingest.OnMicPcmBytesFromWav(new ReadOnlyMemory<byte>(_payload, 0, _payload.Length));

            offset += take;
            deadline += periodTicks;
            long remain = deadline - clock.ElapsedTicks;
            if (remain <= 0)
            {
                if (-remain > periodTicks * 8) deadline = clock.ElapsedTicks;
                continue;
            }
            int delayMs = (int)(remain * 1000 / System.Diagnostics.Stopwatch.Frequency);
            if (delayMs > 0 && ct.WaitHandle.WaitOne(delayMs)) return;
        }
    }

    private bool WaitUntil(double utcMs, CancellationToken ct)
    {
        while (true)
        {
            double remain = utcMs - _digital.Clock.UtcNowMs;
            if (remain <= 0) return true;
            if (!_armed) return false;
            if (ct.WaitHandle.WaitOne(remain > 50 ? 10 : 2)) return false;
        }
    }

    private void PublishTxStatus() =>
        _digital.Events.PublishTxStatus(new Ft8TxStatus
        {
            Armed = _armed,
            Transmitting = _transmitting,
            Mode = "WSPR",
            Message = _call.Length > 0 ? $"{_call} {_grid4} {_dBm}" : null,
            AudioHz = _audioHz,
            NativeAvailable = NativeAvailable,
        });

    /// <summary>÷4 decimator, 48 → 12 kHz. Allocation-free; writes directly
    /// into the caller's power-of-two ring at a running index.</summary>
    private sealed class Decim4
    {
        private const int Taps = 48;
        private readonly float[] _h = BuildTaps();
        private readonly float[] _delay = new float[Taps];
        private int _pos, _phase;

        public int Process(ReadOnlySpan<float> in48k, float[] ring, long writeIndex, int ringLen)
        {
            int produced = 0;
            for (int i = 0; i < in48k.Length; i++)
            {
                _delay[_pos] = in48k[i];
                _pos = _pos + 1 == Taps ? 0 : _pos + 1;
                if (++_phase == 4)
                {
                    _phase = 0;
                    float acc = 0f;
                    int idx = _pos;
                    for (int t = Taps - 1; t >= 0; t--)
                    {
                        acc += _delay[idx] * _h[t];
                        idx = idx + 1 == Taps ? 0 : idx + 1;
                    }
                    ring[(writeIndex + produced) & (ringLen - 1)] = acc;
                    produced++;
                }
            }
            return produced;
        }

        public void Reset() { Array.Clear(_delay); _pos = 0; _phase = 0; }

        private static float[] BuildTaps()
        {
            var h = new double[Taps];
            double fc = 5_000.0 / 48_000.0, sum = 0;
            for (int i = 0; i < Taps; i++)
            {
                double m = i - (Taps - 1) / 2.0;
                double sinc = m == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * m) / (Math.PI * m);
                double win = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (Taps - 1));
                h[i] = sinc * win; sum += h[i];
            }
            var f = new float[Taps];
            for (int i = 0; i < Taps; i++) f[i] = (float)(h[i] / sum);
            return f;
        }
    }
}
