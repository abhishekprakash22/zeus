// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// FreeDvNative — bindings for libcodec2's freedv_api (drowe67/codec2,
// pinned 1.2.0 — see native/codec2/VENDORING.md). The library ships in
// Zeus.Dsp/runtimes/{rid}/native like libwdsp / libminiaudio, so after
// `dotnet publish` it lands under runtimes/{rid}/native beside the host.
//
// LOADING: deliberately NOT a [DllImport] + SetDllImportResolver pair.
// The .NET runtime allows exactly ONE DllImport resolver per assembly and
// this assembly already has claimants (Ft8Native's static ctor and
// MiniAudioInterop.EnsureResolverRegistered — a latent first-wins race of
// their own). Instead we NativeLibrary.TryLoad the RID-probed path once and
// bind unmanaged function pointers via GetExport. A missing library is NOT
// fatal: Available goes false, /status reports nativeAvailable:false, the
// FreeDV UI shows its gated state, and RadioService refuses FREEDV mode.
//
// ABI: verified against codec2 tag 1.2.0 src/freedv_api.h. Mode constants:
// 1600=0, 800XA=5, 700C=6, 700D=7, 700E=13. freedv_set_squelch_en /
// freedv_set_clip take C99 bool (1 byte); marshal as byte to be exact.

using System.Runtime.InteropServices;

namespace Zeus.Server.Hosting.FreeDv;

internal static unsafe class FreeDvNative
{
    // FREEDV_MODE_* from codec2 1.2.0 freedv_api.h.
    public const int Mode1600 = 0;
    public const int Mode800Xa = 5;
    public const int Mode700C = 6;
    public const int Mode700D = 7;
    public const int Mode700E = 13;

    private static readonly object Sync = new();
    private static bool _probed;
    private static bool _available;
    private static IntPtr _lib;

    // ---- bound entry points (valid iff Available) ---------------------------
    private static delegate* unmanaged[Cdecl]<int, IntPtr> _open;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _close;
    private static delegate* unmanaged[Cdecl]<IntPtr, short*, short*, void> _tx;
    private static delegate* unmanaged[Cdecl]<IntPtr, short*, short*, int> _rx;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _nin;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _nSpeechSamples;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _nMaxModemSamples;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _nNomModemSamples;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _speechSampleRate;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _modemSampleRate;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _getSync;
    private static delegate* unmanaged[Cdecl]<IntPtr, int*, float*, void> _getModemStats;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte, void> _setSquelchEn;
    private static delegate* unmanaged[Cdecl]<IntPtr, float, void> _setSnrSquelchThresh;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte, void> _setClip;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, void> _setTxBpf;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> _setCallbackTxt;
    private static delegate* unmanaged[Cdecl]<int> _getVersion;

    /// <summary>True when libcodec2 loaded and every required export bound.</summary>
    public static bool Available
    {
        get
        {
            lock (Sync)
            {
                if (_probed) return _available;
                _probed = true;
                _available = TryBind();
                return _available;
            }
        }
    }

    /// <summary>freedv_api version int, or null when unavailable.</summary>
    public static int? ApiVersion => Available ? _getVersion() : null;

    public static IntPtr Open(int mode) => _open(mode);
    public static void Close(IntPtr f) => _close(f);
    public static void Tx(IntPtr f, short* modOut, short* speechIn) => _tx(f, modOut, speechIn);
    public static int Rx(IntPtr f, short* speechOut, short* demodIn) => _rx(f, speechOut, demodIn);
    public static int Nin(IntPtr f) => _nin(f);
    public static int NSpeechSamples(IntPtr f) => _nSpeechSamples(f);
    public static int NMaxModemSamples(IntPtr f) => _nMaxModemSamples(f);
    public static int NNomModemSamples(IntPtr f) => _nNomModemSamples(f);
    public static int SpeechSampleRate(IntPtr f) => _speechSampleRate(f);
    public static int ModemSampleRate(IntPtr f) => _modemSampleRate(f);
    public static int GetSync(IntPtr f) => _getSync(f);
    public static void GetModemStats(IntPtr f, out int sync, out float snrEst)
    {
        int s; float snr;
        _getModemStats(f, &s, &snr);
        sync = s; snrEst = snr;
    }
    public static void SetSquelchEn(IntPtr f, bool en) => _setSquelchEn(f, en ? (byte)1 : (byte)0);
    public static void SetSnrSquelchThresh(IntPtr f, float db) => _setSnrSquelchThresh(f, db);
    public static void SetClip(IntPtr f, bool on) => _setClip(f, on ? (byte)1 : (byte)0);
    public static void SetTxBpf(IntPtr f, bool on) => _setTxBpf(f, on ? 1 : 0);
    /// <summary>rxCb: void(*)(void*, char); txCb: char(*)(void*).</summary>
    public static void SetCallbackTxt(IntPtr f, IntPtr rxCb, IntPtr txCb, IntPtr state)
        => _setCallbackTxt(f, rxCb, txCb, state);

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

            _open = (delegate* unmanaged[Cdecl]<int, IntPtr>)Export("freedv_open");
            _close = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("freedv_close");
            _tx = (delegate* unmanaged[Cdecl]<IntPtr, short*, short*, void>)Export("freedv_tx");
            _rx = (delegate* unmanaged[Cdecl]<IntPtr, short*, short*, int>)Export("freedv_rx");
            _nin = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("freedv_nin");
            _nSpeechSamples = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("freedv_get_n_speech_samples");
            _nMaxModemSamples = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("freedv_get_n_max_modem_samples");
            _nNomModemSamples = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("freedv_get_n_nom_modem_samples");
            _speechSampleRate = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("freedv_get_speech_sample_rate");
            _modemSampleRate = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("freedv_get_modem_sample_rate");
            _getSync = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("freedv_get_sync");
            _getModemStats = (delegate* unmanaged[Cdecl]<IntPtr, int*, float*, void>)Export("freedv_get_modem_stats");
            _setSquelchEn = (delegate* unmanaged[Cdecl]<IntPtr, byte, void>)Export("freedv_set_squelch_en");
            _setSnrSquelchThresh = (delegate* unmanaged[Cdecl]<IntPtr, float, void>)Export("freedv_set_snr_squelch_thresh");
            _setClip = (delegate* unmanaged[Cdecl]<IntPtr, byte, void>)Export("freedv_set_clip");
            _setTxBpf = (delegate* unmanaged[Cdecl]<IntPtr, int, void>)Export("freedv_set_tx_bpf");
            _setCallbackTxt = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)Export("freedv_set_callback_txt");
            _getVersion = (delegate* unmanaged[Cdecl]<int>)Export("freedv_get_version");
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
        // Publish / dotnet-run layout: Zeus.Dsp's runtimes/ copied beside the host.
        yield return Path.Combine(baseDir, "runtimes", rid, "native", file);
        // Self-contained publish flattens RID-matching natives beside the exe.
        yield return Path.Combine(baseDir, file);
    }

    private static string Rid() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64")
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
            : (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64");

    private static string FileName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "codec2.dll"
      : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libcodec2.dylib"
      : "libcodec2.so";
}
