// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// SstvService — analog SSTV receive IN CORE, alongside FT8/FT4/WSPR.
//
// RX: taps DspPipelineService.RxAudioAvailable on the audio thread through the
// shared ÷4 decimator into a 12 kHz single-producer/single-consumer ring with
// NO lock: WSPR's TryEnter-and-drop is fine for a 2-minute slot decode, but
// SSTV is a continuous time base — one dropped 21 ms block shifts every later
// line of the picture. A worker thread drains the ring every 50 ms; the
// decoder's events become `sstv` SSE frames on the Digital EventHub:
//     kind:"start" — a VIS header was decoded; picture metadata
//     kind:"rows"  — freshly decoded rows, base64 RGB
//     kind:"end"   — picture finished; the client re-fetches /sstv/image/{id}
//                    because the end-of-picture re-render (final slant fit)
//                    redraws every row.
// SSTV is self-announcing (VIS), so unlike WSPR/FT8 there is no slot clock.
//
// kind:"update" / "removed" follow gallery edits (re-render, delete, a late
// FSK-ID callsign).
//
// GALLERY: every finished picture is written to SstvGallery (PNG + JSON in
// PrefsDbPath.SstvDir()) and the index is reloaded at startup, so the
// operator's pictures survive restarts. The demodulated track behind the
// newest few pictures stays in memory so they can be re-rendered (slant,
// shift, "decode as…"); older and reloaded pictures are fixed PNGs.
//
// Pure managed code — no native library, nothing to build per platform.

using Zeus.Contracts;
using Zeus.Server.Hosting.Digital.Sstv;

namespace Zeus.Server.Hosting.Digital;

public sealed record SstvImageMeta(
    int Id, string Mode, int Width, int Height, int RowsDone,
    double OffsetHz, double ClockErrorPpm, long DialHz, string SideBand,
    long StartedUnixMs, long? EndedUnixMs, string? EndReason,
    string? Key, bool Adjustable, double SlantPpm, double ShiftPx, string? Callsign);

public sealed record SstvModeInfo(string Name, int Width, int Height, double DurationMs);

public sealed record SstvStatusDto(
    bool Enabled, int Receiver, SstvImageMeta? Current, SstvImageMeta[] Images, string[] Modes,
    string? GalleryDir, SstvModeInfo[] ModeInfos);

/// <summary>A picture's pixels: <see cref="Rgb"/> (raw base64 RGB) for pictures
/// in memory, <see cref="Png"/> (base64 PNG) for ones only on disk.</summary>
public sealed record SstvImageDto(SstvImageMeta Meta, string? Rgb, string? Png);

public sealed record SstvAdjustRequest(string? Mode, double? SlantPpm, double? ShiftPx);

public sealed class SstvService : IHostedService, IDisposable
{
    private const int RingLen = 1 << 18;                 // 262 k samples ≈ 21.8 s at 12 kHz
    private const int PollMs = 50;
    private const int MaxIndex = 200;                    // pictures listed
    private const int KeepPixels = 12;                   // newest with pixels in memory
    private const int KeepRecordings = 6;                // newest re-renderable

    private readonly DspPipelineService _pipeline;
    private readonly DigitalService _digital;
    private readonly RadioService? _radio;
    private readonly ILogger<SstvService> _log;
    private readonly SstvGallery? _gallery;

    // ---- RX ring (audio thread → worker), lock-free SPSC ---------------------
    // The audio thread alone writes samples and then publishes _ringWrite
    // (Volatile.Write); the worker alone advances _ringRead. Neither index
    // is ever reset — a restart just moves _ringRead up to _ringWrite.
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

    // ---- pictures (worker + HTTP; guarded by _imgLock) -----------------------
    private readonly object _imgLock = new();
    private readonly List<Entry> _images = new();        // finished, newest first
    private Entry? _current;

