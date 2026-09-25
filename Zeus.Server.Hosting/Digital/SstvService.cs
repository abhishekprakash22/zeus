// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// SstvService — analog SSTV receive IN CORE, alongside FT8/FT4/WSPR.
//
// RX: taps DspPipelineService.RxAudioAvailable on the audio thread through the
// shared ÷4 decimator into a 12 kHz ring (the WsprService discipline: no
// allocation, TryEnter so a slow reader drops a block rather than stalling
// audio). A worker thread drains the ring into SstvDecoder every 50 ms; the
// decoder's events become `sstv` SSE frames on the Digital EventHub:
//     kind:"start" — a VIS header was decoded; picture metadata
//     kind:"rows"  — freshly decoded rows, base64 RGB
//     kind:"end"   — picture finished; the client re-fetches /sstv/image/{id}
//                    because the end-of-picture re-render (final slant fit)
//                    redraws every row.
// SSTV is self-announcing (VIS), so unlike WSPR/FT8 there is no slot clock.
//
// Pure managed code — no native library, nothing to build per platform.
// Received pictures are kept in memory for the session (a small ring);
// persistence to disk is the gallery follow-up.

using Zeus.Contracts;
using Zeus.Server.Hosting.Digital.Sstv;

namespace Zeus.Server.Hosting.Digital;

public sealed record SstvImageMeta(
    int Id, string Mode, int Width, int Height, int RowsDone,
    double OffsetHz, double ClockErrorPpm, long DialHz, string SideBand,
    long StartedUnixMs, long? EndedUnixMs, string? EndReason);

public sealed record SstvStatusDto(
    bool Enabled, int Receiver, SstvImageMeta? Current, SstvImageMeta[] Images, string[] Modes);

public sealed record SstvImageDto(SstvImageMeta Meta, string Rgb);

public sealed class SstvService : IHostedService, IDisposable
{
    private const int RingLen = 1 << 18;                 // 262 k samples ≈ 21.8 s at 12 kHz
    private const int PollMs = 50;
    private const int KeepImages = 12;

    private readonly DspPipelineService _pipeline;
    private readonly DigitalService _digital;
    private readonly RadioService? _radio;
    private readonly ILogger<SstvService> _log;

    // ---- RX ring (audio thread → worker) ------------------------------------
    private readonly object _rxLock = new();
    private readonly float[] _ring = new float[RingLen];
    private long _ringWrite;
    private readonly Decimator4 _decim = new();
    private volatile bool _enabled;
    private int _receiver;

    // ---- worker-owned -------------------------------------------------------
    private readonly SstvDecoder _decoder = new();
    private long _ringRead;
    private volatile bool _stopRequested;
    private volatile bool _resetRequested;
    private Thread? _worker;
    private CancellationTokenSource? _cts;

    // ---- pictures (worker writes, HTTP reads) --------------------------------
    private readonly object _imgLock = new();
    private readonly LinkedList<Entry> _images = new();
    private Entry? _current;

    private sealed class Entry
    {
        public required SstvImage Image;
        public long DialHz;
        public string SideBand = "";
        public long StartedUnixMs;
        public long? EndedUnixMs;
    }

    public SstvService(
        DspPipelineService pipeline, DigitalService digital, ILogger<SstvService> log,
        RadioService? radio = null)
    {
        _pipeline = pipeline;
        _digital = digital;
        _radio = radio;
        _log = log;
        _decoder.ImageStarted += OnImageStarted;
        _decoder.RowsDecoded += OnRowsDecoded;
        _decoder.ImageEnded += OnImageEnded;
    }

    public bool Enabled => _enabled;

    // ---- control ------------------------------------------------------------

    /// <summary>Start (or re-assert) SSTV receive. Idempotent: re-enabling the
    /// same receiver keeps a picture in progress.</summary>
    public void Enable(int receiver)
    {
        lock (_rxLock)
        {
            bool restart = !_enabled || _receiver != receiver;
            if (!restart) return;
            _receiver = receiver;
            _decim.Reset();
            _ringWrite = 0;
            _ringRead = 0;              // worker reads it under this same lock
            _resetRequested = true;
            _enabled = true;
        }
        _log.LogInformation("sstv: RX enabled (rx={Rx})", receiver);
    }

