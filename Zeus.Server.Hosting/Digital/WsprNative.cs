// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// WsprNative — bindings for libzeus_wspr (native/wspr: the vendored K9AN
// wsprd decoder + wsprsim channel-symbol encoder behind a flat shim ABI).
// Same explicit NativeLibrary.TryLoad + GetExport pattern as FreeDvNative,
// for the same reason: this assembly's single DllImport-resolver slot is
// already claimed (Ft8Native / MiniAudioInterop). Missing library is not
// fatal — Available goes false, /wspr reports nativeAvailable:false, the
// WSPR workspace shows its gated state.

using System.Runtime.InteropServices;

namespace Zeus.Server.Hosting.Digital;

[StructLayout(LayoutKind.Sequential)]
internal struct ZeusWsprSpot
{
    public double FreqHz;      // absolute spot frequency (as reported by wsprd)
    public float SnrDb;
    public float DtSec;
    public float DriftHz;
    public unsafe fixed byte Message[24];
}

internal static unsafe class WsprNative
{
    /// <summary>Decoder input contract: 120 s of complex baseband at 375 Hz.</summary>
    public const int BasebandRateHz = 375;
    public const int SlotSamples = 45_000;
    public const int SymbolCount = 162;

    private static readonly object Sync = new();
    private static bool _probed;
    private static bool _available;
    private static IntPtr _lib;

    private static delegate* unmanaged[Cdecl]<int> _abi;
    private static delegate* unmanaged[Cdecl]<float*, float*, int, int, ZeusWsprSpot*, int, int> _decode;
    private static delegate* unmanaged[Cdecl]<byte*, byte*, int> _encode;

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

    /// <summary>Decode one slot; returns spot count (0..maxOut) or -1.
    /// Forces the lazy bind — safe to call without touching Available first.</summary>
    public static int Decode(float* idat, float* qdat, int samples, int dialFreqHz,
                             ZeusWsprSpot* outSpots, int maxOut)
        => Available ? _decode(idat, qdat, samples, dialFreqHz, outSpots, maxOut) : -1;

    /// <summary>"CALL GRID DBM" → 162 channel symbols (0..3). True on success.</summary>
    public static bool Encode(string message, Span<byte> symbols162)
    {
        if (!Available || symbols162.Length < SymbolCount) return false;
        var bytes = System.Text.Encoding.ASCII.GetBytes(message + "\0");
        fixed (byte* pMsg = bytes)
        fixed (byte* pSym = symbols162)
            return _encode(pMsg, pSym) == 0;
    }

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

            _abi = (delegate* unmanaged[Cdecl]<int>)Export("zeus_wspr_abi_version");
            _decode = (delegate* unmanaged[Cdecl]<float*, float*, int, int, ZeusWsprSpot*, int, int>)
                Export("zeus_wspr_decode");
            _encode = (delegate* unmanaged[Cdecl]<byte*, byte*, int>)Export("zeus_wspr_encode");
            return _abi() == 1;
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
        string rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64")
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
                : (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64");
        string file = FileName();
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", file);
        yield return Path.Combine(AppContext.BaseDirectory, file);
    }

    private static string FileName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "zeus_wspr.dll"
      : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libzeus_wspr.dylib"
      : "libzeus_wspr.so";
}
