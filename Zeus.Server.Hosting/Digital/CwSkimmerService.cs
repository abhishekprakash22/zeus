// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// DeepCW multi-channel skimmer (phase 2). The WSPR service's architecture,
// pointed at CW: tap the RX audio fan-out, resample to the model's 3200 Hz,
// detect concurrent CW carriers in the passband, isolate each with a narrow
// bandpass, and run the DeepCW neural decoder (e04/deepcw-engine,
// AGPL-3.0-or-later — see wwwroot/deepcw/NOTICE.txt) per channel on the Pi's
// CPU via ONNX Runtime. Decoded text streams as the `cwskim` SSE event; the
// frontend paints per-station callout lanes on the waterfall and maps each
// channel's audio pitch to an absolute frequency (it owns VFO/mode/CW-pitch).
//
// Preprocessing is the same contract the browser worker uses (Hann/256 STFT
// hop 48, 65 magnitude bins over 400-1200 Hz, log1p, [1,1,T,65] ->
// log_probs [1,T,42], greedy CTC, stable-prefix streaming with a volatile
// tail). Channels outside the model's 420-1180 Hz window are reported as
// active-but-undecodable rather than fed audio the net was never trained on.

using System.Numerics.Tensors;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Zeus.Server.Hosting.Digital;

public sealed class CwSkimmerService : IHostedService, IDisposable
{
    private const int ModelRateHz = 3200;
    private const int RingSeconds = 16;
    private const int RingLen = ModelRateHz * RingSeconds;
    private const int WindowSeconds = 12;
    private const double VolatileSeconds = 1.6;

    private const int DetectEveryMs = 1000;
    private const int DetectFft = 2048;               // 1.5625 Hz bins
    private const double DetectLowHz = 300;
    private const double DetectHighHz = 2700;
    private const double PeakOverFloorDb = 10.0;
    private const double HoldOverFloorDb = 6.0;
    private const double PeakMinSpacingHz = 60.0;
    private const int MaxChannels = 4;
    private const double ChannelMatchHz = 30.0;
    private const int ChannelHoldMs = 7000;

    private const int InferEveryMsPerChannel = 1600;
    private const int InferSchedulerMs = 400;
    private const double DecodeLowHz = 420;
    private const double DecodeHighHz = 1180;

    private readonly DspPipelineService _pipeline;
    private readonly DigitalService _digital;
    private readonly ILogger<CwSkimmerService> _log;

    private readonly object _lock = new();
    private readonly float[] _ring = new float[RingLen];
    private long _ringWrite;
    private double _resamplePos;
    private volatile bool _enabled;
    private int _receiver;
    private Thread? _worker;
    private CancellationTokenSource? _cts;

    // ---- model ---------------------------------------------------------------
    private InferenceSession? _session;
    private string[] _vocab = Array.Empty<string>();
    private int _blankIndex = 41;
    private int _fftLen = 256, _hopLen = 48, _freqBins = 65;
    private double _minFreqHz = 400;
    private float[]? _cos, _sin, _hann;    // twiddles for the 65 needed bins
    private string _modelPath = "";
    public bool ModelAvailable { get; private set; }

    private sealed class Channel
    {
        public int Id;
        public double PitchHz;
        public double SnrDb;
        public long LastSeenMs;
        public long LastInferMs;
        public string Emitted = "";
        public bool Active = true;
    }

    private readonly List<Channel> _channels = new();
    private int _nextChannelId = 1;

    public CwSkimmerService(
        DspPipelineService pipeline, DigitalService digital, ILogger<CwSkimmerService> log)
    {
        _pipeline = pipeline;
        _digital = digital;
        _log = log;
    }

    public bool Enabled => _enabled;

    public object StatusDto()
    {
        lock (_lock)
        {
            return new
            {
                enabled = _enabled,
                modelAvailable = ModelAvailable,
                modelPath = _modelPath,
                receiver = _receiver,
                channels = _channels.Where(c => c.Active).Select(ChannelDto).ToArray(),
            };
        }
    }

    private static object ChannelDto(Channel c) => new
    {
        id = c.Id,
        pitchHz = Math.Round(c.PitchHz, 1),
        snrDb = Math.Round(c.SnrDb, 1),
        active = c.Active,
        decodable = c.PitchHz >= DecodeLowHz && c.PitchHz <= DecodeHighHz,
    };