    private sealed class Entry
    {
        public int Id;
        /// <summary>Pixels (+ recording) while in memory; null for pictures
        /// known only from the gallery on disk.</summary>
        public SstvImage? Image;
        public string? Key;
        public string Mode = "";
        public int Width, Height, RowsDone;
        public double OffsetHz, ClockErrorPpm, SlantPpm, ShiftPx;
        public long DialHz;
        public string SideBand = "";
        public long StartedUnixMs;
        public long? EndedUnixMs;
        public string? EndReason;
        public string? Callsign;

        public void Absorb(SstvImage img)
        {
            Image = img;
            Mode = img.Mode.Name;
            Width = img.Mode.Width;
            Height = img.Mode.Height;
            RowsDone = img.RowsDone;
            OffsetHz = Math.Round(img.OffsetHz, 1);
            ClockErrorPpm = Math.Round(img.ClockError * 1e6);
            SlantPpm = img.SlantPpm;
            ShiftPx = img.ShiftPx;
            EndReason = img.EndReason?.ToString();
        }
    }

    public SstvService(
        DspPipelineService pipeline, DigitalService digital, ILogger<SstvService> log,
        RadioService? radio = null, SstvGallery? gallery = null)
    {
        _pipeline = pipeline;
        _digital = digital;
        _radio = radio;
        _log = log;
        _gallery = gallery;
        _decoder.ImageStarted += OnImageStarted;
        _decoder.RowsDecoded += OnRowsDecoded;
        _decoder.ImageEnded += OnImageEnded;
        _decoder.CallsignDecoded += OnCallsign;
    }

    public bool Enabled => _enabled;

    // ---- control ------------------------------------------------------------

