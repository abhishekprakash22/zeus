// SPDX-License-Identifier: GPL-2.0-or-later
//
// The support sidecar is launched unconditionally. In STANDALONE builds (the
// default) it must not dial upstream's dead broker: with no broker it runs the
// local-only crash capturer. ZEUS_REMOTE_BROKER_URL still opts in.

using OpenhpsdrZeus;
using Xunit;

namespace Zeus.Server.Tests;

public class SupportSidecarBrokerTests
{
    [Fact]
    public void Standalone_WithNoOverride_HasNoBroker() =>
        Assert.Null(SupportSidecarLauncher.BrokerUrl(envUrl: null, standalone: true));

    [Fact]
    public void AnExplicitBroker_AlwaysWins() =>
        Assert.Equal("wss://broker.example/signal?role=host",
            SupportSidecarLauncher.BrokerUrl(" wss://broker.example/signal?role=host ", standalone: true));

    [Fact]
    public void OutsideStandalone_KeepsTheUpstreamDefault() =>
        Assert.Contains("/signal?role=host", SupportSidecarLauncher.BrokerUrl("", standalone: false));
}
