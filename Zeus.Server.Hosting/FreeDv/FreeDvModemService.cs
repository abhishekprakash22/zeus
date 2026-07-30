// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// FreeDvModemService — the FreeDV digital-voice modem, IN CORE.
//
// WHY IN CORE, NOT A PLUGIN
// Same story as Zeus.Server.Hosting/Digital: upstream planned FreeDV as the
// org.openhpsdr.freedv backend plugin, distributed from a registry that no
// longer exists. The host side of the seam was fully built and shipped —
// IAudioModemPlugin, AudioModemPluginBridge, the DspPipelineService RX insert,
// the TxAudioIngest mic insert + end-of-over tail drain, the RadioService mode
// gate and FreeDV sideband convention — but the modem itself never landed.
// This class is that modem. It implements IAudioModemPlugin exactly as a
// plugin would and is published to the same hot paths through
// AudioModemPluginBridge's core-modem fallback, so every host-side behaviour
// (tail drain, USB fallback, filter conventions) is unchanged. A real
// org.openhpsdr.freedv plugin, if ever installed, takes precedence.
//
// SIGNAL PATH (700D/700E — 8 kHz speech, 8 kHz modem, both sides of
// freedv_api):
//   RX: 48 kHz demod audio → ÷6 decimate → short ring → freedv_rx() per
//       nin() chunk → decoded speech ring → ×6 interpolate → in-place block.
//       Silence until the speech ring primes (one codec frame), silence again
//       on underrun — matches "silence until sync".
//   TX: 48 kHz mic → ÷6 decimate → speech ring → freedv_tx() per
//       n_speech_samples frame → modem ring → ×6 interpolate → in-place block
//       (WDSP's USB TXA then SSB-modulates the modem audio). FinishTx() pads
//       the residual to a whole frame so the tail drain puts complete OFDM
//       frames on air.
//
// REALTIME CONTRACT: ProcessRx / ProcessTx / DrainTx run on audio-sensitive
// threads and must not allocate, block, or throw. All buffers are
// preallocated; the shared state lock is taken with Monitor.TryEnter(0) on
// those paths — if a control-thread reconfiguration holds it, the block is
// zeroed (RX/TX silence for one tick) rather than blocking the audio thread.
// Control paths (config, auto-detect timer, lifecycle) take the same lock
// blocking. Sync/SNR are cached to volatiles on the audio thread after each
// freedv_rx so /status polling never touches the native object.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Server.Hosting.FreeDv;

/// <summary>
/// FreeDV submode wire values. Order is the persisted/wire byte order and
/// mirrors zeus-web's FREEDV_SUBMODE_BY_BYTE — append-only, never reorder.
/// </summary>
public enum FreeDvSubmode : byte
{
    Mode700D = 0,
    Mode700E = 1,
    Mode700C = 2,
    Mode1600 = 3,
    Mode800XA = 4,
    RadeV1 = 5,
}

/// <summary>Immutable status snapshot for the REST surface.</summary>
public sealed record FreeDvModemStatus(
    bool NativeAvailable,
    bool Active,
    FreeDvSubmode Submode,
    bool Synced,
    double SnrDb,
    bool SquelchEnabled,
    double SnrSquelchThreshDb,
    int SpeechSampleRateHz,
    int ModemSampleRateHz,
    string? RxText,
    string? TxText,
    string? LibraryVersion,
    bool AutoDetect,
    bool RadeAvailable);

public sealed unsafe class FreeDvModemService : IAudioModemPlugin, IHostedService, IDisposable
{
    // Ring sizes (powers of two). 8 kHz shorts: 4 s of audio; 48 kHz floats:
    // ~0.68 s — comfortably above the largest 700D frame + tail backlog.
    private const int Ring8k = 32768;
    private const int Ring48k = 32768;
    private const int Scratch8k = 4096;      // ≥ n_max_modem_samples for every mode
    private const int RxTextCap = 512;
    private const int AutoDetectDwellMs = 4000;

