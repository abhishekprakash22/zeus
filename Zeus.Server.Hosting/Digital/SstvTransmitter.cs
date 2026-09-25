// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// SstvTransmitter — sends ONE SSTV picture on explicit operator request.
//
// The picture (raw RGB at the mode's exact size, composed by the frontend) is
// encoded by the same SstvEncoder the RX tests round-trip against — VIS
// header, scan lines, optional FSK ID — then keyed and streamed exactly the
// way the WSPR beacon and FT8 keyer do it: MOX via TxService under its own
// source (MoxSource.Sstv), audio paced in real time into TxAudioIngest's mic
// path, so every TX interlock, ALC and the operator's drive setting apply
// unchanged. Nothing here touches drive, PA or PureSignal.
//
// SAFETY
//   * Never keys on its own: one Start() = one picture, then it unkeys.
//   * Refuses when MOX is already on (someone else owns the transmitter) or
//     the receiver isn't on SSB/FM.
//   * HALT, or the operator taking MOX away (UI is the master override), ends
//     the picture within one 20 ms audio block.
//   * A watchdog bounds the transmission at its nominal length + 3 s.
//   * SSTV is 100 % duty cycle for up to ~5 min (PD 290): the frontend shows
//     the duration before sending; the radio's own TX timeout still applies.

using System.Buffers.Binary;
using Zeus.Contracts;
using Zeus.Server.Hosting.Digital.Sstv;

namespace Zeus.Server.Hosting.Digital;

public sealed record SstvTxRequest(string Mode, string Rgb, string? FskId);

public sealed record SstvTxStatus(
    bool Transmitting, string? Mode, double Progress, long? StartedUnixMs,
    double DurationMs, string? LastError);

public sealed class SstvTransmitter : IDisposable
{
    private const float Amplitude = 0.90f;
    private const int StartDelayMs = 300;               // let T/R relays settle
    private const int WatchdogSlackMs = 3_000;
    private const int ProgressEveryBlocks = 50;         // ≈ 1 s

    /// <summary>Receiver modes an SSTV picture can go out on.</summary>
    private static readonly RxMode[] TxModes = [RxMode.USB, RxMode.LSB, RxMode.DIGU, RxMode.DIGL, RxMode.FM];

    private readonly TxAudioIngest _ingest;
    private readonly TxService _tx;
    private readonly DigitalService _digital;
    private readonly RadioService? _radio;
    private readonly ILogger<SstvTransmitter> _log;

    private readonly object _sync = new();
    private readonly float[] _block = new float[TxMicBlockResampler.OutputBlockSamples];
    private readonly byte[] _payload = new byte[TxMicBlockResampler.OutputBlockSamples * sizeof(float)];
    private volatile bool _busy;
    private volatile bool _halt;
    private string? _mode;
    private double _progress;
    private long? _startedUnixMs;
    private double _durationMs;
    private string? _lastError;

    public SstvTransmitter(
        TxAudioIngest ingest, TxService tx, DigitalService digital,
        ILogger<SstvTransmitter> log, RadioService? radio = null)
    {
        _ingest = ingest;
        _tx = tx;
        _digital = digital;
        _radio = radio;
        _log = log;
    }

    public bool Transmitting => _busy;

    public SstvTxStatus Status()
    {
        lock (_sync)
            return new SstvTxStatus(_busy, _mode, Math.Round(_progress, 3), _startedUnixMs,
                Math.Round(_durationMs), _lastError);
    }

