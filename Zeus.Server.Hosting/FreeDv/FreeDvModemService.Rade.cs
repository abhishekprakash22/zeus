// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// FreeDvModemService — RADE V1 / V2 path. RADE is not a freedv_open submode: it
// has its own engine (IRadeEngine: managed RadeSharp by default, the native
// zeus_rade shim as fallback — see RadeEngine.cs), complex modem IO and 16 kHz
// speech, so it
// runs beside the classic codec2 path rather than through it. Exactly one of
// _f (codec2) / _rade is open at a time; both are guarded by _state and obey
// the same realtime contract as the classic path (no allocation, no blocking,
// no throwing on ProcessRx / ProcessTx / DrainTx).
//
// SIGNAL PATH:
//   RX: 48 kHz demod audio → ÷6 → 8 kHz real → complex {re=x, im=0}
//       → engine Rx per nin() → 16 kHz PCM → ×3 → in-place block.
//   TX: 48 kHz mic → ÷3 → 16 kHz speech → engine Tx per n_speech frame
//       → 8 kHz complex modem → real part → ×6 → in-place block.
//       FinishTx() pads the last frame and appends the End-of-Over frame,
//       which on RADEV1 carries the callsign (first word of the TX text).
//       RADEV2 has no EOO callsign channel (rade_n_eoo_bits() == 0).
//
// LEVELS (freedv-gui 2.1.0 RADEReceiveStep / RADETransmitStep): RX feeds the
// modem short/32767 — i.e. Zeus's float audio as-is; TX scales the modem's
// real part by RADE_SCALING_FACTOR 16383 into shorts — 16383/32768 in floats.

using System.Runtime.InteropServices;

namespace Zeus.Server.Hosting.FreeDv;

public sealed partial class FreeDvModemService
{
    private const float RadeTxScale = 16383f / 32768f;

    private IRadeEngine? _rade;
    private int _radeNin;
    private readonly float[] _radeRxModemRing = new float[Ring8k];

    // Sized at open from the library's own geometry (control thread).
    private float[] _radeIqIn = Array.Empty<float>();    // 2 × nin_max
    private short[] _radePcmOut = Array.Empty<short>();  // max_pcm_per_rx
    private short[] _radeSpeechIn = Array.Empty<short>(); // n_speech
    private float[] _radeIqOut = Array.Empty<float>();   // 2 × max(n_tx_out, n_tx_eoo_out)
    private readonly byte[] _radeCallsign = new byte[16];

    // 16 kHz speech-side resamplers (the 8 kHz modem side reuses _rxDecim / _txInterp).
    private readonly Interpolator16To48 _radeRxInterp = new();
    private readonly Decimator48To16 _radeTxDecim = new();

    // Bumped once per decoded End-of-Over callsign (guarded by _state).
    private int _rxCallsignSeq;

    /// <summary>
    /// The last End-of-Over callsign decoded on RADEV1 (RADEV2 carries none), if one arrived after
    /// <paramref name="afterSeq"/>. Control-thread only (takes the state lock);
    /// used by the FreeDV Reporter to send rx_report once per heard over.
    /// </summary>
    public bool TryGetRxCallsign(int afterSeq, out int seq, out string callsign, out int snrDb)
    {
        lock (_state)
        {
            seq = _rxCallsignSeq;
            callsign = seq != afterSeq && _rade != null ? new string(_rxText, 0, _rxTextLen) : "";
            snrDb = (int)Math.Round(Interlocked.Read(ref _snrMilliDb) / 1000.0);
            return callsign.Length > 0;
        }
    }

    /// <summary>True when a RADE engine can run on this platform (always, with the managed engine).</summary>
    public static bool RadeAvailable => RadeEngines.Available;

    /// <summary>True for the RADE submodes (own engine and signal path).</summary>
    internal static bool IsRade(FreeDvSubmode s) => s is FreeDvSubmode.RadeV1 or FreeDvSubmode.RadeV2;

    /// <summary>Name of the open RADE engine, or null.</summary>
    public string? RadeEngineName => Volatile.Read(ref _rade)?.Name;

    private bool OpenRadeLocked(bool v2)
    {
        string label = v2 ? "RADEV2" : "RADEV1";
        IRadeEngine? z;
        string? error;
        try { z = RadeEngines.Open(v2, out error); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "freedv: RADE engine open threw");
            return false;
        }
        if (z == null)
        {
            _log.LogInformation("freedv: {Mode} selected but no RADE engine could be opened ({Error}) — modem idle", label, error);
            return false;
        }
        if (error != null) _log.LogWarning("freedv: {Error}; using {Engine}", error, z.Name);

