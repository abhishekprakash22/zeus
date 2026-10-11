// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

using Xunit;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class UiWfSpeedTests
{
    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(4.0, 4.0)]
    [InlineData(3.0, 1.0)]
    [InlineData(0.0, 1.0)]
    [InlineData(-2.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void NormalizeWfSpeedFactor_KeepsOnlyTheSpdSteps(double raw, double expected)
    {
        Assert.Equal(expected, UiPrefsEndpoints.NormalizeWfSpeedFactor(raw));
    }
}
