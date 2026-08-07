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

// NAMESPACE TRAP (second bite — see RepoUpdateService.Apply's commit): files
// in the Hosting PROJECT live in `namespace Zeus.Server`, not
// Zeus.Server.Hosting. ZeusHost resolves types unqualified from Zeus.Server;
// declaring Hosting here produced 8x CS0246 at CI.
namespace Zeus.Server;

public sealed class RecorderService : IDisposable
{
    private const long MinFreeToStart = 300L * 1024 * 1024;
    private const long MinFreeToContinue = 100L * 1024 * 1024;
    private const int FreeCheckEveryBlocks = 64;

    private readonly DspPipelineService _pipeline;
    private readonly TxAudioIngest _txIngest;
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly ILogger<RecorderService> _log;

    // ---- instant-replay ring: always taps RX, last RingSeconds of audio ----
    private const int RingSeconds = 60;
    private readonly object _ringLock = new();
    private short[] _ring = Array.Empty<short>();
    private int _ringWrite;
    private long _ringTotal;
    private int _ringRate;

    // ---- local playback (through the RADIO's speakers via the RX bus) ----
    private readonly object _playLock = new();
    private CancellationTokenSource? _playCts;
    private string _playFile = "";
    private double _playTotalSec;
    private long _playStartedMs;