    // ---- control -------------------------------------------------------------

    public bool Enable(int receiver)
    {
        EnsureModel();
        lock (_lock)
        {
            if (_enabled) { _receiver = receiver; return true; }
            _receiver = receiver;
            _ringWrite = 0;
            Array.Clear(_ring);
            _channels.Clear();
            _enabled = true;
        }
        _pipeline.RxAudioAvailable += OnRxAudio;
        _cts = new CancellationTokenSource();
        _worker = new Thread(() => WorkerLoop(_cts.Token)) { IsBackground = true, Name = "cw-skim" };
        _worker.Start();
        _log.LogInformation("cw-skim enabled on RX{Rx}; model={Model}", receiver, ModelAvailable);
        PublishRoster();
        return true;
    }

    public void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        _pipeline.RxAudioAvailable -= OnRxAudio;
        _cts?.Cancel();
        _worker?.Join(1500);
        _worker = null;
        lock (_lock) _channels.Clear();
        _log.LogInformation("cw-skim disabled");
        PublishRoster();
    }

    // ---- audio ---------------------------------------------------------------

    // Anti-alias lowpass state: 4th-order Butterworth (two cascaded biquads,
    // streaming) at 1350 Hz, applied at the SOURCE rate before decimation.
    // Interpolating 48 kHz straight down to 3200 Hz folds 1.6-24 kHz into the
    // model band (2.0-2.8 kHz lands exactly inside 400-1200 Hz) — the same
    // aliasing bug the browser worker shipped with, fixed in both places.
    private int _lpfRate;
    private readonly double[] _lpfState = new double[8];
    private readonly double[][] _lpfCoef = new double[2][];

    private void DesignLpf(int rate)
    {
        _lpfRate = rate;
        Array.Clear(_lpfState);
        double[] qs = { 0.5411961, 1.3065630 };
        for (int i = 0; i < 2; i++)
        {
            double w0 = 2 * Math.PI * 1350.0 / rate;
            double alpha = Math.Sin(w0) / (2 * qs[i]);
            double cosW = Math.Cos(w0);
            double b0 = (1 - cosW) / 2, b1 = 1 - cosW, b2 = (1 - cosW) / 2;
            double a0 = 1 + alpha, a1 = -2 * cosW, a2 = 1 - alpha;
            _lpfCoef[i] = new[] { b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0 };
        }
    }

    private void LowpassInPlace(Span<float> x, int rate)
    {
        if (rate <= 3600) return;
        if (_lpfRate != rate) DesignLpf(rate);
        for (int stage = 0; stage < 2; stage++)
        {
            var c = _lpfCoef[stage]!;
            int o = stage * 4;
            double x1 = _lpfState[o], x2 = _lpfState[o + 1], y1 = _lpfState[o + 2], y2 = _lpfState[o + 3];
            for (int i = 0; i < x.Length; i++)
            {
                double xi = x[i];
                double yi = c[0] * xi + c[1] * x1 + c[2] * x2 - c[3] * y1 - c[4] * y2;
                x2 = x1; x1 = xi; y2 = y1; y1 = yi;
                x[i] = (float)yi;
            }
            _lpfState[o] = x1; _lpfState[o + 1] = x2; _lpfState[o + 2] = y1; _lpfState[o + 3] = y2;
        }
    }

    private readonly float[] _lpfScratch = new float[8192];

    private void OnRxAudio(int receiver, int sampleRateHz, ReadOnlyMemory<float> samples)
    {
        if (!_enabled || receiver != _receiver || sampleRateHz <= 0) return;
        var src = samples.Span;
        double step = (double)sampleRateHz / ModelRateHz;
        lock (_lock)
        {
            // Filter on a scratch copy (the source memory belongs to the
            // pipeline), preserving streaming filter state across blocks.
            Span<float> work = src.Length <= _lpfScratch.Length
                ? _lpfScratch.AsSpan(0, src.Length)
                : new float[src.Length];
            src.CopyTo(work);
            LowpassInPlace(work, sampleRateHz);

            double pos = _resamplePos;
            while (pos < work.Length - 1)
            {
                int i = (int)pos;
                double f = pos - i;
                _ring[(int)(_ringWrite % RingLen)] = (float)(work[i] * (1 - f) + work[i + 1] * f);
                _ringWrite++;
                pos += step;
            }
            _resamplePos = pos - work.Length;   // carry into the next block
        }
    }

    // ---- worker: detection + per-channel inference ---------------------------

    private void WorkerLoop(CancellationToken ct)
    {
        long lastDetect = 0;
        while (!ct.IsCancellationRequested && _enabled)
        {
            try
            {
                long now = Environment.TickCount64;
                if (now - lastDetect >= DetectEveryMs)
                {
                    lastDetect = now;
                    DetectChannels(now);
                }
                InferDueChannel(now);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "cw-skim worker tick failed");
            }
            ct.WaitHandle.WaitOne(InferSchedulerMs);
        }
    }

    private void DetectChannels(long nowMs)
    {
        // 3 s Welch periodogram over the detection band.
        float[] tail = SnapshotTail(ModelRateHz * 3);
        if (tail.Length < DetectFft * 2) return;

        int loBin = (int)(DetectLowHz * DetectFft / ModelRateHz);
        int hiBin = (int)(DetectHighHz * DetectFft / ModelRateHz);
        int nBins = hiBin - loBin + 1;
        var power = new double[nBins];
        var win = new double[DetectFft];
        for (int i = 0; i < DetectFft; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / DetectFft);

        int segments = 0;
        for (int s0 = 0; s0 + DetectFft <= tail.Length; s0 += DetectFft / 2)
        {
            segments++;
            for (int b = 0; b < nBins; b++)
            {
                int k = loBin + b;
                double re = 0, im = 0;
                for (int n = 0; n < DetectFft; n += 2)
                {
                    // stride-2 sampling halves the cost; at CW bandwidths the
                    // aliased noise penalty is negligible for peak-finding.
                    double a = -2 * Math.PI * k * n / DetectFft;
                    double v = tail[s0 + n] * win[n];
                    re += v * Math.Cos(a);
                    im += v * Math.Sin(a);
                }
                power[b] += re * re + im * im;
            }
        }
        if (segments == 0) return;

        var db = new double[nBins];
        for (int b = 0; b < nBins; b++) db[b] = 10 * Math.Log10(power[b] / segments + 1e-12);
        var sorted = (double[])db.Clone();
        Array.Sort(sorted);
        double floor = sorted[nBins / 2];

        // Peak-pick: strongest first, enforce spacing.
        var picks = new List<(double hz, double snr)>();
        var order = Enumerable.Range(0, nBins).OrderByDescending(b => db[b]);
        foreach (int b in order)
        {
            double snr = db[b] - floor;
            if (snr < PeakOverFloorDb) break;
            double hz = (loBin + b) * (double)ModelRateHz / DetectFft;
            if (picks.Any(p => Math.Abs(p.hz - hz) < PeakMinSpacingHz)) continue;
            picks.Add((hz, snr));
            if (picks.Count >= MaxChannels) break;
        }

        bool changed = false;
        lock (_lock)
        {
            foreach (var (hz, snr) in picks)
            {
                var c = _channels.FirstOrDefault(c => Math.Abs(c.PitchHz - hz) < ChannelMatchHz);
                if (c is null)
                {
                    if (_channels.Count(c => c.Active) >= MaxChannels) continue;
                    _channels.Add(new Channel
                    {
                        Id = _nextChannelId++,
                        PitchHz = hz,
                        SnrDb = snr,
                        LastSeenMs = nowMs,
                    });
                    changed = true;
                }
                else
                {
                    c.PitchHz = c.PitchHz * 0.8 + hz * 0.2;   // gentle track
                    c.SnrDb = snr;
                    c.LastSeenMs = nowMs;
                    if (!c.Active) { c.Active = true; changed = true; }
                }
            }
            foreach (var c in _channels)
            {
                if (c.Active && nowMs - c.LastSeenMs > ChannelHoldMs)
                {
                    c.Active = false;
                    changed = true;
                }
            }
            _channels.RemoveAll(c => !c.Active && nowMs - c.LastSeenMs > ChannelHoldMs * 4);
        }
        if (changed) PublishRoster();
    }

    private void InferDueChannel(long nowMs)
    {
        if (!ModelAvailable || _session is null) return;
        Channel? due;
        lock (_lock)
        {
            due = _channels
                .Where(c => c.Active
                    && c.PitchHz >= DecodeLowHz && c.PitchHz <= DecodeHighHz
                    && nowMs - c.LastInferMs >= InferEveryMsPerChannel)
                .OrderBy(c => c.LastInferMs)
                .FirstOrDefault();
            if (due is not null) due.LastInferMs = nowMs;
        }
        if (due is null) return;

        float[] window = SnapshotTail(ModelRateHz * WindowSeconds);
        if (window.Length < ModelRateHz * 4) return;

        BandpassInPlace(window, due.PitchHz);
        string full = Decode(window);
        // Stable-prefix streaming, per channel (same contract as the browser
        // worker): the trailing VolatileSeconds of audio produce revisable
        // characters — emit only what has stabilized beyond them.
        double stableFrac = Math.Max(0, 1 - VolatileSeconds * ModelRateHz / window.Length);
        string stable = full[..Math.Min(full.Length, (int)(full.Length * stableFrac))];
        string delta = "";
        lock (_lock)
        {
            if (stable.Length > 0)
            {
                if (due.Emitted.Length == 0 || stable.StartsWith(due.Emitted, StringComparison.Ordinal))
                {
                    delta = stable[due.Emitted.Length..];
                    due.Emitted = stable;
                }
                else
                {
                    int k = Math.Min(due.Emitted.Length, stable.Length);
                    while (k > 0 && !stable.StartsWith(due.Emitted[^k..], StringComparison.Ordinal)) k--;
                    delta = stable[k..];
                    due.Emitted = stable;
                }
            }
        }
        if (delta.Length > 0)
        {
            _digital.Events.PublishCwSkim(new
            {
                receiver = _receiver,
                kind = "text",
                channel = ChannelDto(due),
                delta,
            });
        }
    }

    private void PublishRoster()
    {
        object[] roster;
        lock (_lock) roster = _channels.Where(c => c.Active).Select(ChannelDto).ToArray();
        _digital.Events.PublishCwSkim(new
        {
            receiver = _receiver,
            kind = "roster",
            enabled = _enabled,
            channels = roster,
        });
    }

    private float[] SnapshotTail(int samples)
    {
        lock (_lock)
        {
            long have = Math.Min(_ringWrite, RingLen);
            int n = (int)Math.Min(samples, have);
            var outBuf = new float[n];
            long start = _ringWrite - n;
            for (int i = 0; i < n; i++) outBuf[i] = _ring[(int)((start + i) % RingLen)];
            return outBuf;
        }
    }

    // ---- DSP: channel isolation ---------------------------------------------

    private static void BandpassInPlace(float[] x, double centerHz)
    {
        // Two cascaded RBJ constant-skirt bandpass biquads, Q=10, centered on
        // the channel pitch: isolates ~±40 Hz so neighbours inside the model's
        // 400-1200 window can't interleave into this channel's CTC stream.
        for (int pass = 0; pass < 2; pass++)
        {
            double w0 = 2 * Math.PI * centerHz / ModelRateHz;
            double q = 10, alpha = Math.Sin(w0) / (2 * q);
            double b0 = alpha, b1 = 0, b2 = -alpha;
            double a0 = 1 + alpha, a1 = -2 * Math.Cos(w0), a2 = 1 - alpha;
            b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double xi = x[i];
                double yi = b0 * xi + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = xi; y2 = y1; y1 = yi;
                x[i] = (float)yi;
            }
        }
    }

    // ---- DSP: DeepCW preprocessing + CTC (contract-identical to the browser
    // worker; see model_en.json) ----------------------------------------------

    private void EnsureModel()
    {
        if (ModelAvailable || _session is not null) return;
        try
        {
            string? dir = FindModelDir();
            if (dir is null) { _log.LogWarning("cw-skim: deepcw model not found"); return; }
            _modelPath = Path.Combine(dir, "model_en.onnx");
            using var metaStream = File.OpenRead(Path.Combine(dir, "model_en.json"));
            using var meta = JsonDocument.Parse(metaStream);
            var root = meta.RootElement;
            _vocab = root.GetProperty("chars").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
            _blankIndex = root.GetProperty("blank_index").GetInt32();
            _fftLen = root.GetProperty("fft_length").GetInt32();
            _hopLen = root.GetProperty("hop_length").GetInt32();
            _freqBins = root.GetProperty("spectrogram_frequency_bins").GetInt32();
            _minFreqHz = root.GetProperty("spectrogram_min_freq_hz").GetDouble();

            _hann = new float[_fftLen];
            for (int i = 0; i < _fftLen; i++)
                _hann[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / _fftLen));
            double binHz = (double)ModelRateHz / _fftLen;
            int startBin = (int)Math.Ceiling(_minFreqHz / binHz);
            _cos = new float[_freqBins * _fftLen];
            _sin = new float[_freqBins * _fftLen];
            for (int b = 0; b < _freqBins; b++)
                for (int n = 0; n < _fftLen; n++)
                {
                    double a = -2 * Math.PI * (startBin + b) * n / _fftLen;
                    _cos[b * _fftLen + n] = (float)Math.Cos(a);
                    _sin[b * _fftLen + n] = (float)Math.Sin(a);
                }

            // Fully qualified: ASP.NET's Microsoft.AspNetCore.Builder also
            // exports a SessionOptions and both namespaces are in scope.
            var opts = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = 2 };
            _session = new InferenceSession(_modelPath, opts);
            ModelAvailable = true;
            _log.LogInformation("cw-skim: model loaded from {Path}", _modelPath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "cw-skim: model load failed");
        }
    }

    private static string? FindModelDir()
    {
        string[] candidates =
        {
            Environment.GetEnvironmentVariable("ZEUS_DEEPCW_DIR") ?? "",
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "deepcw"),
            Path.Combine(AppContext.BaseDirectory, "..", "zeus-web", "public", "deepcw"),
            Path.Combine(Directory.GetCurrentDirectory(), "zeus-web", "public", "deepcw"),
        };
        return candidates.FirstOrDefault(d =>
            d.Length > 0 && File.Exists(Path.Combine(d, "model_en.onnx")));
    }

    private string Decode(float[] audio)
    {
        if (_session is null || _cos is null || _sin is null || _hann is null) return "";
        int pad = _fftLen / 2;
        int padded = audio.Length + pad * 2;
        var buf = new float[padded];
        Array.Copy(audio, 0, buf, pad, audio.Length);
        for (int i = 0; i < pad; i++)
        {
            buf[i] = audio[Math.Min(pad - i, audio.Length - 1)];
            buf[pad + audio.Length + i] = audio[Math.Max(0, audio.Length - 2 - i)];
        }
        int frames = 1 + (padded - _fftLen) / _hopLen;
        var spec = new float[frames * _freqBins];
        var frame = new float[_fftLen];
        for (int f = 0; f < frames; f++)
        {
            int s0 = f * _hopLen;
            for (int i = 0; i < _fftLen; i++) frame[i] = buf[s0 + i] * _hann[i];
            for (int b = 0; b < _freqBins; b++)
            {
                float re = 0, im = 0;
                int o = b * _fftLen;
                var cosSeg = _cos.AsSpan(o, _fftLen);
                var sinSeg = _sin.AsSpan(o, _fftLen);
                re = TensorPrimitives.Dot<float>(frame, cosSeg);
                im = TensorPrimitives.Dot<float>(frame, sinSeg);
                spec[f * _freqBins + b] = MathF.Log(1 + MathF.Sqrt(re * re + im * im));
            }
        }

        var input = new DenseTensor<float>(spec, new[] { 1, 1, frames, _freqBins });
        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("spectrogram", input),
        });
        var lp = results.First(r => r.Name == "log_probs").AsTensor<float>();
        int T = lp.Dimensions[1], C = lp.Dimensions[2];
        var sb = new System.Text.StringBuilder();
        int prev = -1;
        for (int t = 0; t < T; t++)
        {
            int best = 0;
            float bestV = float.NegativeInfinity;
            for (int c = 0; c < C; c++)
            {
                float v = lp[0, t, c];
                if (v > bestV) { bestV = v; best = c; }
            }
            if (best != prev && best != _blankIndex && best < _vocab.Length)
                sb.Append(_vocab[best]);
            prev = best;
        }
        return sb.ToString();
    }

    // ---- lifecycle -----------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Disable();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Disable();
        _session?.Dispose();
        _cts?.Dispose();
    }
}