        int nin = z.Nin;
        int ninMax = z.NinMax;
        int maxPcm = z.MaxPcmPerRx;
        int nSpeech = z.NSpeechSamples;
        int nTxOut = z.NTxOut;
        int nEoo = z.NTxEooOut;
        // The rings hold 32768 samples; one call's worth must fit several times
        // over or a steady stream would overflow them. max_pcm_per_rx is only
        // an upper bound (sizes the scratch buffer) — a steady decode yields
        // one frame group (~nSpeech) per call.
        if (nin <= 0 || ninMax < nin || ninMax > Ring8k / 4 || maxPcm <= 0 || maxPcm > Ring8k
            || nSpeech <= 0 || nSpeech > Ring8k / 4 || nTxOut <= 0 || nEoo <= 0
            || (nTxOut + nEoo) * 6 > Ring48k / 2)
        {
            _log.LogWarning(
                "freedv: {Mode} geometry unsupported (nin={Nin}/{NinMax} pcm={Pcm} nSpeech={NSpeech} txOut={TxOut} eoo={Eoo})",
                label, nin, ninMax, maxPcm, nSpeech, nTxOut, nEoo);
            z.Dispose();
            return false;
        }

        _radeIqIn = new float[2 * ninMax];
        _radePcmOut = new short[maxPcm];
        _radeSpeechIn = new short[nSpeech];
        _radeIqOut = new float[2 * Math.Max(nTxOut, nEoo)];

        _rade = z;
        _radeNin = nin;
        _nSpeech = nSpeech;           // RX priming threshold + TX frame size
        _speechRateHz = RadeNative.SpeechSampleRate;   // 16000, the zeus_rade.h / rade_api.h rates
        _modemRateHz = RadeNative.ModemSampleRate;     // 8000
        ApplyRadeCallsignLocked();

