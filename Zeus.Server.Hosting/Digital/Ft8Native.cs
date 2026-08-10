// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — native FT8 decoder + synthesizer bindings.
//
// Binds libzeus_ft8 (native/ft8/zeus_ft8.c), a flat shim over ft8_lib
// (Karlis Goba, MIT). The shim keeps ft8_lib's monitor/candidate/decode
// machinery on the C side and returns a plain array of PODs, so nothing but
// blittable structs crosses the boundary. The TX side (zeus_ft8_synth) is an
// ADDITIVE export: an older .so without it still decodes, Synth() reports the
// rebuild requirement instead of throwing, and Available stays version==1.
//
// Library resolution mirrors WdspNativeLoader: try the explicit RID path under
// runtimes/{rid}/native first, then the plain name (LD_LIBRARY_PATH / system).
// A missing library is NOT fatal — Available goes false, /status reports
// decoderAvailable:false, and the UI still renders.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Zeus.Server.Hosting.Digital;

/// <summary>Mirrors zeus_ft8_decode_t in zeus_ft8.h, field for field.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ZeusFt8Decode
{
    public int SnrDb;
    public float DtSec;
    public float FreqHz;
    public int Score;
    public fixed byte Text[40];
}

internal static unsafe class Ft8Native
{
    private const string Lib = "zeus_ft8";
    private const int MaxDecodesPerSlot = 64;

    private static readonly object Sync = new();
    private static bool _probed;
    private static bool _available;

    static Ft8Native()
    {
        // Through the assembly's single resolver hub — a direct
            // SetDllImportResolver here collided with the other Hosting
            // interop's registration ('A resolver is already set').
            HostingNativeResolver.Register(Resolve);
    }

    /// <summary>True when libzeus_ft8 loaded and its ABI matches.</summary>
    public static bool Available
    {
        get
        {
            lock (Sync)
            {
                if (_probed) return _available;
                _probed = true;
                try { _available = zeus_ft8_version() == 1; }
                catch { _available = false; }
                return _available;
            }
        }
    }

    public static IReadOnlyList<Ft8DecodeDto> Decode(float[] audio, int rate, bool isFt4)
    {
        if (!Available || audio.Length == 0) return Array.Empty<Ft8DecodeDto>();

        var raw = new ZeusFt8Decode[MaxDecodesPerSlot];
        int n;
        fixed (float* pAudio = audio)
        fixed (ZeusFt8Decode* pOut = raw)
        {
            n = zeus_ft8_decode(pAudio, audio.Length, rate, isFt4 ? 1 : 0, pOut, MaxDecodesPerSlot);
        }
        if (n <= 0) return Array.Empty<Ft8DecodeDto>();

        var list = new List<Ft8DecodeDto>(n);
        for (int i = 0; i < n; i++)
        {
            string text;
            fixed (byte* p = raw[i].Text)
                text = Marshal.PtrToStringUTF8((IntPtr)p) ?? "";
            text = text.Trim();
            if (text.Length == 0) continue;

            list.Add(new Ft8DecodeDto
            {
                SnrDb = raw[i].SnrDb,
                DtSec = Math.Round(raw[i].DtSec, 2),
                FreqHz = (int)Math.Round(raw[i].FreqHz),
                Score = raw[i].Score,
                Text = text,
                WorkedBefore = false,        // plugin has no logbook — UI derives it
                Country = null,              // TODO: prefix → DXCC enrichment
            });
        }
        return list;
    }

    // ---- TX synthesis -------------------------------------------------------

    /// <summary>FT8: 79 symbols × 160 ms. FT4: 105 × 48 ms.</summary>
    public static int WaveSamples(bool isFt4, int sampleRate)
    {
        int nSym = isFt4 ? 105 : 79;
        float period = isFt4 ? 0.048f : 0.16f;
        int nSpsym = (int)(0.5f + sampleRate * period);
        return nSym * nSpsym;
    }

    /// <summary>
    /// Synthesize the full GFSK waveform for <paramref name="message"/> at the
    /// given audio offset and sample rate. Full-scale ±1.0 — the caller scales.
    /// Returns null with a human-readable <paramref name="error"/> on failure
    /// (encode rejection, missing native, or an old .so lacking the export).
    /// </summary>
    public static float[]? Synth(string message, bool isFt4, float audioHz,
                                 int sampleRate, out string? error)
    {
        error = null;
        if (!Available)
        {
            error = "libzeus_ft8 unavailable";
            return null;
        }

        int nWave = WaveSamples(isFt4, sampleRate);
        var wave = new float[nWave];

        // NUL-terminated UTF-8 for the C side.
        byte[] utf8 = Encoding.UTF8.GetBytes(message + "\0");

        int n;
        try
        {
            fixed (float* pOut = wave)
            fixed (byte* pText = utf8)
            {
                n = zeus_ft8_synth(pText, isFt4 ? 1 : 0, audioHz, sampleRate, pOut, nWave);
            }
        }
        catch (EntryPointNotFoundException)
        {
            error = "libzeus_ft8.so predates TX support — rebuild native/ft8 " +
                    "(./build.sh) and redeploy the .so";
            return null;
        }

        if (n <= 0)
        {
            error = n switch
            {
                -1 => $"FT8 encoder rejected message '{message}' (grammar/pack error)",
                -2 => "synth argument/buffer error",
                -3 => "synth out of memory",
                _ => $"synth failed ({n})",
            };
            return null;
        }

        if (n != nWave) Array.Resize(ref wave, n);
        return wave;
    }

    // ---- interop ------------------------------------------------------------

    [DllImport(Lib, EntryPoint = "zeus_ft8_version", CallingConvention = CallingConvention.Cdecl)]
    private static extern int zeus_ft8_version();

    [DllImport(Lib, EntryPoint = "zeus_ft8_decode", CallingConvention = CallingConvention.Cdecl)]
    private static extern int zeus_ft8_decode(
        float* audio, int nSamples, int sampleRate, int isFt4,
        ZeusFt8Decode* outArr, int maxOut);

    [DllImport(Lib, EntryPoint = "zeus_ft8_synth", CallingConvention = CallingConvention.Cdecl)]
    private static extern int zeus_ft8_synth(
        byte* textUtf8, int isFt4, float audioHz, int sampleRate,
        float* outArr, int maxOut);

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (!string.Equals(name, Lib, StringComparison.Ordinal)) return IntPtr.Zero;

        foreach (var candidate in Candidates(asm))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var h))
                return h;
        }
        return NativeLibrary.TryLoad(FileName(), asm, null, out var fallback)
            ? fallback : IntPtr.Zero;
    }

    private static IEnumerable<string> Candidates(Assembly asm)
    {
        string rid = Rid();
        string file = FileName();

        // Beside the app — IL3000: Location is "" in the single-file
        // Windows installer; BaseDirectory is the app folder in every
        // deployment shape, and this loader ships in the core app.
        string? asmDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(asmDir))
        {
            yield return Path.Combine(asmDir, file);
            yield return Path.Combine(asmDir, "runtimes", rid, "native", file);
        }

        // Beside the host — where Zeus.Dsp's runtimes/ land after publish.
        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "runtimes", rid, "native", file);
        yield return Path.Combine(baseDir, file);
    }

    private static string Rid() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64")
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "osx-arm64"
            : (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64");

    private static string FileName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "zeus_ft8.dll"
      : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libzeus_ft8.dylib"
      : "libzeus_ft8.so";
}
