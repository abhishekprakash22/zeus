// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1.Tests;

public class DiscoverySocketPlanTests
{
    [Fact]
    public void Builds_PerInterfaceDirectedBroadcastPlans()
    {
        var plans = RadioDiscoveryService.BuildSocketPlan(
        [
            new RadioDiscoveryService.DiscoveryInterfaceDescriptor(
                IPAddress.Parse("169.254.10.20"),
                IPAddress.Parse("255.255.0.0")),
            new RadioDiscoveryService.DiscoveryInterfaceDescriptor(
                IPAddress.Parse("192.168.44.10"),
                IPAddress.Parse("255.255.255.0"))
        ]);

        Assert.Collection(
            plans,
            p =>
            {
                Assert.Equal(IPAddress.Parse("169.254.10.20"), p.BindAddress);
                Assert.Equal(IPAddress.Parse("169.254.255.255"), p.TargetAddress);
            },
            p =>
            {
                Assert.Equal(IPAddress.Parse("192.168.44.10"), p.BindAddress);
                Assert.Equal(IPAddress.Parse("192.168.44.255"), p.TargetAddress);
            },
            p =>
            {
                Assert.Equal(IPAddress.Any, p.BindAddress);
                Assert.Equal(IPAddress.Broadcast, p.TargetAddress);
            });
    }

    [Fact]
    public void EmptyInterfaces_FallsBackToLimitedBroadcastFromAny()
    {
        var plan = Assert.Single(RadioDiscoveryService.BuildSocketPlan([]));

        Assert.Equal(IPAddress.Any, plan.BindAddress);
        Assert.Equal(IPAddress.Broadcast, plan.TargetAddress);
    }
}
