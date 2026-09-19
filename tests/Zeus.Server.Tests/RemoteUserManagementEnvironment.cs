// SPDX-License-Identifier: GPL-2.0-or-later
//
// The remote user-management path (RemoteUserAccessClient + the access gate's
// broker branch) is off by default in this build: ZeusStandalone forces it off
// unless ZEUS_STANDALONE=0. Tests that exercise that path turn it back on for
// their duration. The switch is a process-wide environment variable, so those
// tests share one collection that never runs in parallel with anything else.

namespace Zeus.Server.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RemoteUserManagementCollection
{
    public const string Name = "Remote user management (ZEUS_STANDALONE=0)";
}

/// <summary>Enables the non-standalone remote user-management path; restores the environment on dispose.</summary>
internal sealed class RemoteUserManagementEnvironment : IDisposable
{
    private readonly string? _standalone = Environment.GetEnvironmentVariable("ZEUS_STANDALONE");
    private readonly string? _remoteUsers = Environment.GetEnvironmentVariable("ZEUS_REMOTE_USER_MANAGEMENT");

    public RemoteUserManagementEnvironment()
    {
        Environment.SetEnvironmentVariable("ZEUS_STANDALONE", "0");
        Environment.SetEnvironmentVariable("ZEUS_REMOTE_USER_MANAGEMENT", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ZEUS_STANDALONE", _standalone);
        Environment.SetEnvironmentVariable("ZEUS_REMOTE_USER_MANAGEMENT", _remoteUsers);
    }
}
