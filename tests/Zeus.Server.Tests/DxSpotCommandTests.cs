// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

using Zeus.Server;

namespace Zeus.Server.Tests;

public class DxSpotCommandTests
{
    [Fact]
    public void Builds_node_spot_command_in_khz_with_one_decimal()
    {
        Assert.True(DxClusterManagementService.TryBuildSpotCommand(14_025_300, "w1aw", "tnx qso", out var cmd, out var err));
        Assert.Null(err);
        Assert.Equal("DX 14025.3 W1AW tnx qso", cmd);
    }

    [Fact]
    public void Omits_empty_comment()
    {
        Assert.True(DxClusterManagementService.TryBuildSpotCommand(7_074_000, "DL1ABC/P", "  ", out var cmd, out _));
        Assert.Equal("DX 7074.0 DL1ABC/P", cmd);
    }

    [Fact]
    public void Strips_control_characters_so_a_comment_cannot_inject_a_second_line()
    {
        Assert.True(DxClusterManagementService.TryBuildSpotCommand(14_200_000, "K1ABC", "hi\r\nset/password x", out var cmd, out _));
        Assert.DoesNotContain("\r", cmd);
        Assert.DoesNotContain("\n", cmd);
        Assert.StartsWith("DX 14200.0 K1ABC hiset/passwor", cmd);
    }

    [Fact]
    public void Caps_comment_length()
    {
        var longText = new string('x', 80);
        Assert.True(DxClusterManagementService.TryBuildSpotCommand(14_200_000, "K1ABC", longText, out var cmd, out _));
        Assert.Equal("DX 14200.0 K1ABC " + new string('x', DxClusterManagementService.MaxSpotCommentLength), cmd);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AB")]
    [InlineData("NODIGIT")]
    [InlineData("W1AW;X")]
    [InlineData("W1 AW")]
    public void Rejects_bad_callsigns(string call)
    {
        Assert.False(DxClusterManagementService.TryBuildSpotCommand(14_200_000, call, null, out _, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void Rejects_out_of_range_frequency()
    {
        Assert.False(DxClusterManagementService.TryBuildSpotCommand(50, "W1AW", null, out _, out var err));
        Assert.Equal("frequency out of range", err);
    }
}
