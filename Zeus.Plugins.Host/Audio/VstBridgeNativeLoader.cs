using System.Reflection;
using System.Runtime.InteropServices;

namespace Zeus.Plugins.Host.Audio;

internal static class VstBridgeNativeLoader
{
    internal static void EnsureResolverRegistered()
    {
        // Route through the shared registrar: the runtime allows only ONE
        // DllImport resolver per assembly, and the macOS AU bridge ships a
        // second native library in this same assembly. The registrar installs
        // a single dispatching resolver so VST3 and AU never collide. VST3
        // resolution is unchanged — Resolve below is the same delegate as
        // before, just installed via the shared dispatcher instead of a
        // direct SetDllImportResolver call.
        NativeBridgeResolver.Register(Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != VstBridgeNative.LibraryName) return IntPtr.Zero;
        return TryResolve(assembly, out var handle) ? handle : IntPtr.Zero;
    }

    internal static bool TryResolve(Assembly assembly, out IntPtr handle)
    {
        foreach (var candidate in CandidatePaths(assembly))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                return true;
        }
        return NativeLibrary.TryLoad(VstBridgeNative.LibraryName, assembly, null, out handle);
    }

    internal static IEnumerable<string> CandidatePaths(Assembly assembly)
    {
        string rid = CurrentRid();
        string fileName = NativeFileName();
        // Single-file publish (the Windows installer) embeds assemblies:
        // Assembly.Location is "" there and Path.GetDirectoryName of it is
        // null — which would silently break native-library resolution at
        // runtime. AppContext.BaseDirectory is the app folder in EVERY
        // deployment shape (framework-dependent, self-contained, single
        // file), so prefer it and keep Location only as a secondary probe.
        string? asmDir = AppContext.BaseDirectory;
        string? asmLocDir = Path.GetDirectoryName(assembly.Location);
        if (!string.IsNullOrEmpty(asmDir))
        {
            yield return Path.Combine(asmDir, "runtimes", rid, "native", fileName);
            yield return Path.Combine(asmDir, fileName);
        }
        if (!string.Equals(asmLocDir, asmDir, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(asmLocDir))
            {
                yield return Path.Combine(asmLocDir, "runtimes", rid, "native", fileName);
                yield return Path.Combine(asmLocDir, fileName);
            }
        }

        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "runtimes", rid, "native", fileName);
        yield return Path.Combine(baseDir, fileName);
    }

    private static string CurrentRid()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return $"linux-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{arch}";
        return $"unknown-{arch}";
    }

    private static string NativeFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libzeus-vst-bridge.dylib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "libzeus-vst-bridge.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "zeus-vst-bridge.dll";
        return "libzeus-vst-bridge";
    }
}
