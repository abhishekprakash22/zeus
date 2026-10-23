// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

using System.Text.Json;
using Zeus.Server;
// The request records live nested inside UiPrefsEndpoints (like the file's
// other request records), so name them through the class.
using G2CardBox = Zeus.Server.UiPrefsEndpoints.G2CardBox;
using G2CardLayout = Zeus.Server.UiPrefsEndpoints.G2CardLayout;

namespace Zeus.Server.Tests;

public class UiG2CardsTests
{
    [Fact]
    public void Keeps_sane_boxes_and_hidden_ids()
    {
        var layout = UiPrefsEndpoints.NormalizeG2Cards(new G2CardLayout(
            new Dictionary<string, G2CardBox>
            {
                ["filter-a"] = new(420, 60, 560, 300),
                ["smeter"] = new(-24, 60, 300, 260),
            },
            new List<string> { "filter-b", "filter-b" }));
        Assert.Equal(2, layout.Boxes!.Count);
        Assert.Equal(420, layout.Boxes!["filter-a"].X);
        Assert.Equal(new[] { "filter-b" }, layout.Hidden!);   // de-duplicated
    }

    [Fact]
    public void Refuses_junk()
    {
        var layout = UiPrefsEndpoints.NormalizeG2Cards(new G2CardLayout(
            new Dictionary<string, G2CardBox>
            {
                ["nan"] = new(double.NaN, 0, 100, 100),
                ["neg"] = new(0, 0, -5, 100),
                ["huge"] = new(1e9, 0, 100, 100),
                [new string('x', 64)] = new(0, 0, 100, 100),
            },
            new List<string> { "", new string('y', 64) }));
        Assert.Empty(layout.Boxes!);
        Assert.Empty(layout.Hidden!);
        Assert.Empty(UiPrefsEndpoints.NormalizeG2Cards(null).Boxes!);
    }

    [Fact]
    public void Round_trips_through_JSON_in_the_web_shape()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = "{\"boxes\":{\"filter-a\":{\"x\":10,\"y\":20,\"w\":300,\"h\":200}},\"hidden\":[\"smeter\"]}";
        var layout = UiPrefsEndpoints.NormalizeG2Cards(JsonSerializer.Deserialize<G2CardLayout>(json, web));
        Assert.Equal(300, layout.Boxes!["filter-a"].W);
        Assert.Equal(new[] { "smeter" }, layout.Hidden!);
    }
}
