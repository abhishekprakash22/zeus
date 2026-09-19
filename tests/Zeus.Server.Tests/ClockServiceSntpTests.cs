// SPDX-License-Identifier: GPL-2.0-or-later
//
// ClockService's SNTP fallback (macOS / Windows / unsynced Linux): the offset
// maths on hand-built server replies. No network — TrySntp itself is exercised
// live by the running service.

using System.Buffers.Binary;
using Zeus.Server.Hosting.Digital;

namespace Zeus.Server.Tests;

public sealed class ClockServiceSntpTests
{
    private const double NtpToUnixSeconds = 2_208_988_800.0;

    private static byte[] Reply(double serverRecvUnixMs, double serverSendUnixMs, byte mode = 4, byte stratum = 2)
    {
        var b = new byte[48];
        b[0] = (byte)((4 << 3) | mode);   // VN 4, mode
        b[1] = stratum;
        Write(b, 32, serverRecvUnixMs);
        Write(b, 40, serverSendUnixMs);
        return b;

        static void Write(byte[] buf, int at, double unixMs)
        {
            double ntpSecs = unixMs / 1000.0 + NtpToUnixSeconds;
            uint secs = (uint)Math.Floor(ntpSecs);
            uint frac = (uint)((ntpSecs - secs) * 4294967296.0);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(at, 4), secs);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(at + 4, 4), frac);
        }
    }

    [Fact]
    public void Offset_IsZero_WhenClocksAgree()
    {
        double t1 = 1_789_000_000_000;               // local send
        var reply = Reply(t1 + 20, t1 + 21);         // 20 ms one way, 1 ms server hold
        Assert.True(ClockService.TryComputeSntpOffset(reply, t1, t1 + 41, out double off));
        Assert.InRange(off, -1, 1);
    }

    [Fact]
    public void Offset_IsPositive_WhenLocalClockIsBehind()
    {
        double t1 = 1_789_000_000_000;
        // Server is 2 s ahead of us; 30 ms each way.
        var reply = Reply(t1 + 2000 + 30, t1 + 2000 + 31);
        Assert.True(ClockService.TryComputeSntpOffset(reply, t1, t1 + 61, out double off));
        Assert.InRange(off, 1999, 2001);
    }

    [Theory]
    [InlineData(3, 2)]   // mode 3 = a client packet, not a server reply
    [InlineData(4, 0)]   // stratum 0 = kiss-o'-death
    public void InvalidReplies_AreRejected(byte mode, byte stratum)
    {
        double t1 = 1_789_000_000_000;
        Assert.False(ClockService.TryComputeSntpOffset(Reply(t1, t1, mode, stratum), t1, t1, out _));
        Assert.False(ClockService.TryComputeSntpOffset(new byte[12], t1, t1, out _));
    }
}