    private static readonly FreeDvSubmode[] AutoScanSet =
    {
        FreeDvSubmode.Mode700D, FreeDvSubmode.Mode700E, FreeDvSubmode.Mode1600,
        FreeDvSubmode.Mode700C, FreeDvSubmode.Mode800XA,
    };

    private readonly ILogger<FreeDvModemService> _log;
    private readonly FreeDvSettingsStore _store;
    private readonly object _state = new();

    // Modem instance + geometry (valid while _f != 0, guarded by _state).
    private IntPtr _f;
    private int _nin;
    private int _nSpeech;
    private int _speechRateHz = 8000;
    private int _modemRateHz = 8000;

    // Resamplers (guarded by _state).
    private readonly Decimator48To8 _rxDecim = new();
    private readonly Interpolator8To48 _rxInterp = new();
    private readonly Decimator48To8 _txDecim = new();
    private readonly Interpolator8To48 _txInterp = new();

    // RX rings/scratch (guarded by _state).
    private readonly short[] _rxModemRing = new short[Ring8k];
    private int _rxModemHead, _rxModemCount;
    private readonly float[] _rxSpeechRing = new float[Ring8k];
    private int _rxSpeechHead, _rxSpeechCount;
    private readonly float[] _rxOut48Ring = new float[Ring48k];
    private int _rxOutHead, _rxOutCount;
    private bool _rxPrimed;
    private readonly short[] _demodIn = new short[Scratch8k];
    private readonly short[] _speechOut = new short[Scratch8k];
    private readonly float[] _scratch8 = new float[Scratch8k];

    // TX rings/scratch (guarded by _state).
    private readonly short[] _txSpeechRing = new short[Ring8k];
    private int _txSpeechHead, _txSpeechCount;
    private readonly float[] _txOut48Ring = new float[Ring48k];
    private int _txOutHead, _txOutCount;
    private readonly short[] _speechIn = new short[Scratch8k];
    private readonly short[] _modOut = new short[Scratch8k];
    private readonly float[] _txScratch8 = new float[Scratch8k];

    // Config (volatile-published; writes under _state).
    private volatile bool _engaged;
    private int _submode = (int)FreeDvSubmode.Mode700D;
    private volatile bool _autoDetect;
    private volatile bool _squelchEnabled = true;
    private double _squelchThreshDb = -2.0;
    private volatile bool _pendingRxFlush;
    private volatile bool _pendingTxFlush;

    // Telemetry cached off the audio thread.
    private volatile bool _synced;
    private long _snrMilliDb;                     // Interlocked
    private long _lastSyncOrSwitchTicks;          // Environment.TickCount64

    // TX text sidechannel: snapshot swapped atomically; callback walks it.
    private byte[] _txTextBytes = "\r"u8.ToArray();
    private string _txText = "";
    private int _txTextIdx;

    // RX text sidechannel: bounded, appended from the native rx callback
    // (which runs inside freedv_rx on the audio thread — no allocation).
    private readonly char[] _rxText = new char[RxTextCap];
    private int _rxTextLen;

    private Timer? _autoDetectTimer;

    // The native txt callbacks carry a void* state we ignore in favour of a
    // static instance — one core modem exists per process by construction.
    private static FreeDvModemService? _instance;

    public FreeDvModemService(ILogger<FreeDvModemService> log, FreeDvSettingsStore store)
    {
        _log = log;
        _store = store;
        _instance = this;
    }

    // ---- IAudioModemPlugin --------------------------------------------------

    public bool NativeAvailable => FreeDvNative.Available;

    /// <summary>
    /// Engaged (host mode is FREEDV) with a runnable codec. RadeV1 keeps the
    /// modem inactive until librade lands — the RX path then passes modem
    /// audio through untouched and the panel shows its gated state.
    /// </summary>
    public bool Active => _engaged && Volatile.Read(ref _f) != IntPtr.Zero;

    public void SyncMode(byte rxModeByte)
    {
        bool engage = rxModeByte == (byte)RxMode.FreeDv;
        if (engage == _engaged) return;
        _engaged = engage;
        // Rings are cleared by the audio thread at its next tick (it already
        // owns the TryEnter) — never open/close the codec from here: SyncMode
        // is called per-block on the RX hot path.
        _pendingRxFlush = true;
        _pendingTxFlush = true;
        Interlocked.Exchange(ref _lastSyncOrSwitchTicks, Environment.TickCount64);
    }

