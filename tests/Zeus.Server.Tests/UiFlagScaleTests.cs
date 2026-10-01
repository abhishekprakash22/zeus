// SPDX-License-Identifier: GPL-2.0-or-later
using Xunit;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class UiFlagScaleTests
{
    [Theory]
    [InlineData(0.5, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(2.0, 2.0)]
    [InlineData(9.0, 2.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void ClampFlagScale_KeepsTheFlagBetweenOneAndTwo(double raw, double expected)
    {
        Assert.Equal(expected, UiPrefsEndpoints.ClampFlagScale(raw));
    }
}
