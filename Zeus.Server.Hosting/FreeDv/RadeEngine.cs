// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// RADE engines — the RADEV1 modem behind one surface, two implementations:
//
//   managed  RadeSharp (external/RadeSharp submodule): a bit-exact C# port of
//            rade_c + the Opus FARGAN vocoder / LPCNet analyzer + the FreeDV
//            reliable-text EOO callsign. No native library, every RID. DEFAULT.
//   native   the zeus_rade shim via RadeNative (P/Invoke). Kept as a fallback
//            while the managed engine is proven on air.
//
// SELECTION: ZEUS_RADE_ENGINE=native forces the shim; anything else (or unset)
// uses the managed engine, falling back to the shim only if the managed engine
// fails to open. Both engines expose the zeus_rade.h geometry and semantics, so
// FreeDvModemService.Rade.cs is engine-agnostic.
//
// ABI of the spans: modem IQ is interleaved float (re, im, re, im, ...) at
// 8 kHz; speech is int16 at 16 kHz — exactly RadeNative's pointers.

using System.Runtime.InteropServices;
using RadeSharp;
using RadeSharp.Text;

namespace Zeus.Server.Hosting.FreeDv;

internal interface IRadeEngine : IDisposable
{
    /// <summary>Human-readable engine name for status/logs.</summary>
    string Name { get; }

    int Nin { get; }
    int NinMax { get; }
    int MaxPcmPerRx { get; }
    int NSpeechSamples { get; }
    int NTxOut { get; }
    int NTxEooOut { get; }

    /// <summary>Decodes <see cref="Nin"/> interleaved complex samples; returns int16 PCM written.</summary>
    int Rx(ReadOnlySpan<float> iqIn, Span<short> pcmOut);

    /// <summary>Encodes <see cref="NSpeechSamples"/> speech samples; returns complex samples written.</summary>
    int Tx(ReadOnlySpan<short> speechIn, Span<float> iqOut);

    int TxEoo(Span<float> iqOut);

    /// <summary>Callsign for the End-of-Over frame (sanitised to ≤ 8 printable ASCII chars; empty clears).</summary>
    void SetTxCallsign(string callsign);

    bool Sync { get; }
    int SnrDb { get; }

    /// <summary>Copies the last decoded EOO callsign into <paramref name="dest"/> (≥ 9 bytes). Returns chars, 0 if none; consumes it.</summary>
    int GetEooCallsign(Span<byte> dest);
}

internal static class RadeEngines
{
    public const string EngineEnvVar = "ZEUS_RADE_ENGINE";

    /// <summary>True when some RADE engine can run here — always, with the managed engine.</summary>
    public static bool Available => true;

    public static bool NativeRequested =>
        string.Equals(Environment.GetEnvironmentVariable(EngineEnvVar), "native", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens the configured engine. Returns null (with <paramref name="error"/>) when nothing could be opened.
    /// Control-thread only: opening allocates and, for the managed engine, loads the embedded weights once per process.
    /// </summary>
    public static IRadeEngine? Open(out string? error)
    {
        error = null;
        if (!NativeRequested)
        {
            try { return new ManagedRadeEngine(); }
            catch (Exception ex) { error = $"managed RADE engine failed: {ex.Message}"; }
        }
        if (!RadeNative.Available)
        {
            error ??= "libzeus_rade is not available on this platform";
            return null;
        }
        var z = RadeNative.Open();
        if (z == IntPtr.Zero)
        {
            error = "zeus_rade_open returned NULL";
            return null;
        }
        return new NativeRadeEngine(z);
    }

    /// <summary>Same sanitising the shim binding always applied: ≤ 8 chars in '!'..'~'.</summary>
    public static int SanitiseCallsign(string callsign, Span<byte> dest)
    {
        int n = 0;
        foreach (char c in callsign)
        {
            if (n == 8 || n >= dest.Length) break;
            if (c > ' ' && c <= '~') dest[n++] = (byte)c;
        }
        return n;
    }
}

/// <summary>RadeSharp's <see cref="RadeVoiceModem"/> — the managed equivalent of the zeus_rade shim.</summary>
internal sealed class ManagedRadeEngine : IRadeEngine
{
    private readonly RadeVoiceModem _m;