    public void ProcessRx(Span<float> block48k)
    {
        if (block48k.IsEmpty) return;
        if (!Monitor.TryEnter(_state)) { block48k.Clear(); return; }
        try
        {
            if (_f == IntPtr.Zero) { block48k.Clear(); return; }
            if (_pendingRxFlush) { FlushRxLocked(); _pendingRxFlush = false; }

            // 1) 48 kHz demod audio → 8 kHz modem shorts into the ring.
            int off = 0;
            while (off < block48k.Length)
            {
                int chunk = Math.Min(block48k.Length - off, _scratch8.Length * 6);
                int n8 = _rxDecim.Process(block48k.Slice(off, chunk), _scratch8);
                for (int i = 0; i < n8; i++)
                {
                    if (_rxModemCount >= Ring8k) break;   // overflow → drop newest
                    float v = _scratch8[i] * 32767f;
                    _rxModemRing[(_rxModemHead + _rxModemCount) & (Ring8k - 1)] =
                        v >= 32767f ? (short)32767 : v <= -32768f ? (short)-32768 : (short)v;
                    _rxModemCount++;
                }
                off += chunk;
            }

            // 2) Demodulate/decode whole nin() chunks.
            while (_rxModemCount >= _nin && _nin > 0 && _nin <= _demodIn.Length)
            {
                for (int i = 0; i < _nin; i++)
                {
                    _demodIn[i] = _rxModemRing[_rxModemHead];
                    _rxModemHead = (_rxModemHead + 1) & (Ring8k - 1);
                }
                _rxModemCount -= _nin;

                int nout;
                fixed (short* pOut = _speechOut)
                fixed (short* pIn = _demodIn)
                    nout = FreeDvNative.Rx(_f, pOut, pIn);
                _nin = FreeDvNative.Nin(_f);              // varies with timing slew

                FreeDvNative.GetModemStats(_f, out int sync, out float snr);
                bool syncedNow = sync != 0;
                if (syncedNow && !_synced)
                    Interlocked.Exchange(ref _lastSyncOrSwitchTicks, Environment.TickCount64);
                _synced = syncedNow;
                if (float.IsFinite(snr))
                    Interlocked.Exchange(ref _snrMilliDb, (long)(snr * 1000));

                for (int i = 0; i < nout; i++)
                {
                    if (_rxSpeechCount >= Ring8k) break;
                    _rxSpeechRing[(_rxSpeechHead + _rxSpeechCount) & (Ring8k - 1)] =
                        _speechOut[i] * (1f / 32768f);
                    _rxSpeechCount++;
                }
            }

            // 3) Speech → 48 kHz output ring (primed to one codec frame so a
            //    steady decode never underruns between blocks).
            if (!_rxPrimed && _rxSpeechCount >= _nSpeech) _rxPrimed = true;
            if (_rxPrimed)
            {
                Span<float> six = stackalloc float[6];
                while (_rxSpeechCount > 0 && _rxOutCount + 6 <= Ring48k)
                {
                    float s = _rxSpeechRing[_rxSpeechHead];
                    _rxSpeechHead = (_rxSpeechHead + 1) & (Ring8k - 1);
                    _rxSpeechCount--;
                    ReadOnlySpan<float> one = MemoryMarshal.CreateReadOnlySpan(ref s, 1);
                    _rxInterp.Process(one, six);
                    for (int i = 0; i < 6; i++)
                    {
                        _rxOut48Ring[(_rxOutHead + _rxOutCount) & (Ring48k - 1)] = six[i];
                        _rxOutCount++;
                    }
                }
            }

            // 4) Replace the block with decoded speech (silence on underrun).
            int have = Math.Min(_rxOutCount, block48k.Length);
            for (int i = 0; i < have; i++)
            {
                block48k[i] = _rxOut48Ring[_rxOutHead];
                _rxOutHead = (_rxOutHead + 1) & (Ring48k - 1);
            }
            _rxOutCount -= have;
            if (have < block48k.Length)
            {
                block48k.Slice(have).Clear();
                if (_rxOutCount == 0 && _rxSpeechCount < _nSpeech) _rxPrimed = false;
            }
        }
        finally { Monitor.Exit(_state); }
    }

