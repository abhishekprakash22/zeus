// SPDX-License-Identifier: GPL-2.0-or-later
//
// Enabling WSPR receive is idempotent: the panel re-asserts it on every open,
// and a restart costs a whole 120 s slot of captured audio. Re-enabling the
// same receiver and dial inside one slot used to wipe the capture, so a panel
// that remounted every few minutes never decoded anything.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.Hosting.Digital;

namespace Zeus.Server.Tests;

public sealed class WsprEnableTests
{
    private static WsprService NewService() =>
        new(pipeline: null!, digital: null!, ingest: null!, tx: null!,
            log: NullLogger<WsprService>.Instance);

    [SkippableFact]
    public void ReEnabling_TheSameReceiveSettings_KeepsTheCapture()
    {
        var w = NewService();
        Skip.IfNot(w.NativeAvailable, "libzeus_wspr not staged for this platform");

        Assert.True(w.Enable(0, 14.0956));
        int afterFirst = w.CaptureRestarts;
        Assert.Equal(1, afterFirst);

        Assert.True(w.Enable(0, 14.0956));
        Assert.True(w.Enable(0, 14.0956));
        Assert.Equal(afterFirst, w.CaptureRestarts);   // no restart, no lost slot
        Assert.True(w.Enabled);
    }

    [SkippableFact]
    public void ChangingBandOrReceiver_RestartsTheCapture()
    {
        var w = NewService();
        Skip.IfNot(w.NativeAvailable, "libzeus_wspr not staged for this platform");

        w.Enable(0, 14.0956);
        w.Enable(0, 7.0386);                            // QSY
        Assert.Equal(2, w.CaptureRestarts);

        w.Enable(1, 7.0386);                            // other receiver
        Assert.Equal(3, w.CaptureRestarts);

        w.Disable();
        w.Enable(1, 7.0386);                            // restart after a stop
        Assert.Equal(4, w.CaptureRestarts);
    }
}
