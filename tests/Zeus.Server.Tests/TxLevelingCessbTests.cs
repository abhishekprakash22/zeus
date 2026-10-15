// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

using System.Text.Json;
using Zeus.Contracts;

namespace Zeus.Server.Tests;

public class TxLevelingCessbTests
{
    [Fact]
    public void CESSB_defaults_on_and_rides_with_the_compressor()
    {
        Assert.True(new TxLevelingConfig().CessbEnabled);
        Assert.False(new TxLevelingConfig().CompressorEnabled);
    }

    [Fact]
    public void Older_JSON_without_the_field_reads_as_CESSB_on()
    {
        // Stored profiles / requests written before CESSB was a setting.
        var cfg = JsonSerializer.Deserialize<TxLevelingConfig>(
            "{\"alcMaxGainDb\":3,\"alcDecayMs\":10,\"levelerEnabled\":true,\"levelerDecayMs\":100,\"compressorEnabled\":true,\"compressorGainDb\":8}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(cfg.CessbEnabled);
        Assert.True(cfg.CompressorEnabled);
    }

    [Fact]
    public void The_choice_round_trips()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var off = new TxLevelingConfig(CompressorEnabled: true, CompressorGainDb: 6, CessbEnabled: false);
        var back = JsonSerializer.Deserialize<TxLevelingConfig>(JsonSerializer.Serialize(off, web), web)!;
        Assert.Equal(off, back);
    }
}
