// SPDX-License-Identifier: GPL-2.0-or-later
//
// Chat's relay: upstream's chat.openhpsdrzeus.com no longer resolves, so in
// STANDALONE builds (the default) chat has no relay unless one is configured,
// and the panel says why instead of reconnecting forever to a dead host.

using Xunit;

namespace Zeus.Server.Tests;

public class ChatRelayAvailabilityTests
{
    [Fact]
    public void Standalone_WithNoRelayConfigured_HasNoRelay() =>
        Assert.Null(ChatService.ResolveRelayUrl(envUrl: null, standalone: true));

    [Fact]
    public void AnExplicitRelay_AlwaysWins()
    {
        Assert.Equal("wss://chat.example/chat", ChatService.ResolveRelayUrl("  wss://chat.example/chat ", standalone: true));
        Assert.Equal("wss://chat.example/chat", ChatService.ResolveRelayUrl("wss://chat.example/chat", standalone: false));
    }

    [Fact]
    public void OutsideStandalone_FallsBackToUpstreamsRelay() =>
        Assert.Equal(ChatService.DefaultRelayUrl, ChatService.ResolveRelayUrl("", standalone: false));

    [Fact]
    public void TheReasonShownToOperators_SaysChatIsUnavailable() =>
        Assert.Contains("isn't available", ChatService.NoRelayError);
}
