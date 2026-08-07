// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// The Recorder — the organ the audio-tap sockets were built for (the
// AudioTapBridge comments name it as their intended client). Records any of
// the three station feeds to WAV on the Pi:
//   rx     — RX band audio            (DspPipelineService.RxAudioAvailable)
//   txmic  — raw TX mic audio         (TxAudioIngest.MicPcmTapped, f32le@48k)
//   txair  — processed on-air TX audio (DspPipelineService.TxMonitorAudioAvailable;
//            flows while the TX chain runs — MON engaged or MOX keyed)
// 16-bit PCM mono, source rate; RIFF sizes patched on stop. Files live in
// $XDG_DATA_HOME/Zeus/recordings with self-describing names. Disk-gated on
// start and auto-stopped near-full, per the self-update gate's discipline.

using System.Text;
using Zeus.Contracts;

namespace Zeus.Server.Hosting;

public sealed class RecorderService : IDisposable
{
    private const long MinFreeToStart = 300L * 1024 * 1024;
    private const long MinFreeToContinue = 100L * 1024 * 1024;
    private const int FreeCheckEveryBlocks = 64;

    private readonly DspPipelineService _pipeline;
    private readonly TxAudioIngest _txIngest;
    private readonly RadioService _radio;
    private readonly ILogger<RecorderService> _log;

    private readonly object _lock = new();
    private FileStream? _file;
    private string _source = "";
    private string _path = "";
    private int _rateHz;
    private long _dataBytes;
    private long _startedUnixMs;
    private int _blocksSinceFreeCheck;
    private string? _lastError;

    public RecorderService(
        DspPipelineService pipeline, TxAudioIngest txIngest,
        RadioService radio, ILogger<RecorderService> log)
    {
        _pipeline = pipeline;
        _txIngest = txIngest;
        _radio = radio;
        _log = log;
    }

    public static string RecordingsDir()
    {
        string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(data, "Zeus", "recordings");
    }

