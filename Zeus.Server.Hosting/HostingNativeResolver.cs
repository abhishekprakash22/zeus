// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Single owner of the NativeLibrary.SetDllImportResolver hook for the
// Zeus.Server.Hosting assembly — the runtime permits exactly ONE resolver
// per assembly, and this assembly had grown two independent registrants
// (MiniAudioInterop, Ft8Native): whichever initialized second threw
// 'A resolver is already set for the assembly', which is precisely how the
// first Windows installer died at DigitalService.StartAsync (Linux masked
// it by initialization order/platform gating). Same cure the plugin host
// already carries as NativeBridgeResolver: interop classes register their
// name-filtering resolver here; the hub registers once and dispatches
// first-non-zero, falling through to default probing otherwise.

using System.Reflection;
using System.Runtime.InteropServices;

namespace Zeus.Server;

internal static class HostingNativeResolver
{
    private static readonly object Sync = new();
    private static readonly List<DllImportResolver> Handlers = new();
    private static bool _hooked;

    /// <summary>Add a resolver (which must return IntPtr.Zero for library
    /// names it does not own). First call installs the single assembly-wide
    /// hook.</summary>
    public static void Register(DllImportResolver handler)
    {
        lock (Sync)
        {
            Handlers.Add(handler);
            if (_hooked) return;
            _hooked = true;
            NativeLibrary.SetDllImportResolver(typeof(HostingNativeResolver).Assembly, Dispatch);
        }
    }

    private static IntPtr Dispatch(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        DllImportResolver[] handlers;
        lock (Sync) handlers = Handlers.ToArray();
        foreach (var h in handlers)
        {
            IntPtr p = h(libraryName, assembly, searchPath);
            if (p != IntPtr.Zero) return p;
        }
        return IntPtr.Zero;   // default probing continues
    }
}
