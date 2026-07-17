// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Protocol2.Tests;

public sealed class CmdHighPriorityOcMaskTests
{
    [Fact]
    public void OcMaskByte1401_UsesRxMaskWhenIdle()
    {
        byte b = Protocol2Client.ComposeOcMaskByte1401(
            moxOn: false,
            tuneActive: false,
            txMask: 0x01,
            rxMask: 0x02,
            tuneMask: 0x04);

        Assert.Equal((byte)0x04, b);
    }

    [Fact]
    public void OcMaskByte1401_UsesTxMaskWhenMox()
    {
        byte b = Protocol2Client.ComposeOcMaskByte1401(
            moxOn: true,
            tuneActive: false,
            txMask: 0x01,
            rxMask: 0x02,
            tuneMask: 0x04);

        Assert.Equal((byte)0x02, b);
    }

    [Fact]
    public void OcMaskByte1401_OrsTuneMaskOnTopOfTxMaskWhenTuneActive()
    {
        byte b = Protocol2Client.ComposeOcMaskByte1401(
            moxOn: false,
            tuneActive: true,
            txMask: 0x01,
            rxMask: 0x40,
            tuneMask: 0x04);

        Assert.Equal((byte)0x0A, b);
    }

    [Fact]
    public void OcMaskByte1401_ZeroTuneMaskIsNeutral()
    {
        byte mox = Protocol2Client.ComposeOcMaskByte1401(
            moxOn: true,
            tuneActive: false,
            txMask: 0x12,
            rxMask: 0x55,
            tuneMask: 0x00);
        byte tune = Protocol2Client.ComposeOcMaskByte1401(
            moxOn: true,
            tuneActive: true,
            txMask: 0x12,
            rxMask: 0x55,
            tuneMask: 0x00);

        Assert.Equal(mox, tune);
    }

    [Fact]
    public void OcMaskByte1401_NarrowsMasksToSevenBits()
    {
        Assert.Equal(
            (byte)0xFE,
            Protocol2Client.ComposeOcMaskByte1401(
                moxOn: false,
                tuneActive: false,
                txMask: 0x00,
                rxMask: 0xFF,
                tuneMask: 0x00));

        Assert.Equal(
            (byte)0x02,
            Protocol2Client.ComposeOcMaskByte1401(
                moxOn: true,
                tuneActive: true,
                txMask: 0x01,
                rxMask: 0x00,
                tuneMask: 0x80));
    }
}
