// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

using Xunit;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class UiEffectsTests
{
    [Theory]
    [InlineData("off", "off")]
    [InlineData("subtle", "subtle")]
    [InlineData("full", "full")]
    [InlineData("FULL", "subtle")]
    [InlineData("", "subtle")]
    [InlineData(null, "subtle")]
    [InlineData("max", "subtle")]
    public void NormalizeEffectsLevel_KeepsOnlyTheThreeLevels(string? raw, string expected)
    {
        Assert.Equal(expected, UiPrefsEndpoints.NormalizeEffectsLevel(raw));
    }
}