    public void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        _stopRequested = true;
        _log.LogInformation("sstv: RX disabled");
    }

    /// <summary>Operator "stop": end the picture in progress, keep listening.</summary>
    public void StopCurrent() => _stopRequested = true;

    public SstvStatusDto Status()
    {
        lock (_imgLock)
        {
            return new SstvStatusDto(
                _enabled, _receiver,
                _current is null ? null : Meta(_current),
                _images.Select(Meta).ToArray(),
                SstvModes.All.Select(m => m.Name).ToArray());
        }
    }

    public SstvImageDto? Image(int id)
    {
        lock (_imgLock)
        {
            var e = _current?.Image.Id == id ? _current : _images.FirstOrDefault(x => x.Image.Id == id);
            if (e is null) return null;
            return new SstvImageDto(Meta(e), Convert.ToBase64String(e.Image.Rgb));
        }
    }

    // ---- lifecycle ----------------------------------------------------------

    public Task StartAsync(CancellationToken ct)
    {
        _cts = new CancellationTokenSource();
        if (_pipeline is not null) _pipeline.RxAudioAvailable += OnRxAudio;   // null in tests
        _worker = new Thread(() => WorkerLoop(_cts.Token)) { IsBackground = true, Name = "sstv-rx" };
        _worker.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _enabled = false;
        if (_pipeline is not null) _pipeline.RxAudioAvailable -= OnRxAudio;
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _cts?.Cancel();

    // ---- audio thread -------------------------------------------------------

    /// <summary>Test seam: 12 kHz samples captured but not yet decoded.</summary>
    internal long Backlog { get { lock (_rxLock) return _ringWrite - _ringRead; } }

    /// <summary>Test seam: feed RX audio as the pipeline would.</summary>
    internal void FeedRxAudio(int receiver, int sampleRateHz, ReadOnlyMemory<float> samples) =>
        OnRxAudio(receiver, sampleRateHz, samples);

    /// <summary>RX AUDIO THREAD — no allocation, no long locks, no throw.</summary>
    private void OnRxAudio(int receiver, int sampleRateHz, ReadOnlyMemory<float> samples)
    {
        if (!_enabled || receiver != _receiver || sampleRateHz != 48_000) return;
        if (!Monitor.TryEnter(_rxLock)) return;
        try
        {
            long w = _ringWrite;
            _ringWrite = w + _decim.Process(samples.Span, _ring, w, RingLen);
        }
        finally { Monitor.Exit(_rxLock); }
    }

    // ---- worker -------------------------------------------------------------

    private void WorkerLoop(CancellationToken ct)
    {
        var chunk = new float[RingLen / 4];
        while (!ct.IsCancellationRequested)
        {
            Thread.Sleep(PollMs);
            try
            {
                if (_stopRequested)
                {
                    _stopRequested = false;
                    _decoder.Stop();
                }
                if (_resetRequested)
                {
                    _resetRequested = false;
                    _decoder.Stop();
                    _decoder.Reset();
                }
                if (!_enabled) continue;

                while (true)
                {
                    int n;
                    lock (_rxLock)
                    {
                        long avail = _ringWrite - _ringRead;
                        if (avail <= 0) break;
                        if (avail > RingLen - chunk.Length)
                        {
                            // Fell a whole ring behind (a stall): skip ahead
                            // rather than decode a torn buffer.
                            _log.LogWarning("sstv: worker overrun, skipping {N} samples", avail);
                            _ringRead = _ringWrite;
                            _decoder.Stop();
                            break;
                        }
                        n = (int)Math.Min(avail, chunk.Length);
                        for (int i = 0; i < n; i++)
                            chunk[i] = _ring[(_ringRead + i) & (RingLen - 1)];
                        _ringRead += n;
                    }
                    if (n == 0) break;
                    _decoder.Process(chunk.AsSpan(0, n));
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "sstv: decoder fault — resetting");
                _decoder.Reset();
            }
        }
    }

    // ---- decoder events (worker thread) -------------------------------------

    private void OnImageStarted(SstvImage img)
    {
        var st = _radio?.Snapshot();
        var e = new Entry
        {
            Image = img,
            DialHz = st?.VfoHz ?? 0,
            SideBand = st?.Mode.ToString() ?? "",
            StartedUnixMs = (long)_digital.Clock.UtcNowMs,
        };
        lock (_imgLock) _current = e;
        _log.LogInformation("sstv: {Mode} started (offset {Off:+0;-0} Hz)", img.Mode.Name, img.OffsetHz);
        _digital.Events.PublishSstv(new { kind = "start", image = Meta(e) });
    }

    private void OnRowsDecoded(SstvImage img, int firstRow, int count)
    {
        int stride = img.Mode.Width * 3;
        string rgb = Convert.ToBase64String(img.Rgb, firstRow * stride, count * stride);
        _digital.Events.PublishSstv(new
        {
            kind = "rows", id = img.Id, row = firstRow, count, width = img.Mode.Width, rgb,
        });
    }

    private void OnImageEnded(SstvImage img, SstvEndReason reason)
    {
        Entry? e;
        lock (_imgLock)
        {
            e = _current?.Image == img ? _current : null;
            _current = null;
            if (e is null || reason == SstvEndReason.FalseStart) e = null;
            else
            {
                e.EndedUnixMs = (long)_digital.Clock.UtcNowMs;
                _images.AddFirst(e);
                while (_images.Count > KeepImages) _images.RemoveLast();
            }
        }
        if (reason == SstvEndReason.FalseStart)
        {
            _log.LogDebug("sstv: false start ({Mode}) discarded", img.Mode.Name);
            _digital.Events.PublishSstv(new { kind = "discard", id = img.Id });
            return;
        }
        if (e is null) return;
        _log.LogInformation("sstv: {Mode} ended ({Reason}, {Rows}/{H} rows, clock {Ppm:+0;-0} ppm)",
            img.Mode.Name, reason, img.RowsDone, img.Mode.Height, img.ClockError * 1e6);
        _digital.Events.PublishSstv(new { kind = "end", image = Meta(e) });
    }

    private static SstvImageMeta Meta(Entry e)
    {
        var i = e.Image;
        return new SstvImageMeta(
            i.Id, i.Mode.Name, i.Mode.Width, i.Mode.Height, i.RowsDone,
            Math.Round(i.OffsetHz, 1), Math.Round(i.ClockError * 1e6),
            e.DialHz, e.SideBand, e.StartedUnixMs, e.EndedUnixMs,
            i.EndReason?.ToString());
    }
}