    /// <summary>Start (or re-assert) SSTV receive. Idempotent: re-enabling the
    /// same receiver keeps a picture in progress.</summary>
    public void Enable(int receiver)
    {
        if (_enabled && _receiver == receiver) return;
        _receiver = receiver;
        _resetRequested = true;         // worker: reset decoder, skip stale samples
        _enabled = true;
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
                SstvModes.All.Select(m => m.Name).ToArray(),
                _gallery?.Dir,
                SstvModes.All.Select(m => new SstvModeInfo(m.Name, m.Width, m.Height,
                    Math.Round(SstvEncoder.VisMs + m.DurationMs))).ToArray());
        }
    }

    public SstvImageDto? Image(int id)
    {
        Entry? e;
        string? rgb = null;
        lock (_imgLock)
        {
            e = _current?.Id == id ? _current : _images.FirstOrDefault(x => x.Id == id);
            if (e is null) return null;
            if (e.Image is not null) rgb = Convert.ToBase64String(e.Image.Rgb);
            else if (e.Key is null) return null;
        }
        if (rgb is not null) return new SstvImageDto(Meta(e), rgb, null);
        try
        {
            var png = _gallery?.ReadPng(e.Key!);
            return png is null ? null : new SstvImageDto(Meta(e), null, Convert.ToBase64String(png));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "sstv: gallery read failed ({Key})", e.Key);
            return null;
        }
    }

    /// <summary>Re-render a picture (manual slant / shift / "decode as").
    /// Null when the picture is unknown or no longer re-renderable.</summary>
    public SstvImageMeta? Adjust(int id, SstvAdjustRequest req)
    {
        Entry? e;
        SstvImage? src;
        lock (_imgLock)
        {
            e = _images.FirstOrDefault(x => x.Id == id);
            src = e?.Image;
            if (e is null || src?.Recording is null) return null;
        }
        var mode = req.Mode is null ? src.Mode : SstvModes.ByName(req.Mode);
        if (mode is null) return null;
        var img = SstvDecoder.Rerender(src, mode,
            req.SlantPpm ?? src.SlantPpm, req.ShiftPx ?? src.ShiftPx);

        SstvImageMeta meta;
        lock (_imgLock)
        {
            if (!_images.Contains(e)) return null;           // deleted meanwhile
            e.Absorb(img);
            meta = Meta(e);
        }
        Persist(e);
        _digital.Events.PublishSstv(new { kind = "update", image = Meta(e) });
        return meta;
    }

    public bool Delete(int id)
    {
        Entry? e;
        lock (_imgLock)
        {
            e = _images.FirstOrDefault(x => x.Id == id);
            if (e is null) return false;
            _images.Remove(e);
        }
        if (e.Key is not null && _gallery is not null)
        {
            try { _gallery.Delete(e.Key); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "sstv: gallery delete failed ({Key})", e.Key);
            }
        }
        _digital.Events.PublishSstv(new { kind = "removed", id });
        return true;
    }

    // ---- lifecycle ----------------------------------------------------------

    public Task StartAsync(CancellationToken ct)
    {
        _cts = new CancellationTokenSource();
        LoadGallery();
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

    /// <summary>Test seam: Enable's capture restart not yet applied by the worker.</summary>
    internal bool ResetPending => _resetRequested;

    /// <summary>Test seam: 12 kHz samples captured but not yet decoded.</summary>
    internal long Backlog => Volatile.Read(ref _ringWrite) - Volatile.Read(ref _ringRead);

    /// <summary>Test seam: feed RX audio as the pipeline would.</summary>
    internal void FeedRxAudio(int receiver, int sampleRateHz, ReadOnlyMemory<float> samples) =>
        OnRxAudio(receiver, sampleRateHz, samples);

    /// <summary>RX AUDIO THREAD — no allocation, no long locks, no throw.</summary>
    private void OnRxAudio(int receiver, int sampleRateHz, ReadOnlyMemory<float> samples)
    {
        if (!_enabled || receiver != _receiver || sampleRateHz != 48_000) return;
        long w = _ringWrite;
        Volatile.Write(ref _ringWrite, w + _decim.Process(samples.Span, _ring, w, RingLen));
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
                    _decoder.Stop();
                    _decoder.Reset();
                    Volatile.Write(ref _ringRead, Volatile.Read(ref _ringWrite));
                    _resetRequested = false;    // last: audio after this is kept
                }
                if (!_enabled) continue;

                while (true)
                {
                    long write = Volatile.Read(ref _ringWrite);
                    long avail = write - _ringRead;
                    if (avail <= 0) break;
                    if (avail > RingLen - chunk.Length)
                    {
                        // Fell most of a ring behind (a long stall): the oldest
                        // samples may already be overwritten. Skip ahead rather
                        // than decode a torn buffer.
                        _log.LogWarning("sstv: worker overrun, skipping {N} samples", avail);
                        Volatile.Write(ref _ringRead, write);
                        _decoder.Stop();
                        break;
                    }
                    int n = (int)Math.Min(avail, chunk.Length);
                    for (int i = 0; i < n; i++)
                        chunk[i] = _ring[(_ringRead + i) & (RingLen - 1)];
                    Volatile.Write(ref _ringRead, _ringRead + n);
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
            Id = img.Id,
            DialHz = st?.VfoHz ?? 0,
            SideBand = st?.Mode.ToString() ?? "",
            StartedUnixMs = (long)_digital.Clock.UtcNowMs,
        };
        e.Absorb(img);
        lock (_imgLock) _current = e;
        _log.LogInformation("sstv: {Mode} started (offset {Off:+0;-0} Hz)", img.Mode.Name, img.OffsetHz);
        _digital.Events.PublishSstv(new { kind = "start", image = Meta(e) });
    }

    private void OnRowsDecoded(SstvImage img, int firstRow, int count)
    {
        lock (_imgLock) if (_current?.Id == img.Id) _current.RowsDone = img.RowsDone;
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
            e = _current?.Id == img.Id ? _current : null;
            _current = null;
            if (e is not null && reason != SstvEndReason.FalseStart)
            {
                e.Absorb(img);
                e.EndedUnixMs = (long)_digital.Clock.UtcNowMs;
                _images.Insert(0, e);
                TrimMemory();
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
        Persist(e);
        _digital.Events.PublishSstv(new { kind = "end", image = Meta(e) });
    }

    private void OnCallsign(SstvImage img, string call)
    {
        Entry? e;
        lock (_imgLock)
        {
            e = _images.FirstOrDefault(x => x.Id == img.Id);
            if (e is null) return;
            e.Callsign = call;
        }
        _log.LogInformation("sstv: FSK ID {Call} for {Mode}", call, e.Mode);
        Persist(e);
        _digital.Events.PublishSstv(new { kind = "update", image = Meta(e) });
    }

    // ---- gallery ------------------------------------------------------------

    private void LoadGallery()
    {
        if (_gallery is null) return;
        try
        {
            var stored = _gallery.LoadIndex(MaxIndex);
            lock (_imgLock)
                foreach (var m in stored)
                    _images.Add(new Entry
                    {
                        Id = SstvImage.NextId(), Key = m.Key, Mode = m.Mode,
                        Width = m.Width, Height = m.Height, RowsDone = m.RowsDone,
                        OffsetHz = m.OffsetHz, ClockErrorPpm = m.ClockErrorPpm,
                        SlantPpm = m.SlantPpm, ShiftPx = m.ShiftPx,
                        DialHz = m.DialHz, SideBand = m.SideBand,
                        StartedUnixMs = m.StartedUnixMs, EndedUnixMs = m.EndedUnixMs,
                        EndReason = m.EndReason, Callsign = m.Callsign,
                    });
            _log.LogInformation("sstv: gallery {Dir} ({N} picture(s))", _gallery.Dir, stored.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "sstv: gallery index unreadable ({Dir})", _gallery.Dir);
        }
    }

    /// <summary>Write the picture to the gallery (first time: allocate its
    /// key). Best-effort — a failure leaves it session-only.</summary>
    private void Persist(Entry e)
    {
        if (_gallery is null) return;
        SstvStoredMeta meta;
        byte[] rgb;
        lock (_imgLock)
        {
            if (e.Image is null) return;
            if (e.Key is null)
            {
                string key = SstvGallery.MakeKey(e.StartedUnixMs, e.DialHz, e.Mode);
                // Two pictures in one second (a restart mid-VIS) — keep both.
                var taken = _images.Where(x => x != e).Select(x => x.Key).ToHashSet();
                for (int i = 2; taken.Contains(key); i++) key = $"{SstvGallery.MakeKey(e.StartedUnixMs, e.DialHz, e.Mode)}-{i}";
                e.Key = key;
            }
            meta = new SstvStoredMeta(
                e.Key, e.Mode, e.Width, e.Height, e.RowsDone, e.OffsetHz, e.ClockErrorPpm,
                e.DialHz, e.SideBand, e.StartedUnixMs, e.EndedUnixMs, e.EndReason,
                e.SlantPpm, e.ShiftPx, e.Callsign);
            rgb = e.Image.Rgb;
        }
        try
        {
            _gallery.Save(meta, rgb);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "sstv: gallery write failed ({Dir}) — picture kept for this session only",
                _gallery.Dir);
            lock (_imgLock) e.Key = null;
        }
    }

    /// <summary>Bound memory: only the newest pictures keep pixels, fewer
    /// still keep their re-render track. Caller holds _imgLock.</summary>
    private void TrimMemory()
    {
        for (int i = 0; i < _images.Count; i++)
        {
            var img = _images[i].Image;
            if (img is null) continue;
            if (i >= KeepRecordings) img.Recording = null;
            // Drop pixels only once they're safely on disk.
            if (i >= KeepPixels && _images[i].Key is not null) _images[i].Image = null;
        }
    }

    private static SstvImageMeta Meta(Entry e) => new(
        e.Id, e.Mode, e.Width, e.Height, e.RowsDone, e.OffsetHz, e.ClockErrorPpm,
        e.DialHz, e.SideBand, e.StartedUnixMs, e.EndedUnixMs, e.EndReason,
        e.Key, e.Image?.Recording is not null, e.SlantPpm, e.ShiftPx, e.Callsign);
}