    public void ProcessTx(Span<float> block48k)
    {
        if (block48k.IsEmpty) return;
        if (!Monitor.TryEnter(_state)) { block48k.Clear(); return; }
        try
        {
            if (_f == IntPtr.Zero) { block48k.Clear(); return; }
            if (_pendingTxFlush) { FlushTxLocked(); _pendingTxFlush = false; }

            // 1) 48 kHz mic → 8 kHz speech shorts.
            int off = 0;
            while (off < block48k.Length)
            {
                int chunk = Math.Min(block48k.Length - off, _txScratch8.Length * 6);
                int n8 = _txDecim.Process(block48k.Slice(off, chunk), _txScratch8);
                for (int i = 0; i < n8; i++)
                {
                    if (_txSpeechCount >= Ring8k) break;
                    float v = _txScratch8[i] * 32767f;
                    _txSpeechRing[(_txSpeechHead + _txSpeechCount) & (Ring8k - 1)] =
                        v >= 32767f ? (short)32767 : v <= -32768f ? (short)-32768 : (short)v;
                    _txSpeechCount++;
                }
                off += chunk;
            }

            // 2) Whole speech frames → modem audio → 48 kHz ring.
            EncodeQueuedSpeechLocked(padPartialFrame: false);

            // 3) Replace the mic block with modem audio (zeros while the first
            //    frame is still filling — WDSP just modulates silence).
            int have = Math.Min(_txOutCount, block48k.Length);
            for (int i = 0; i < have; i++)
            {
                block48k[i] = _txOut48Ring[_txOutHead];
                _txOutHead = (_txOutHead + 1) & (Ring48k - 1);
            }
            _txOutCount -= have;
            if (have < block48k.Length) block48k.Slice(have).Clear();
        }
        finally { Monitor.Exit(_state); }
    }

    public void FlushRx()
    {
        // May race audio callbacks; defer to the owning thread when contended.
        if (!Monitor.TryEnter(_state)) { _pendingRxFlush = true; return; }
        try { FlushRxLocked(); }
        finally { Monitor.Exit(_state); }
    }

    public void FlushTx()
    {
        if (!Monitor.TryEnter(_state)) { _pendingTxFlush = true; return; }
        try { FlushTxLocked(); }
        finally { Monitor.Exit(_state); }
    }

    public int FinishTx()
    {
        // Un-key (API) thread — blocking is fine; the mic hot path is already
        // parked by TxAudioIngest's _tailDraining handoff.
        lock (_state)
        {
            if (_f == IntPtr.Zero) return 0;
            EncodeQueuedSpeechLocked(padPartialFrame: true);
            return _txOutCount;
        }
    }

    public int DrainTx(Span<float> block48k)
    {
        if (block48k.IsEmpty) return 0;
        if (!Monitor.TryEnter(_state)) { block48k.Clear(); return 0; }
        try
        {
            int have = Math.Min(_txOutCount, block48k.Length);
            for (int i = 0; i < have; i++)
            {
                block48k[i] = _txOut48Ring[_txOutHead];
                _txOutHead = (_txOutHead + 1) & (Ring48k - 1);
            }
            _txOutCount -= have;
            if (have < block48k.Length) block48k.Slice(have).Clear();
            return have;
        }
        finally { Monitor.Exit(_state); }
    }

    // ---- control surface ----------------------------------------------------

