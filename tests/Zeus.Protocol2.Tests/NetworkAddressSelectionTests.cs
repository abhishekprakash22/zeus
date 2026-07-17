// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;

namespace Zeus.Protocol2.Tests;

public class NetworkAddressSelectionTests
{
    [Fact]
    public void Finds_LinkLocal_SubnetMatch()
    {
        var radio = IPAddress.Parse("169.254.177.168");
        var local = IPAddress.Parse("169.254.12.34");

        var selected = NetworkAddressSelection.FindLocalAddressForSubnet(
            radio,
            [new LocalIpv4Address(local, IPAddress.Parse("255.255.0.0"))]);

        Assert.Equal(local, selected);
    }

    [Fact]
    public void Finds_GeneralPrivateSubnetMatch()
    {
        var radio = IPAddress.Parse("192.168.44.77");
        var wired = IPAddress.Parse("192.168.44.10");

        var selected = NetworkAddressSelection.FindLocalAddressForSubnet(
            radio,
            [
                new LocalIpv4Address(IPAddress.Parse("10.20.30.40"), IPAddress.Parse("255.255.255.0")),
                new LocalIpv4Address(wired, IPAddress.Parse("255.255.255.0"))
            ]);

        Assert.Equal(wired, selected);
    }

    [Fact]
    public void NoMatch_ReturnsNull_ForAnyBindFallback()
    {
        var selected = NetworkAddressSelection.FindLocalAddressForSubnet(
            IPAddress.Parse("172.16.20.5"),
            [new LocalIpv4Address(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"))]);

        Assert.Null(selected);
    }
}
