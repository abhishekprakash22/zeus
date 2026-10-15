// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — WDSP 2.11 WDSPwisdom refusal handling.

using Zeus.Contracts;
using Zeus.Dsp.Wdsp;

namespace Zeus.Dsp.Tests;

public sealed class WdspWisdomRefusalTests
{
    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"zeus-wisdom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task MinusOne_FailsThePhase_AndWritesNoStamp()
    {
        string dir = NewDir();
        try
        {
            var init = new WdspWisdomInitializer(
                logger: null,
                ensureResolverRegistered: () => { },
                runWisdom: _ => -1,
                getWisdomStatus: () => string.Empty,
                getWisdomDirectory: () => dir,
                getStamp: () => "test-stamp",
                // CI sets ZEUS_WISDOM_SKIP=1, which would short-circuit to Ready
                // without calling runWisdom at all.
                shouldSkip: () => false);
            await init.EnsureInitializedAsync();

            Assert.Equal(WisdomPhase.Failed, init.Phase);
            Assert.False(File.Exists(Path.Combine(dir, WdspWisdomInitializer.VersionStampFileName)));
        }
        finally
        {
            WdspWisdomInitializer.ResetAmbientForTests();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task LoadedOrBuilt_IsReady_AndStamped(int nativeResult)
    {
        string dir = NewDir();
        try
        {
            var init = new WdspWisdomInitializer(
                logger: null,
                ensureResolverRegistered: () => { },
                runWisdom: _ => nativeResult,
                getWisdomStatus: () => string.Empty,
                getWisdomDirectory: () => dir,
                getStamp: () => "test-stamp",
                // CI sets ZEUS_WISDOM_SKIP=1, which would short-circuit to Ready
                // without calling runWisdom at all.
                shouldSkip: () => false);
            await init.EnsureInitializedAsync();

            Assert.Equal(WisdomPhase.Ready, init.Phase);
            Assert.True(File.Exists(Path.Combine(dir, WdspWisdomInitializer.VersionStampFileName)));
        }
        finally
        {
            WdspWisdomInitializer.ResetAmbientForTests();
            Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class NnrSlotMappingTests
{
    // WDSP 2.11 slots: 0 SSB Standard, 1 SSB Premium, 2 CW Standard, 3 CW Premium.
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(1, false, 1)]
    [InlineData(0, true, 2)]
    [InlineData(1, true, 3)]
    public void OperatorGrade_MapsToWdspSlotByMode(int choice, bool cw, int expected) =>
        Assert.Equal(expected, WdspDspEngine.NnrWdspSlot(choice, cw));
}