    public object StatusDto()
    {
        lock (_lock)
        {
            return new
            {
                recording = _file is not null,
                source = _source,
                fileName = _path.Length > 0 ? Path.GetFileName(_path) : null,
                elapsedSec = _file is not null
                    ? Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _startedUnixMs) / 1000.0)
                    : 0,
                bytes = _dataBytes,
                error = _lastError,
            };
        }
    }

    public bool Start(string source)
    {
        source = source.ToLowerInvariant();
        if (source is not ("rx" or "txmic" or "txair")) { SetError($"unknown source '{source}'"); return false; }
        lock (_lock)
        {
            if (_file is not null) return true; // already rolling
            try
            {
                string dir = RecordingsDir();
                Directory.CreateDirectory(dir);
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir)) ?? "/");
                if (drive.AvailableFreeSpace < MinFreeToStart)
                {
                    SetError($"only {drive.AvailableFreeSpace / (1024 * 1024)} MB free — need 300 MB to start");
                    return false;
                }

                var snap = _radio.Snapshot();
                string freq = (snap.VfoHz / 1e6).ToString("0.000",
                    System.Globalization.CultureInfo.InvariantCulture);
                string name = $"{DateTime.Now:yyyyMMdd-HHmmss}_{source}_{freq}MHz_{Sanitize(snap.Mode.ToString())}.wav";
                _path = Path.Combine(dir, name);
                _file = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
                _rateHz = source == "txmic" ? 48000 : 0;   // rx/txair learn from first block
                _dataBytes = 0;
                _blocksSinceFreeCheck = 0;
                _startedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _source = source;
                _lastError = null;
                WriteWavHeader(_file, _rateHz == 0 ? 48000 : _rateHz); // patched on stop
            }
            catch (Exception ex)
            {
                _file?.Dispose(); _file = null;
                SetError(ex.Message);
                return false;
            }
        }
        switch (source)
        {
            case "rx": _pipeline.RxAudioAvailable += OnFloatBlock; break;
            case "txair": _pipeline.TxMonitorAudioAvailable += OnFloatBlock; break;
            case "txmic": _txIngest.MicPcmTapped += OnMicBytes; break;
        }
        _log.LogInformation("recorder: started {Source} -> {Path}", source, _path);
        return true;
    }

    public void Stop()
    {
        string src;
        lock (_lock)
        {
            if (_file is null) return;
            src = _source;
        }
        switch (src)
        {
            case "rx": _pipeline.RxAudioAvailable -= OnFloatBlock; break;
            case "txair": _pipeline.TxMonitorAudioAvailable -= OnFloatBlock; break;
            case "txmic": _txIngest.MicPcmTapped -= OnMicBytes; break;
        }
        lock (_lock)
        {
            if (_file is null) return;
            try
            {
                PatchWavHeader(_file, _rateHz == 0 ? 48000 : _rateHz, _dataBytes);
                _file.Dispose();
                _log.LogInformation("recorder: stopped {Path} ({Bytes} bytes)", _path, _dataBytes);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "recorder: stop failed");
            }
            _file = null;
            _source = "";
        }
    }

    // ---- feeds ---------------------------------------------------------------

    private void OnFloatBlock(int receiver, int rateHz, ReadOnlyMemory<float> samples)
    {
        if (receiver != 0) return;
        WriteFloats(samples.Span, rateHz);
    }

    private void OnMicBytes(ReadOnlyMemory<byte> f32le)
    {
        int n = f32le.Length / 4;
        if (n == 0) return;
        // Rent-free view: reinterpret the little-endian float payload.
        var floats = new float[n];
        var src = f32le.Span;
        for (int i = 0; i < n; i++)
            floats[i] = BitConverter.ToSingle(src.Slice(i * 4, 4));
        WriteFloats(floats, 48000);
    }

    private void WriteFloats(ReadOnlySpan<float> x, int rateHz)
    {
        lock (_lock)
        {
            if (_file is null) return;
            if (_rateHz == 0) _rateHz = rateHz;
            var buf = new byte[x.Length * 2];
            for (int i = 0; i < x.Length; i++)
            {
                float v = Math.Clamp(x[i], -1f, 1f);
                short s = (short)MathF.Round(v * 32767f);
                buf[i * 2] = (byte)s;
                buf[i * 2 + 1] = (byte)(s >> 8);
            }
            try
            {
                _file.Write(buf);
                _dataBytes += buf.Length;
                if (++_blocksSinceFreeCheck >= FreeCheckEveryBlocks)
                {
                    _blocksSinceFreeCheck = 0;
                    var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_path)) ?? "/");
                    if (drive.AvailableFreeSpace < MinFreeToContinue)
                    {
                        SetError("disk nearly full — recording auto-stopped");
                        _log.LogWarning("recorder: auto-stop, {Free} MB free",
                            drive.AvailableFreeSpace / (1024 * 1024));
                        // Stop() re-enters the lock; defer to a task.
                        _ = Task.Run(Stop);
                    }
                }
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
                _ = Task.Run(Stop);
            }
        }
    }

    // ---- files ---------------------------------------------------------------

    public object[] ListFiles()
    {
        string dir = RecordingsDir();
        if (!Directory.Exists(dir)) return Array.Empty<object>();
        return Directory.EnumerateFiles(dir, "*.wav")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Take(200)
            .Select(f => (object)new
            {
                name = f.Name,
                bytes = f.Length,
                createdUtc = f.CreationTimeUtc.ToString("o"),
                durationSec = Math.Max(0, (f.Length - 44) / 2.0 / ReadRate(f.FullName)),
            })
            .ToArray();
    }

    public bool DeleteFile(string name)
    {
        string? p = SafePath(name);
        if (p is null || !File.Exists(p)) return false;
        lock (_lock)
        {
            if (_file is not null && string.Equals(p, _path, StringComparison.Ordinal))
                return false;   // never delete the live take
        }
        File.Delete(p);
        return true;
    }

    public string? SafePath(string name)
    {
        string clean = Path.GetFileName(name);
        if (clean.Length == 0 || !clean.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.Combine(RecordingsDir(), clean);
    }

    private static double ReadRate(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            f.Seek(24, SeekOrigin.Begin);
            Span<byte> b = stackalloc byte[4];
            return f.Read(b) == 4 ? Math.Max(1, BitConverter.ToInt32(b)) : 48000;
        }
        catch { return 48000; }
    }

    private static string Sanitize(string s) =>
        new(s.Where(char.IsLetterOrDigit).ToArray());

    private void SetError(string msg)
    {
        lock (_lock) _lastError = msg;
    }

    // ---- WAV plumbing --------------------------------------------------------

    private static void WriteWavHeader(FileStream f, int rateHz)
    {
        Span<byte> h = stackalloc byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(h);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(h[8..]);
        BitConverter.TryWriteBytes(h[16..], 16);
        BitConverter.TryWriteBytes(h[20..], (short)1);      // PCM
        BitConverter.TryWriteBytes(h[22..], (short)1);      // mono
        BitConverter.TryWriteBytes(h[24..], rateHz);
        BitConverter.TryWriteBytes(h[28..], rateHz * 2);    // byte rate
        BitConverter.TryWriteBytes(h[32..], (short)2);      // block align
        BitConverter.TryWriteBytes(h[34..], (short)16);     // bits
        Encoding.ASCII.GetBytes("data").CopyTo(h[36..]);
        f.Write(h);
    }

    private static void PatchWavHeader(FileStream f, int rateHz, long dataBytes)
    {
        f.Seek(4, SeekOrigin.Begin);
        f.Write(BitConverter.GetBytes((int)Math.Min(int.MaxValue, dataBytes + 36)));
        f.Seek(24, SeekOrigin.Begin);
        f.Write(BitConverter.GetBytes(rateHz));
        f.Write(BitConverter.GetBytes(rateHz * 2));
        f.Seek(40, SeekOrigin.Begin);
        f.Write(BitConverter.GetBytes((int)Math.Min(int.MaxValue, dataBytes)));
        f.Flush();
    }

    public void Dispose() => Stop();
}