    // ---- voice keyer ----
    private readonly object _keyerLock = new();
    private CancellationTokenSource? _keyerCts;
    private string _keyerFile = "";
    private double _keyerTotalSec;
    private long _keyerStartedMs;

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
        RadioService radio, TxService tx, ILogger<RecorderService> log)
    {
        _pipeline = pipeline;
        _txIngest = txIngest;
        _radio = radio;
        _tx = tx;
        _log = log;
        // The replay ring listens from birth: "what did he just say?" only
        // works if the recorder was already listening before you asked.
        _pipeline.RxAudioAvailable += OnRingBlock;
    }

    private void OnRingBlock(int receiver, int rateHz, ReadOnlyMemory<float> samples)
    {
        if (receiver != 0) return;
        lock (_ringLock)
        {
            if (_ringRate != rateHz || _ring.Length == 0)
            {
                _ringRate = rateHz;
                _ring = new short[Math.Max(1, RingSeconds * rateHz)];
                _ringWrite = 0;
                _ringTotal = 0;
            }
            var x = samples.Span;
            for (int i = 0; i < x.Length; i++)
            {
                float v = Math.Clamp(x[i], -1f, 1f);
                _ring[_ringWrite] = (short)MathF.Round(v * 32767f);
                _ringWrite = (_ringWrite + 1) % _ring.Length;
            }
            _ringTotal += x.Length;
        }
    }

    /// <summary>Dump the last <paramref name="seconds"/> of RX audio from the
    /// always-on ring to a WAV in the recordings dir. Returns the file name.</summary>
    public string? SaveReplay(int seconds)
    {
        seconds = Math.Clamp(seconds, 1, RingSeconds);
        short[] snap;
        int rate;
        lock (_ringLock)
        {
            if (_ringRate == 0 || _ringTotal == 0) { SetError("replay ring is empty"); return null; }
            rate = _ringRate;
            int want = (int)Math.Min((long)seconds * rate, Math.Min(_ringTotal, _ring.Length));
            snap = new short[want];
            int start = (_ringWrite - want + _ring.Length * 2) % _ring.Length;
            for (int i = 0; i < want; i++)
                snap[i] = _ring[(start + i) % _ring.Length];
        }
        try
        {
            string dir = RecordingsDir();
            Directory.CreateDirectory(dir);
            var snapState = _radio.Snapshot();
            string freq = (snapState.VfoHz / 1e6).ToString("0.000",
                System.Globalization.CultureInfo.InvariantCulture);
            string name = $"{DateTime.Now:yyyyMMdd-HHmmss}_replay{seconds}s_{freq}MHz_{Sanitize(snapState.Mode.ToString())}.wav";
            string path = Path.Combine(dir, name);
            using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            WriteWavHeader(f, rate);
            var bytes = new byte[snap.Length * 2];
            Buffer.BlockCopy(snap, 0, bytes, 0, bytes.Length);
            f.Write(bytes);
            PatchWavHeader(f, rate, bytes.Length);
            return name;
        }
        catch (Exception ex) { SetError(ex.Message); return null; }
    }

    /// <summary>Play a recording locally THROUGH THE RADIO: 48 k mono floats
    /// fed into the pipeline's monitor-audio queue, which mixes into the RX
    /// audio block — so playback reaches every sink RX audio reaches,
    /// including the radio's own speaker. (The popover's original browser
    /// &lt;audio&gt; played out the PI's audio device — connected to nothing
    /// on a G2.) Never touches MOX.</summary>
    public bool PlayLocalFile(string name)
    {
        string? path = SafePath(name);
        if (path is null || !File.Exists(path)) { SetError("no such recording"); return false; }
        float[] audio;
        double durSec;
        try { (audio, durSec) = LoadWavAs48kMono(path); }
        catch (Exception ex) { SetError($"cannot read WAV: {ex.Message}"); return false; }
        lock (_playLock)
        {
            _playCts?.Cancel();
            _playCts = new CancellationTokenSource();
            _playFile = Path.GetFileName(path);
            _playTotalSec = durSec;
            _playStartedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ct = _playCts.Token;
            _pipeline.SetMonitorSolo(true);   // hear the recording, not the band
            _ = Task.Run(() => PlayLocalPump(audio, ct));
        }
        return true;
    }

    public void PlayLocalStop()
    {
        lock (_playLock) _playCts?.Cancel();
    }

    public object PlayLocalStatus()
    {
        lock (_playLock)
        {
            bool playing = _playCts is not null;
            double elapsed = playing
                ? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _playStartedMs) / 1000.0
                : 0;
            return new
            {
                playing,
                fileName = playing ? _playFile : null,
                remainSec = playing ? Math.Max(0, _playTotalSec - elapsed) : 0,
            };
        }
    }

    private async Task PlayLocalPump(float[] audio, CancellationToken ct)
    {
        const int chunk = 4800;                   // 100 ms @ 48 k
        try
        {
            long t0 = Environment.TickCount64;
            int sent = 0;
            for (int off = 0; off < audio.Length && !ct.IsCancellationRequested; off += chunk)
            {
                int n = Math.Min(chunk, audio.Length - off);
                // Backlog-respecting: if the mixer queue is deep, yield a beat
                // rather than overflow it; if the enqueue is refused, retry
                // the same chunk after a short wait.
                while (!ct.IsCancellationRequested && _pipeline.MonitorBacklog > 48000 / 2)
                    await Task.Delay(50, CancellationToken.None);
                if (ct.IsCancellationRequested) break;
                if (!_pipeline.EnqueueMonitorAudio(audio.AsSpan(off, n)))
                {
                    await Task.Delay(50, CancellationToken.None);
                    off -= chunk;                 // retry this chunk
                    continue;
                }
                sent++;
                long due = t0 + sent * 100L;
                long wait = due - Environment.TickCount64;
                if (wait > 0) await Task.Delay((int)wait, CancellationToken.None);
            }
        }
        finally
        {
            _pipeline.SetMonitorSolo(false);
            lock (_playLock)
            {
                _playCts?.Dispose();
                _playCts = null;
                _playFile = "";
            }
        }
    }

    // ---- voice keyer: a recording, through the real TX chain, on the air ----

    public object KeyerStatus()
    {
        lock (_keyerLock)
        {
            bool playing = _keyerCts is not null;
            double elapsed = playing
                ? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _keyerStartedMs) / 1000.0
                : 0;
            return new
            {
                playing,
                fileName = playing ? _keyerFile : null,
                remainSec = playing ? Math.Max(0, _keyerTotalSec - elapsed) : 0,
            };
        }
    }

    /// <summary>Key MOX (MoxSource.Plugin — the slot documented for voice
    /// keyers), stream the named WAV through TxAudioIngest as 20 ms mic
    /// blocks, unkey at the end. Refuses if MOX is already keyed by anyone.</summary>
    public bool KeyerPlay(string name)
    {
        string? path = SafePath(name);
        if (path is null || !File.Exists(path)) { SetError("no such recording"); return false; }
        float[] audio;
        double durSec;
        try
        {
            (audio, durSec) = LoadWavAs48kMono(path);
        }
        catch (Exception ex) { SetError($"cannot read WAV: {ex.Message}"); return false; }
        lock (_keyerLock)
        {
            if (_keyerCts is not null) { SetError("keyer already playing"); return false; }
            if (_tx.MoxOwner is not null) { SetError("TX is already keyed"); return false; }
            if (!_tx.TrySetMox(true, MoxSource.Plugin, out var err))
            {
                SetError(err ?? "MOX refused");
                return false;
            }
            _keyerCts = new CancellationTokenSource();
            _keyerFile = Path.GetFileName(path);
            _keyerTotalSec = durSec;
            _keyerStartedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ct = _keyerCts.Token;
            _ = Task.Run(() => KeyerPump(audio, ct));
        }
        _log.LogInformation("keyer: transmitting {File} ({Sec:0.0}s)", name, durSec);
        return true;
    }

    public void KeyerStop()
    {
        lock (_keyerLock) _keyerCts?.Cancel();
    }

    private async Task KeyerPump(float[] audio, CancellationToken ct)
    {
        const int block = 960;                    // 20 ms @ 48 kHz — TxAudioIngest's unit
        var bytes = new byte[block * 4];
        try
        {
            long t0 = Environment.TickCount64;
            int sent = 0;
            for (int off = 0; off + block <= audio.Length && !ct.IsCancellationRequested; off += block)
            {
                Buffer.BlockCopy(audio, off * 4, bytes, 0, bytes.Length);
                _txIngest.OnMicPcmBytesFromWav(bytes);
                sent++;
                long due = t0 + sent * 20L;
                long wait = due - Environment.TickCount64;
                if (wait > 0) await Task.Delay((int)wait, CancellationToken.None);
            }
        }
        finally
        {
            lock (_keyerLock)
            {
                _keyerCts?.Dispose();
                _keyerCts = null;
                _keyerFile = "";
            }
            if (_tx.MoxOwner == MoxSource.Plugin)
                _tx.TrySetMox(false, MoxSource.Plugin, out _);
            _log.LogInformation("keyer: done, MOX released");
        }
    }

    private static (float[] audio, double durSec) LoadWavAs48kMono(string path)
    {
        byte[] all = File.ReadAllBytes(path);
        if (all.Length < 44) throw new InvalidDataException("too short");
        int rate = BitConverter.ToInt32(all, 24);
        short channels = BitConverter.ToInt16(all, 22);
        short bits = BitConverter.ToInt16(all, 34);
        if (bits != 16 || channels < 1) throw new InvalidDataException($"need 16-bit PCM (got {bits}-bit, {channels} ch)");
        int n = (all.Length - 44) / 2 / channels;
        var mono = new float[n];
        for (int i = 0; i < n; i++)
        {
            int acc = 0;
            for (int c = 0; c < channels; c++)
                acc += BitConverter.ToInt16(all, 44 + (i * channels + c) * 2);
            mono[i] = acc / (channels * 32768f);
        }
        if (rate == 48000) return (mono, n / 48000.0);
        // linear resample to the ingest rate
        int outN = (int)((long)n * 48000 / Math.Max(1, rate));
        var res = new float[outN];
        for (int i = 0; i < outN; i++)
        {
            double srcPos = (double)i * rate / 48000;
            int i0 = (int)srcPos;
            double frac = srcPos - i0;
            float a = mono[Math.Min(i0, n - 1)];
            float b = mono[Math.Min(i0 + 1, n - 1)];
            res[i] = (float)(a + (b - a) * frac);
        }
        return (res, outN / 48000.0);
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

    public void Dispose()
    {
        _pipeline.RxAudioAvailable -= OnRingBlock;
        PlayLocalStop();
        KeyerStop();
        Stop();
    }
}
