// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// RadeNative — bindings for the zeus_rade shim (native/radae/shim/zeus_rade.h):
// RADE V1 (radae_c) + the Opus FARGAN vocoder + LPCNet analyzer behind one
// streaming surface. The library ships in Zeus.Dsp/runtimes/{rid}/native beside
// libcodec2 (libzeus_rade.dylib / libzeus_rade.so / zeus_rade.dll), built by
// .github/workflows/build-rade.yml. Weights are compiled in — no model files.
//
// LOADING: same NativeLibrary.TryLoad + GetExport pattern as FreeDvNative, for
// the same reason (this assembly's single DllImport resolver is already
// claimed). A missing library is NOT fatal: Available goes false and RADEV1
// stays gated in the panel exactly as before.
//
// ABI: RADE_COMP is { float real; float imag; } — passed as an interleaved
// float* (re, im, re, im, ...). Modem IQ is 8 kHz, speech PCM is int16 @ 16 kHz.

using System.Runtime.InteropServices;

namespace Zeus.Server.Hosting.FreeDv;

internal static unsafe class RadeNative
{
    public const int ModemSampleRate = 8000;
    public const int SpeechSampleRate = 16000;

    private static readonly object LoadLock = new();
    private static bool _probed;
    private static bool _available;
    private static IntPtr _lib;

    // ---- bound entry points (valid iff Available) ---------------------------
    private static delegate* unmanaged[Cdecl]<void> _globalInit;
    private static delegate* unmanaged[Cdecl]<IntPtr> _open;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _close;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _nin;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _ninMax;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _maxPcmPerRx;
    private static delegate* unmanaged[Cdecl]<IntPtr, float*, short*, int> _rx;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _nSpeechSamples;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _nTxOut;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _nTxEooOut;
    private static delegate* unmanaged[Cdecl]<IntPtr, short*, float*, int> _tx;
    private static delegate* unmanaged[Cdecl]<IntPtr, float*, int> _txEoo;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, void> _setTxCallsign;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _sync;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _snrDb;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, int> _getEooCallsign;

    /// <summary>
    /// True when libzeus_rade loaded, every export bound, and the library's
    /// one-time global init ran.
    /// </summary>
    public static bool Available
    {
        get
        {
            lock (LoadLock)
            {
                if (_probed) return _available;
                _probed = true;
                _available = TryBind();
                return _available;
            }
        }
    }

    public static IntPtr Open() => _open();
    public static void Close(IntPtr z) => _close(z);
    public static int Nin(IntPtr z) => _nin(z);
    public static int NinMax(IntPtr z) => _ninMax(z);
    public static int MaxPcmPerRx(IntPtr z) => _maxPcmPerRx(z);
    /// <summary>iqIn: Nin(z) interleaved complex samples; returns int16 PCM written.</summary>
    public static int Rx(IntPtr z, float* iqIn, short* pcmOut) => _rx(z, iqIn, pcmOut);
    public static int NSpeechSamples(IntPtr z) => _nSpeechSamples(z);
    public static int NTxOut(IntPtr z) => _nTxOut(z);
    public static int NTxEooOut(IntPtr z) => _nTxEooOut(z);
    /// <summary>pcmIn: NSpeechSamples(z) int16; returns complex samples written to iqOut.</summary>
    public static int Tx(IntPtr z, short* pcmIn, float* iqOut) => _tx(z, pcmIn, iqOut);
    public static int TxEoo(IntPtr z, float* iqOut) => _txEoo(z, iqOut);
    public static int Sync(IntPtr z) => _sync(z);
    public static int SnrDb(IntPtr z) => _snrDb(z);

    /// <summary>Callsign carried in the End-of-Over frame (≤ 8 ASCII chars; empty clears).</summary>
    public static void SetTxCallsign(IntPtr z, string callsign)
    {
        Span<byte> buf = stackalloc byte[9];
        int n = 0;
        foreach (char c in callsign)
        {
            if (n == 8) break;
            if (c > ' ' && c <= '~') buf[n++] = (byte)c;
        }
        buf[n] = 0;
        fixed (byte* p = buf) _setTxCallsign(z, p);
    }

    /// <summary>
    /// Copies the last decoded EOO callsign into <paramref name="dest"/>
    /// (≥ 9 bytes). Returns chars written, 0 if none since the last over.
    /// Allocation-free — safe on the audio thread.
    /// </summary>
    public static int GetEooCallsign(IntPtr z, Span<byte> dest)
    {
        if (dest.Length < 9) return 0;
        fixed (byte* p = dest) return _getEooCallsign(z, p);
    }

    // ---- load ---------------------------------------------------------------

    private static bool TryBind()
    {
        try
        {
            foreach (var candidate in Candidates())
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out _lib))
                    break;
            }
            if (_lib == IntPtr.Zero && !NativeLibrary.TryLoad(FileName(), out _lib))
                return false;

            _globalInit = (delegate* unmanaged[Cdecl]<void>)Export("zeus_rade_global_init");
            _open = (delegate* unmanaged[Cdecl]<IntPtr>)Export("zeus_rade_open");
            _close = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("zeus_rade_close");
            _nin = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("zeus_rade_nin");
            _ninMax = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("zeus_rade_nin_max");
            _maxPcmPerRx = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("zeus_rade_max_pcm_per_rx");
            _rx = (delegate* unmanaged[Cdecl]<IntPtr, float*, short*, int>)Export("zeus_rade_rx");
            _nSpeechSamples = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("zeus_rade_n_speech_samples");
            _nTxOut = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("zeus_rade_n_tx_out");
            _nTxEooOut = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("zeus_rade_n_tx_eoo_out");
            _tx = (delegate* unmanaged[Cdecl]<IntPtr, short*, float*, int>)Export("zeus_rade_tx");
            _txEoo = (delegate* unmanaged[Cdecl]<IntPtr, float*, int>)Export("zeus_rade_tx_eoo");
            _setTxCallsign = (delegate* unmanaged[Cdecl]<IntPtr, byte*, void>)Export("zeus_rade_set_tx_callsign");
            _sync = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("zeus_rade_sync");
            _snrDb = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("zeus_rade_snr_db");
            _getEooCallsign = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)Export("zeus_rade_get_eoo_callsign");

            // rade_initialize — once per process. Never finalized: the modem
            // may reopen at any time and process exit reclaims everything.
            _globalInit();
            return true;
        }
        catch
        {
            if (_lib != IntPtr.Zero) { NativeLibrary.Free(_lib); _lib = IntPtr.Zero; }
            return false;
        }
    }

    private static IntPtr Export(string name) => NativeLibrary.GetExport(_lib, name);

    private static IEnumerable<string> Candidates()
    {
        string rid = Rid();
        string file = FileName();
        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "runtimes", rid, "native", file);
        yield return Path.Combine(baseDir, file);
    }

    private static string Rid() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64")
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
            : (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64");

    private static string FileName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "zeus_rade.dll"
      : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libzeus_rade.dylib"
      : "libzeus_rade.so";
}