    public FreeDvModemStatus Snapshot()
    {
        var sub = (FreeDvSubmode)Volatile.Read(ref _submode);
        string rxText;
        lock (_state) rxText = new string(_rxText, 0, _rxTextLen);
        return new FreeDvModemStatus(
            NativeAvailable: NativeAvailable,
            Active: Active,
            Submode: sub,
            Synced: _synced,
            SnrDb: Interlocked.Read(ref _snrMilliDb) / 1000.0,
            SquelchEnabled: _squelchEnabled,
            SnrSquelchThreshDb: Volatile.Read(ref _squelchThreshDb),
            SpeechSampleRateHz: _speechRateHz,
            ModemSampleRateHz: _modemRateHz,
            RxText: rxText.Length == 0 ? null : rxText,
            TxText: _txText.Length == 0 ? null : _txText,
            LibraryVersion: FreeDvNative.ApiVersion is int v
                ? $"libcodec2 1.2.0 (freedv_api v{v})" : null,
            AutoDetect: _autoDetect,
            RadeAvailable: false);
    }

    public FreeDvModemStatus Configure(
        FreeDvSubmode? submode, bool? autoDetect, bool? squelchEnabled,
        double? snrSquelchThreshDb, string? txText)
    {
        lock (_state)
        {
            if (txText is not null) SetTxTextLocked(txText);
            if (autoDetect is bool ad) _autoDetect = ad;
            if (squelchEnabled is bool sq) _squelchEnabled = sq;
            if (snrSquelchThreshDb is double th)
                Volatile.Write(ref _squelchThreshDb, Math.Clamp(th, -5.0, 15.0));

            if (submode is FreeDvSubmode sm && (int)sm != _submode)
            {
                Volatile.Write(ref _submode, (int)sm);
                ReopenLocked();
                Interlocked.Exchange(ref _lastSyncOrSwitchTicks, Environment.TickCount64);
            }
            else if (_f != IntPtr.Zero)
            {
                ApplySquelchLocked();
            }
        }
        Persist();
        return Snapshot();
    }

    // ---- lifecycle ----------------------------------------------------------

