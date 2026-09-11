using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Twin prevention (field incident 2026-09-10): the update handoff must know
/// which systemd unit it lives in so the new build restarts as that unit's
/// main process, and the instance guard must recognise a Zeus twin by its
/// command line — both outer AppImage runtime and inner binary.
/// </summary>
public sealed class InstanceGuardTests
{
    [Fact]
    public void ParseCgroupPath_UserUnit()
    {
        var (unit, user) = RepoUpdateService.ParseCgroupPath(
            "/user.slice/user-1000.slice/user@1000.service/app.slice/zeus.service");
        Assert.Equal("zeus.service", unit);
        Assert.True(user);
    }

    [Fact]
    public void ParseCgroupPath_SystemUnit()
    {
        var (unit, user) = RepoUpdateService.ParseCgroupPath("/system.slice/zeus.service");
        Assert.Equal("zeus.service", unit);
        Assert.False(user);
    }

    [Fact]
    public void ParseCgroupPath_NotAService()
    {
        // A terminal launch lands in a session scope, not a service: no unit.
        var (unit, _) = RepoUpdateService.ParseCgroupPath(
            "/user.slice/user-1000.slice/user@1000.service/app.slice/app-org.gnome.Terminal.slice/vte-spawn-1.scope");
        Assert.Null(unit);
    }

    [Fact]
    public void IsZeusProcess_MatchesBothHalvesOfTheAppImagePair()
    {
        Assert.True(InstanceGuard.IsZeusProcess("/home/pi/Applications/OpenhpsdrZeus.AppImage"));
        Assert.True(InstanceGuard.IsZeusProcess("./OpenhpsdrZeus"));
        Assert.False(InstanceGuard.IsZeusProcess("/usr/lib/chromium/chromium --app=http://localhost:6060"));
        Assert.False(InstanceGuard.IsZeusProcess("/home/pi/github/Saturn/sw_projects/P2_app/p2app"));
    }
}
