// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

using Xunit;
using Zeus.Server.Hosting.Remote;

namespace Zeus.Server.Tests;

/// <summary>
/// Software and firmware maintenance is done at the radio: a remote session
/// may read status but never update ANAN Core, update/stop/start p2app,
/// flash or compare gateware, or touch the raw XDMA routes.
/// </summary>
public class RemoteLocalOnlyMaintenanceTests
{
    [Theory]
    [InlineData("/api/system/update/apply")]
    [InlineData("/api/p2app/update")]
    [InlineData("/api/p2app/stop")]
    [InlineData("/api/p2app/start")]
    [InlineData("/api/fpga/flash")]
    [InlineData("/api/fpga/compare")]
    [InlineData("/api/fpga/hl2/flash")]
    [InlineData("/api/fpga/hl2/flash-file")]
    [InlineData("/api/fpga/hl2/compare")]
    [InlineData("/api/xdma/tx/arm")]
    [InlineData("/api/xdma/rx/start")]
    [InlineData("/api/xdma/control/ddc-freq")]
    public void Mutations_AreDeniedOverRemote(string path)
    {
        Assert.True(RemoteWebRtcSession.IsDenied(path, mutating: true));
    }

    [Theory]
    [InlineData("/API/P2APP/UPDATE")]
    [InlineData("/api/fpga/flash?confirm=FLASH")]
    [InlineData("/api/system/update/apply?x=1")]
    public void Mutations_CannotBeSmuggledByCaseOrQuery(string path)
    {
        Assert.True(RemoteWebRtcSession.IsDenied(path, mutating: true));
    }

    [Theory]
    [InlineData("/api/system/update")]
    [InlineData("/api/system/update/apply")]
    [InlineData("/api/p2app")]
    [InlineData("/api/p2app/update")]
    [InlineData("/api/fpga/flash")]
    [InlineData("/api/fpga/images")]
    [InlineData("/api/fpga/hl2/flash")]
    [InlineData("/api/xdma/status")]
    public void StatusReads_StayOpen(string path)
    {
        Assert.False(RemoteWebRtcSession.IsDenied(path, mutating: false));
    }

    [Theory]
    [InlineData("/api/system/update")]   // CHECK FOR UPDATES is a GET, but guard the POST shape too
    [InlineData("/api/tx/mox")]
    [InlineData("/api/receivers/0")]
    public void OrdinaryOperation_IsNotCaught(string path)
    {
        // Only the apply leg of the updater is local-only; a stray POST to the
        // status route and normal radio control must still pass.
        Assert.False(RemoteWebRtcSession.IsDenied(path, mutating: true));
    }
}
