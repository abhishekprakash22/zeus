// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR),
//                         Ramón Martínez (EA5IUE), and contributors.
//
// How this build names itself to everyone outside the machine.
//
// Reporting networks keep the software string next to every spot, and it is
// the only handle their operators have on us: if this station ever starts
// sending PSK Reporter or WSPRnet something wrong, that string is how they
// find out whose software to complain about. Several spellings of one program
// made that handle useless, so every outbound identification comes from here.
//
// Three renderings, one name:
//
//   Name             "ANAN Core"          — a field of its own (ADIF PROGRAMID)
//   NameWithVersion  "ANAN Core 0.10.9"   — one free-text field (PSK Reporter,
//                                           WSPRnet, FreeDV Reporter)
//   UserAgent        "ANAN-Core/0.10.9"   — HTTP, where a product token may
//                                           not contain a space (RFC 9110)
//
// The version is the build's own InformationalVersion with any "+metadata"
// suffix dropped; nothing here is hard-coded, so a release cannot report a
// version it isn't.

using System.Reflection;

namespace Zeus.Server.Hosting;

/// <summary>The one name and version this build reports to the outside world.</summary>
internal static class SoftwareIdentity
{
    /// <summary>Product name, without a version. Apache Labs' build of Zeus.</summary>
    internal const string Name = "ANAN Core";

    /// <summary>This build's version, "" when the assembly carries none.</summary>
    internal static string Version { get; } = ReadVersion();

    /// <summary>Name and version in one field, for networks that take a single string.</summary>
    internal static string NameWithVersion { get; } =
        Version.Length == 0 ? Name : $"{Name} {Version}";

    /// <summary>HTTP User-Agent. Product tokens take no spaces, so the name is hyphenated.</summary>
    internal static string UserAgent { get; } =
        $"{Name.Replace(' ', '-')}/{(Version.Length == 0 ? "1.0" : Version)}";

    private static string ReadVersion()
    {
        var v = typeof(SoftwareIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}
