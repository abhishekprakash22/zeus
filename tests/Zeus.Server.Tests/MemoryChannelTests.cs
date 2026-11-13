// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

using Zeus.Server;

namespace Zeus.Server.Tests;

public class MemoryChannelTests
{
    private const long Now = 1_800_000_000_000;

    private static MemoryChannel Ch(long hz, string mode = "USB", string? id = null, string? name = null, long created = 0) =>
        new(id, name, hz, mode, 100, 2800, created);

    [Fact]
    public void Normalize_assigns_id_and_default_name()
    {
        var m = MemoryChannelEndpoints.Normalize(Ch(14_250_000, "usb"), Now);
        Assert.NotNull(m);
        Assert.False(string.IsNullOrEmpty(m!.Id));
        Assert.Equal("USB", m.Mode);
        Assert.Equal("14.250 USB", m.Name);
        Assert.Equal(Now, m.CreatedUnixMs);
    }

    [Fact]
    public void Normalize_rejects_unknown_mode_and_absurd_frequency()
    {
        Assert.Null(MemoryChannelEndpoints.Normalize(Ch(14_250_000, "XYZ"), Now));
        Assert.Null(MemoryChannelEndpoints.Normalize(Ch(5), Now));
    }

    [Fact]
    public void Normalize_drops_an_inverted_filter_but_keeps_the_channel()
    {
        var m = MemoryChannelEndpoints.Normalize(new MemoryChannel(null, "x", 7_100_000, "LSB", 500, -2000, 0), Now);
        Assert.NotNull(m);
        Assert.Null(m!.FilterLowHz);
        Assert.Null(m.FilterHighHz);
    }

    [Fact]
    public void Normalize_trims_and_caps_name()
    {
        var m = MemoryChannelEndpoints.Normalize(Ch(7_100_000, name: "  " + new string('n', 60) + "\t"), Now);
        Assert.Equal(MemoryChannelEndpoints.MaxNameLength, m!.Name!.Length);
    }

    [Fact]
    public void Add_replaces_same_frequency_and_mode_instead_of_stacking()
    {
        var list = MemoryChannelEndpoints.Add(new List<MemoryChannel>(), Ch(14_250_000, name: "net"), Now);
        list = MemoryChannelEndpoints.Add(list, Ch(14_250_005, name: "net 2"), Now);
        var only = Assert.Single(list);
        Assert.Equal("net 2", only.Name);
        // Same frequency, different mode is a different channel.
        list = MemoryChannelEndpoints.Add(list, Ch(14_250_000, "CWU"), Now);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void Add_drops_oldest_when_full_and_keeps_frequency_order()
    {
        var list = new List<MemoryChannel>();
        for (int i = 0; i < MemoryChannelEndpoints.MaxChannels; i++)
            list = MemoryChannelEndpoints.Add(list, Ch(7_000_000 + i * 1000L, id: $"c{i}", created: 1000 + i), Now);
        list = MemoryChannelEndpoints.Add(list, Ch(1_900_000, id: "new", created: Now), Now);
        Assert.Equal(MemoryChannelEndpoints.MaxChannels, list.Count);
        Assert.DoesNotContain(list, x => x.Id == "c0");
        Assert.Equal("new", list[0].Id);
        Assert.True(list.Zip(list.Skip(1)).All(p => p.First.Hz <= p.Second.Hz));
    }

    [Fact]
    public void NormalizeList_drops_junk_and_duplicate_ids()
    {
        var list = MemoryChannelEndpoints.NormalizeList(new MemoryChannel?[]
        {
            Ch(14_000_000, id: "a"),
            null,
            Ch(14_100_000, id: "a"),
            Ch(14_200_000, "BOGUS", id: "b"),
        }, Now);
        var only = Assert.Single(list);
        Assert.Equal(14_000_000, only.Hz);
    }
}
