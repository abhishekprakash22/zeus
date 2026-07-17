using System.Runtime.InteropServices;
using Zeus.Dsp.Wdsp;

namespace Zeus.Dsp.Tests;

public sealed class WdspNativeLoaderTests
{
    [Fact]
    public void TryProbeExport_ReturnsFalseForMissingSymbol()
    {
        Assert.False(WdspNativeLoader.TryProbeExport("Zeus_DefinitelyMissingNativeExport"));
    }

    [Fact]
    public void NoiseReductionCapabilityProbes_DoNotThrow()
    {
        bool loadable = WdspDspEngine.NativeLibraryLoadable;
        bool post2 = WdspDspEngine.EmnrPost2Available;
        bool nr4 = WdspDspEngine.Nr4SbnrAvailable;

        if (!loadable)
        {
            Assert.False(post2);
            Assert.False(nr4);
        }
    }

    [SkippableFact]
    public void WinX64RuntimeArtifact_ExportsCurrentNoiseReductionSymbols()
    {
        Skip.IfNot(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64,
            "Packaged win-x64 WDSP artifact assertion only runs on Windows x64.");

        Assert.True(WdspDspEngine.NativeLibraryLoadable, "win-x64 wdsp.dll should load from Zeus.Dsp/runtimes/win-x64/native.");
        Assert.True(WdspDspEngine.Nr4SbnrAvailable, "win-x64 wdsp.dll should export NR4/SBNR symbols.");
    }
}