    /// <summary>Validate, encode and start sending. Returns an error message
    /// (and sends nothing) when the request or the radio state is wrong.</summary>
    public string? Start(SstvTxRequest req)
    {
        var mode = SstvModes.ByName(req.Mode);
        if (mode is null) return $"unknown SSTV mode '{req.Mode}'";

        byte[] rgb;
        try { rgb = Convert.FromBase64String(req.Rgb); }
        catch (FormatException) { return "picture is not valid base64"; }
        if (rgb.Length != mode.Width * mode.Height * 3)
            return $"picture must be {mode.Width}x{mode.Height} RGB for {mode.Name}";

        if (_radio is not null)
        {
            var rxMode = _radio.Snapshot().Mode;
            if (!TxModes.Contains(rxMode))
                return $"SSTV goes out on USB/LSB/DIGU/DIGL/FM, not {rxMode}";
        }

        float[] wave = SstvEncoder.Encode(mode, rgb, TxMicBlockResampler.OutputSampleRate, 1f,
            fskId: req.FskId);

        lock (_sync)
        {
            if (_busy) return "already sending a picture";
            if (_tx.IsMoxOn) return "the transmitter is already keyed";
            _busy = true;
            _halt = false;
            _mode = mode.Name;
            _progress = 0;
            _durationMs = wave.Length * 1000.0 / TxMicBlockResampler.OutputSampleRate;
            _startedUnixMs = (long)_digital.Clock.UtcNowMs;
            _lastError = null;
        }
        new Thread(() => Run(wave)) { IsBackground = true, Name = "sstv-tx" }.Start();
        return null;
    }

    public void Halt()
    {
        if (!_busy) return;
        _halt = true;
        _log.LogInformation("sstv tx: HALT requested");
    }

    public void Dispose() => _halt = true;

    private void Run(float[] wave)
    {
        string? error = null;
        bool keyed = false;
        try
        {
            if (!_tx.TrySetMox(true, MoxSource.Sstv, out var moxErr))
            {
                error = $"MOX refused: {moxErr ?? "unknown"}";
                return;
            }
            keyed = true;
            _log.LogInformation("sstv tx: {Mode}, {Sec:F0} s", _mode, wave.Length / (double)TxMicBlockResampler.OutputSampleRate);
            Publish();

            // Relay settle, abortable.
            for (int waited = 0; waited < StartDelayMs && !_halt; waited += 20) Thread.Sleep(20);
            if (!_halt) error = Pump(wave);
        }
        catch (Exception ex)
        {
            error = "transmit failed";
            _log.LogError(ex, "sstv tx: failed");
        }
        finally
        {
            if (keyed && _tx.MoxOwner == MoxSource.Sstv)
            {
                try
                {
                    Thread.Sleep(60);                    // let the last block leave the ingest
                    _tx.TrySetMox(false, MoxSource.Sstv, out _);
                }
                catch (Exception ex) { _log.LogWarning(ex, "sstv tx: MOX release failed"); }
            }
            lock (_sync)
            {
                _busy = false;
                _lastError = error ?? (_halt ? "halted" : null);
            }
            if (error is not null) _log.LogWarning("sstv tx: {Err}", error);
            Publish();
        }
    }

    /// <summary>Stream the waveform in real time. Returns an error string if
    /// it stopped for any reason other than finishing or HALT.</summary>
    private string? Pump(float[] wave)
    {
        int rate = TxMicBlockResampler.OutputSampleRate;
        int blockSamples = _block.Length;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long periodTicks = (long)(System.Diagnostics.Stopwatch.Frequency * blockSamples / (double)rate);
        long deadline = clock.ElapsedTicks;
        long watchdog = (long)(System.Diagnostics.Stopwatch.Frequency
            * ((wave.Length / (double)rate) + WatchdogSlackMs / 1000.0));

        int offset = 0, blocks = 0;
        while (offset < wave.Length)
        {
            if (_halt) return null;
            if (_tx.MoxOwner != MoxSource.Sstv) return "MOX taken away — picture ended";
            if (clock.ElapsedTicks > watchdog) return "WATCHDOG — transmission overran";

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
            if (++blocks % ProgressEveryBlocks == 0)
            {
                lock (_sync) _progress = offset / (double)wave.Length;
                Publish();
            }

            deadline += periodTicks;
            long remain = deadline - clock.ElapsedTicks;
            if (remain <= 0)
            {
                if (-remain > periodTicks * 8) deadline = clock.ElapsedTicks;
                continue;
            }
            int delayMs = (int)(remain * 1000 / System.Diagnostics.Stopwatch.Frequency);
            if (delayMs > 0) Thread.Sleep(delayMs);
        }
        lock (_sync) _progress = 1;
        return null;
    }

    private void Publish() => _digital.Events.PublishSstv(new { kind = "tx", tx = Status() });
}
