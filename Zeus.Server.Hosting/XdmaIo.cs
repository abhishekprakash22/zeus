// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// XdmaIo — register access to the XDMA user BAR the way piHPSDR does it:
// raw libc pread/pwrite on the fd. The first field diagnosis (CM5 on a
// Saturn, /api/system/xdma) named the divergence exactly: .NET classifies
// a character device as unseekable BY FILE TYPE and RandomAccess.Read
// then throws NotSupportedException('Stream does not support seeking')
// without ever issuing the syscall — while the xdma driver's char_ctrl
// implements llseek and honors pread perfectly well, which is why
// piHPSDR sees the radio and the managed probe did not. So we stop
// asking .NET's opinion of the file and make the same syscall the C
// makes. Used by SaturnXdmaProbe and SaturnFlashService.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Zeus.Server;

internal static class XdmaIo
{
    [DllImport("libc", SetLastError = true)]
    private static extern nint pread(int fd, ref uint buf, nuint count, long offset);

    [DllImport("libc", SetLastError = true)]
    private static extern nint pwrite(int fd, ref uint buf, nuint count, long offset);

    /// <summary>4-byte register read at <paramref name="offset"/> in the BAR.</summary>
    public static uint Read32(SafeFileHandle handle, long offset)
    {
        uint v = 0;
        // The handle stays alive for the duration of the owning FileStream's
        // using-scope; DangerousGetHandle without AddRef is safe under that
        // discipline (both call sites hold the stream open across all IO).
        nint n = pread((int)handle.DangerousGetHandle(), ref v, 4, offset);
        if (n != 4)
            throw new IOException($"pread(0x{offset:X}) returned {n} (errno {Marshal.GetLastPInvokeError()})");
        return v;
    }

    /// <summary>4-byte register write at <paramref name="offset"/> in the BAR.</summary>
    public static void Write32(SafeFileHandle handle, long offset, uint value)
    {
        nint n = pwrite((int)handle.DangerousGetHandle(), ref value, 4, offset);
        if (n != 4)
            throw new IOException($"pwrite(0x{offset:X}) returned {n} (errno {Marshal.GetLastPInvokeError()})");
    }
}
