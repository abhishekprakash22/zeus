// SPDX-License-Identifier: GPL-2.0-or-later
//
// The wisdom cache stamp must follow the native CODE, not its signature: the
// release .app carries Developer-ID-signed dylibs while a dev build carries the
// linker's ad-hoc signature, and both share one wisdom directory. Hashing the
// whole file made every switch between them rebake FFTW wisdom (minutes).

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Zeus.Dsp.Wdsp;

namespace Zeus.Dsp.Tests;

public sealed class WdspWisdomStampTests
{
    [Fact]
    public void NonMachO_FallsBackToWholeFileHash()
    {
        var bytes = new byte[4096];
        new Random(42).NextBytes(bytes);
        Assert.Null(WdspWisdomInitializer.MachOSectionsHash(bytes));

        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, bytes);
            string expected = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
            Assert.Equal(expected, WdspWisdomInitializer.HashFile(path));
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void MacOS_ResigningTheLibrary_DoesNotChangeTheStamp()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.OSX), "codesign is macOS-only");
        string rid = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        string lib = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "libwdsp.dylib");
        Skip.IfNot(File.Exists(lib), $"libwdsp not staged for {rid}");

        string copy = Path.Combine(Path.GetTempPath(), $"zeus-wisdom-stamp-{Guid.NewGuid():N}.dylib");
        try
        {
            File.Copy(lib, copy);
            var p = Process.Start(new ProcessStartInfo("codesign", $"--remove-signature \"{copy}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            })!;
            p.WaitForExit();
            Skip.If(p.ExitCode != 0, "codesign --remove-signature failed");

            // The bytes differ (signature and load commands gone) …
            Assert.NotEqual(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(lib))),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copy))));
            // … but the code is the same, so the wisdom stamp must be too.
            Assert.NotNull(WdspWisdomInitializer.MachOSectionsHash(File.ReadAllBytes(lib)));
            Assert.Equal(WdspWisdomInitializer.HashFile(lib), WdspWisdomInitializer.HashFile(copy));
        }
        finally { File.Delete(copy); }
    }
}