    public Task StartAsync(CancellationToken ct)
    {
        var s = _store.GetModem();
        lock (_state)
        {
            Volatile.Write(ref _submode, (int)s.Submode);
            _autoDetect = s.AutoDetect;
            _squelchEnabled = s.SquelchEnabled;
            Volatile.Write(ref _squelchThreshDb, s.SnrSquelchThreshDb);
            SetTxTextLocked(s.TxText);
            if (NativeAvailable) ReopenLocked();
        }
        _log.LogInformation(
            "freedv: modem in core (native={Native}, submode={Submode}, api={Api})",
            NativeAvailable, (FreeDvSubmode)_submode, FreeDvNative.ApiVersion);
        _autoDetectTimer = new Timer(AutoDetectTick, null, 500, 500);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _autoDetectTimer?.Dispose();
        _autoDetectTimer = null;
        lock (_state) CloseLocked();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _autoDetectTimer?.Dispose();
        lock (_state) CloseLocked();
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    // ---- internals (all *Locked members require _state) --------------------

    private void EncodeQueuedSpeechLocked(bool padPartialFrame)
    {
        if (_nSpeech <= 0 || _nSpeech > _speechIn.Length) return;

        if (padPartialFrame && _txSpeechCount > 0 && _txSpeechCount < _nSpeech)
        {
            int pad = _nSpeech - _txSpeechCount;
            for (int i = 0; i < pad && _txSpeechCount < Ring8k; i++)
            {
                _txSpeechRing[(_txSpeechHead + _txSpeechCount) & (Ring8k - 1)] = 0;
                _txSpeechCount++;
            }
        }

        Span<float> six = stackalloc float[6];
        while (_txSpeechCount >= _nSpeech)
        {
            for (int i = 0; i < _nSpeech; i++)
            {
                _speechIn[i] = _txSpeechRing[_txSpeechHead];
                _txSpeechHead = (_txSpeechHead + 1) & (Ring8k - 1);
            }
            _txSpeechCount -= _nSpeech;

            int nMod = FreeDvNative.NNomModemSamples(_f);
            if (nMod <= 0 || nMod > _modOut.Length) return;
            fixed (short* pMod = _modOut)
            fixed (short* pSpeech = _speechIn)
                FreeDvNative.Tx(_f, pMod, pSpeech);

            for (int i = 0; i < nMod; i++)
            {
                if (_txOutCount + 6 > Ring48k) return;    // backlog full → drop
                float s = _modOut[i] * (1f / 32768f);
                ReadOnlySpan<float> one = MemoryMarshal.CreateReadOnlySpan(ref s, 1);
                _txInterp.Process(one, six);
                for (int k = 0; k < 6; k++)
                {
                    _txOut48Ring[(_txOutHead + _txOutCount) & (Ring48k - 1)] = six[k];
                    _txOutCount++;
                }
            }
        }
    }

    private void FlushRxLocked()
    {
        _rxModemHead = _rxModemCount = 0;
        _rxSpeechHead = _rxSpeechCount = 0;
        _rxOutHead = _rxOutCount = 0;
        _rxPrimed = false;
        _rxDecim.Reset();
        _rxInterp.Reset();
        _rxTextLen = 0;
        _synced = false;
        Interlocked.Exchange(ref _snrMilliDb, 0);
    }

    private void FlushTxLocked()
    {
        _txSpeechHead = _txSpeechCount = 0;
        _txOutHead = _txOutCount = 0;
        _txDecim.Reset();
        _txInterp.Reset();
        _txTextIdx = 0;
    }

    private void ReopenLocked()
    {
        CloseLocked();
        var sub = (FreeDvSubmode)_submode;
        if (!NativeAvailable) return;
        if (sub == FreeDvSubmode.RadeV1)
        {
            // librade is not integrated yet (native/radae is scaffold-only).
            // Leave the codec closed: Active stays false, the pipeline skips
            // ProcessRx (audio passes through) and the panel shows the gate.
            _log.LogInformation("freedv: RADEV1 selected but librade is not integrated — modem idle");
            return;
        }

        int mode = sub switch
        {
            FreeDvSubmode.Mode700D => FreeDvNative.Mode700D,
            FreeDvSubmode.Mode700E => FreeDvNative.Mode700E,
            FreeDvSubmode.Mode700C => FreeDvNative.Mode700C,
            FreeDvSubmode.Mode1600 => FreeDvNative.Mode1600,
            FreeDvSubmode.Mode800XA => FreeDvNative.Mode800Xa,
            _ => FreeDvNative.Mode700D,
        };

        IntPtr f;
        try { f = FreeDvNative.Open(mode); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "freedv: freedv_open({Mode}) threw", sub);
            return;
        }
        if (f == IntPtr.Zero)
        {
            _log.LogWarning("freedv: freedv_open({Mode}) returned NULL", sub);
            return;
        }

        int nin = FreeDvNative.Nin(f);
        int nSpeech = FreeDvNative.NSpeechSamples(f);
        int nMax = FreeDvNative.NMaxModemSamples(f);
        int speechRate = FreeDvNative.SpeechSampleRate(f);
        int modemRate = FreeDvNative.ModemSampleRate(f);
        if (nin <= 0 || nMax > Scratch8k || nSpeech <= 0 || nSpeech > Scratch8k
            || speechRate != 8000 || modemRate != 8000)
        {
            // The fixed 6:1 resamplers assume 8 kHz on both sides — true for
            // every classic mode Zeus targets. Refuse anything else outright
            // rather than transmit at the wrong rate.
            _log.LogWarning(
                "freedv: {Mode} geometry unsupported (nin={Nin} nSpeech={NSpeech} nMax={NMax} speech={SpeechHz} modem={ModemHz})",
                sub, nin, nSpeech, nMax, speechRate, modemRate);
            FreeDvNative.Close(f);
            return;
        }

        _f = f;
        _nin = nin;
        _nSpeech = nSpeech;
        _speechRateHz = speechRate;
        _modemRateHz = modemRate;

        // TX clipping + BPF for the OFDM modes — freedv-gui's on-air defaults;
        // tames PAPR so the leveler/PA see a civilised crest factor. No-ops
        // for 1600/800XA on the codec2 side.
        bool ofdm = sub is FreeDvSubmode.Mode700C or FreeDvSubmode.Mode700D or FreeDvSubmode.Mode700E;
        FreeDvNative.SetClip(_f, ofdm);
        FreeDvNative.SetTxBpf(_f, ofdm);
        ApplySquelchLocked();
        FreeDvNative.SetCallbackTxt(_f, RxTxtCallbackPtr, TxTxtCallbackPtr, IntPtr.Zero);

        FlushRxLocked();
        FlushTxLocked();
        _log.LogInformation(
            "freedv: opened {Mode} (nin={Nin} nSpeech={NSpeech} nMax={NMax})",
            sub, nin, nSpeech, nMax);
    }

