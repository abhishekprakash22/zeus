// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Plugins.Contracts;

namespace Zeus.Server;

internal sealed class PluginOperatorIdentityProvider : IOperatorIdentityProvider
{
    private readonly OperatorIdentityStore _identity;
    private readonly QrzService _qrz;

    public PluginOperatorIdentityProvider(OperatorIdentityStore identity, QrzService qrz)
    {
        _identity = identity;
        _qrz = qrz;
    }

    public OperatorIdentitySnapshot Resolve(string? secondaryCall = null, string? secondaryGrid = null)
    {
        var (call, grid) = OperatorIdentityResolver.Resolve(
            _identity, _qrz, secondaryCall, secondaryGrid);
        return new OperatorIdentitySnapshot(call, grid);
    }
}