        FlushRxLocked();
        FlushTxLocked();
        _log.LogInformation(
            "freedv: opened {Mode} on {Engine} (nin={Nin}/{NinMax} nSpeech={NSpeech} txOut={TxOut} eoo={Eoo})",
            label, z.Name, nin, ninMax, nSpeech, nTxOut, nEoo);
        return true;
    }

    private void CloseRadeLocked()
    {
        if (_rade == null) return;
        var z = _rade;
        _rade = null;
        try { z.Dispose(); }
        catch (Exception ex) { _log.LogDebug(ex, "freedv: RADE engine close threw"); }
    }

    /// <summary>RADE's EOO carries a callsign, not free text: the first word of the TX text.</summary>
    private void ApplyRadeCallsignLocked()
    {
        if (_rade == null) return;
        var first = _txText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        _rade.SetTxCallsign(first.Length == 0 ? "" : first[0].ToUpperInvariant());
    }

    private void RadeProcessRxLocked(Span<float> block48k)
    {
        // 1) 48 kHz demod audio → 8 kHz modem floats (RADE takes floats, so
        //    its own ring; head/count are the classic modem ring's, unused
        //    while RADE is open).
        int off = 0;
        while (off < block48k.Length)
        {
            int chunk = Math.Min(block48k.Length - off, _scratch8.Length * 6);
            int n8 = _rxDecim.Process(block48k.Slice(off, chunk), _scratch8);
            for (int i = 0; i < n8; i++)
            {
                if (_rxModemCount >= Ring8k) break;   // overflow → drop newest
                _radeRxModemRing[(_rxModemHead + _rxModemCount) & (Ring8k - 1)] = _scratch8[i];
                _rxModemCount++;
            }
            off += chunk;
        }

        // 2) Demodulate/decode whole nin() chunks: real → complex {x, 0}.
        while (_radeNin > 0 && 2 * _radeNin <= _radeIqIn.Length && _rxModemCount >= _radeNin)
        {
            for (int i = 0; i < _radeNin; i++)
            {
                _radeIqIn[2 * i] = _radeRxModemRing[_rxModemHead];
                _radeIqIn[2 * i + 1] = 0f;
                _rxModemHead = (_rxModemHead + 1) & (Ring8k - 1);
            }
            _rxModemCount -= _radeNin;

            var rade = _rade!;
            int nout = rade.Rx(_radeIqIn, _radePcmOut);
            _radeNin = rade.Nin;

            bool syncedNow = rade.Sync;
            if (syncedNow && !_synced)
                Interlocked.Exchange(ref _lastSyncOrSwitchTicks, Environment.TickCount64);
            _synced = syncedNow;
            if (syncedNow)
                Interlocked.Exchange(ref _snrMilliDb, rade.SnrDb * 1000L);

            int csn = rade.GetEooCallsign(_radeCallsign);
            if (csn > 0)
            {
                // A decoded callsign replaces the RX text line (it is per over).
                int n = Math.Min(csn, RxTextCap);
                for (int i = 0; i < n; i++) _rxText[i] = (char)_radeCallsign[i];
                _rxTextLen = n;
                _rxCallsignSeq++;   // one per over — the shim consumes it on read
            }

            for (int i = 0; i < nout && i < _radePcmOut.Length; i++)
            {
                if (_rxSpeechCount >= Ring8k) break;
                _rxSpeechRing[(_rxSpeechHead + _rxSpeechCount) & (Ring8k - 1)] =
                    _radePcmOut[i] * (1f / 32768f);
                _rxSpeechCount++;
            }
        }

        // 3) 16 kHz speech → 48 kHz output ring, primed to one frame.
        if (!_rxPrimed && _rxSpeechCount >= _nSpeech) _rxPrimed = true;
        if (_rxPrimed)
        {
            Span<float> three = stackalloc float[3];
            while (_rxSpeechCount > 0 && _rxOutCount + 3 <= Ring48k)
            {
                float s = _rxSpeechRing[_rxSpeechHead];
                _rxSpeechHead = (_rxSpeechHead + 1) & (Ring8k - 1);
                _rxSpeechCount--;
                ReadOnlySpan<float> one = MemoryMarshal.CreateReadOnlySpan(ref s, 1);
                _radeRxInterp.Process(one, three);
                for (int i = 0; i < 3; i++)
                {
                    _rxOut48Ring[(_rxOutHead + _rxOutCount) & (Ring48k - 1)] = three[i];
                    _rxOutCount++;
                }
            }
        }

        // 4) Replace the block with decoded speech (silence on underrun).
        WriteRxOutLocked(block48k);
    }

    private void RadeProcessTxLocked(Span<float> block48k)
    {
        // 1) 48 kHz mic → 16 kHz speech shorts.
        int off = 0;
        while (off < block48k.Length)
        {
            int chunk = Math.Min(block48k.Length - off, _txScratch8.Length * 3);
            int n16 = _radeTxDecim.Process(block48k.Slice(off, chunk), _txScratch8);
            for (int i = 0; i < n16; i++)
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
        RadeEncodeQueuedSpeechLocked(padPartialFrame: false);

        // 3) Replace the mic block with modem audio.
        WriteTxOutLocked(block48k);
    }

    private int RadeFinishTxLocked()
    {
        RadeEncodeQueuedSpeechLocked(padPartialFrame: true);

        // End-of-Over: carries the callsign so the far end can show who it was.
        int n = _rade!.TxEoo(_radeIqOut);
        PushTxModemLocked(Math.Min(n, _radeIqOut.Length / 2));
        return _txOutCount;
    }

    private void RadeEncodeQueuedSpeechLocked(bool padPartialFrame)
    {
        int nSpeech = _radeSpeechIn.Length;
        if (nSpeech <= 0) return;

        if (padPartialFrame && _txSpeechCount > 0 && _txSpeechCount < nSpeech)
        {
            int pad = nSpeech - _txSpeechCount;
            for (int i = 0; i < pad && _txSpeechCount < Ring8k; i++)
            {
                _txSpeechRing[(_txSpeechHead + _txSpeechCount) & (Ring8k - 1)] = 0;
                _txSpeechCount++;
            }
        }

        while (_txSpeechCount >= nSpeech)
        {
            for (int i = 0; i < nSpeech; i++)
            {
                _radeSpeechIn[i] = _txSpeechRing[_txSpeechHead];
                _txSpeechHead = (_txSpeechHead + 1) & (Ring8k - 1);
            }
            _txSpeechCount -= nSpeech;

            int n = _rade!.Tx(_radeSpeechIn, _radeIqOut);
            if (!PushTxModemLocked(Math.Min(n, _radeIqOut.Length / 2))) return;
        }
    }

    /// <summary>Real part of <paramref name="n"/> modem samples → ×6 → TX ring. False when the ring filled.</summary>
    private bool PushTxModemLocked(int n)
    {
        Span<float> six = stackalloc float[6];
        for (int i = 0; i < n; i++)
        {
            if (_txOutCount + 6 > Ring48k) return false;   // backlog full → drop
            float s = _radeIqOut[2 * i] * RadeTxScale;
            ReadOnlySpan<float> one = MemoryMarshal.CreateReadOnlySpan(ref s, 1);
            _txInterp.Process(one, six);
            for (int k = 0; k < 6; k++)
            {
                _txOut48Ring[(_txOutHead + _txOutCount) & (Ring48k - 1)] = six[k];
                _txOutCount++;
            }
        }
        return true;
    }
}