    private void CloseLocked()
    {
        if (_f == IntPtr.Zero) return;
        var f = _f;
        _f = IntPtr.Zero;
        try { FreeDvNative.Close(f); }
        catch (Exception ex) { _log.LogDebug(ex, "freedv: freedv_close threw"); }
        _synced = false;
        Interlocked.Exchange(ref _snrMilliDb, 0);
    }

    private void ApplySquelchLocked()
    {
        if (_f == IntPtr.Zero) return;
        FreeDvNative.SetSquelchEn(_f, _squelchEnabled);
        FreeDvNative.SetSnrSquelchThresh(_f, (float)Volatile.Read(ref _squelchThreshDb));
    }

    private void SetTxTextLocked(string txText)
    {
        var trimmed = (txText ?? "").Trim();
        if (trimmed.Length > 79) trimmed = trimmed[..79];
        _txText = trimmed;
        // FreeDV convention: text loops continuously, overs separated by CR.
        _txTextBytes = System.Text.Encoding.ASCII.GetBytes(
            trimmed.Length == 0 ? "\r" : trimmed + "\r");
        _txTextIdx = 0;
    }

    private void Persist()
    {
        try
        {
            _store.SetModem(new FreeDvModemSettings(
                (FreeDvSubmode)_submode, _autoDetect, _squelchEnabled,
                Volatile.Read(ref _squelchThreshDb), _txText));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "freedv: could not persist modem settings");
        }
    }

    private void AutoDetectTick(object? _)
    {
        if (!_autoDetect || !_engaged || _synced) return;
        long last = Interlocked.Read(ref _lastSyncOrSwitchTicks);
        if (Environment.TickCount64 - last < AutoDetectDwellMs) return;

        lock (_state)
        {
            if (!_autoDetect || !_engaged || _synced) return;
            var current = (FreeDvSubmode)_submode;
            int idx = Array.IndexOf(AutoScanSet, current);
            var next = AutoScanSet[(idx + 1 + AutoScanSet.Length) % AutoScanSet.Length];
            Volatile.Write(ref _submode, (int)next);
            ReopenLocked();
            Interlocked.Exchange(ref _lastSyncOrSwitchTicks, Environment.TickCount64);
            _log.LogInformation("freedv: auto-detect scanning → {Mode}", next);
        }
    }

    // ---- native txt callbacks (run inside freedv_rx/tx on the audio thread —
    //      no allocation, no locks: the caller already holds _state) ---------

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void RxTxtCallback(IntPtr state, sbyte c)
    {
        var self = _instance;
        if (self is null) return;
        char ch = (char)(byte)c;
        if (ch == '\r' || ch == '\n') { self._rxTextLen = 0; return; }
        if (ch < ' ' || ch > '~') return;
        if (self._rxTextLen >= RxTextCap)
        {
            Array.Copy(self._rxText, 1, self._rxText, 0, RxTextCap - 1);
            self._rxTextLen = RxTextCap - 1;
        }
        self._rxText[self._rxTextLen++] = ch;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static sbyte TxTxtCallback(IntPtr state)
    {
        var self = _instance;
        if (self is null) return (sbyte)'\r';
        var bytes = self._txTextBytes;
        if (self._txTextIdx >= bytes.Length) self._txTextIdx = 0;
        return (sbyte)bytes[self._txTextIdx++];
    }

    private static IntPtr RxTxtCallbackPtr
    {
        get { unsafe { return (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, sbyte, void>)&RxTxtCallback; } }
    }

    private static IntPtr TxTxtCallbackPtr
    {
        get { unsafe { return (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, sbyte>)&TxTxtCallback; } }
    }
}