    public ManagedRadeEngine()
    {
        // The C library's rade_open banner goes to stderr; keep the server log clean.
        RadeLog.Writer = null;
        _m = new RadeVoiceModem(RadeMode.V1);
    }

    public string Name => "RadeSharp (managed RADE V1 + FARGAN)";
    public int Nin => _m.RxSamplesNeeded;
    public int NinMax => _m.RxMaxSamples;
    public int MaxPcmPerRx => _m.MaxPcmPerReceive;
    public int NSpeechSamples => _m.SpeechSamplesPerTx;
    public int NTxOut => _m.TxSamplesPerFrame;
    public int NTxEooOut => _m.TxEooSamples;
    public bool Sync => _m.InSync;
    public int SnrDb => _m.SnrDb;

    public int Rx(ReadOnlySpan<float> iqIn, Span<short> pcmOut) =>
        _m.Receive(MemoryMarshal.Cast<float, RadeComp>(iqIn), pcmOut);

    public int Tx(ReadOnlySpan<short> speechIn, Span<float> iqOut) =>
        _m.Transmit(speechIn, MemoryMarshal.Cast<float, RadeComp>(iqOut));

    public int TxEoo(Span<float> iqOut) => _m.TransmitEndOfOver(MemoryMarshal.Cast<float, RadeComp>(iqOut));

    public void SetTxCallsign(string callsign)
    {
        Span<byte> buf = stackalloc byte[8];
        int n = RadeEngines.SanitiseCallsign(callsign, buf);
        _m.SetTxCallsign(n == 0 ? null : System.Text.Encoding.ASCII.GetString(buf[..n]));
    }

    public int GetEooCallsign(Span<byte> dest)
    {
        if (dest.Length < 9) return 0;
        var call = _m.TakeEooCallsign();
        if (string.IsNullOrEmpty(call)) return 0;
        int n = Math.Min(call.Length, 8);
        for (int i = 0; i < n; i++) dest[i] = (byte)call[i];
        dest[n] = 0;
        return n;
    }

    public void Dispose() => _m.Dispose();
}

/// <summary>The native zeus_rade shim through <see cref="RadeNative"/>.</summary>
internal sealed unsafe class NativeRadeEngine(IntPtr z) : IRadeEngine
{
    private IntPtr _z = z;

    public string Name => "zeus_rade (native RADE V1 + FARGAN)";
    public int Nin => RadeNative.Nin(_z);
    public int NinMax => RadeNative.NinMax(_z);
    public int MaxPcmPerRx => RadeNative.MaxPcmPerRx(_z);
    public int NSpeechSamples => RadeNative.NSpeechSamples(_z);
    public int NTxOut => RadeNative.NTxOut(_z);
    public int NTxEooOut => RadeNative.NTxEooOut(_z);
    public bool Sync => RadeNative.Sync(_z) != 0;
    public int SnrDb => RadeNative.SnrDb(_z);

    public int Rx(ReadOnlySpan<float> iqIn, Span<short> pcmOut)
    {
        fixed (float* pIq = iqIn)
        fixed (short* pPcm = pcmOut)
            return RadeNative.Rx(_z, pIq, pPcm);
    }

    public int Tx(ReadOnlySpan<short> speechIn, Span<float> iqOut)
    {
        fixed (short* pS = speechIn)
        fixed (float* pOut = iqOut)
            return RadeNative.Tx(_z, pS, pOut);
    }

    public int TxEoo(Span<float> iqOut)
    {
        fixed (float* pOut = iqOut)
            return RadeNative.TxEoo(_z, pOut);
    }

    public void SetTxCallsign(string callsign) => RadeNative.SetTxCallsign(_z, callsign);

    public int GetEooCallsign(Span<byte> dest) => RadeNative.GetEooCallsign(_z, dest);

    public void Dispose()
    {
        if (_z == IntPtr.Zero) return;
        RadeNative.Close(_z);
        _z = IntPtr.Zero;
    }
}
