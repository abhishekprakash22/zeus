// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Protocol1.Tests;

public sealed class Protocol1ClientOcMaskTests
{
    [Fact]
    public void SetOcMasks_StoresAllThreeMasksInSnapshot()
    {
        using var client = new Protocol1Client();

        client.SetOcMasks(txMask: 0x81, rxMask: 0x82, tuneMask: 0x83);
        var state = client.SnapshotState();

        Assert.Equal((byte)0x01, state.UserOcTxMask);
        Assert.Equal((byte)0x02, state.UserOcRxMask);
        Assert.Equal((byte)0x03, state.UserOcTuneMask);
    }
}
