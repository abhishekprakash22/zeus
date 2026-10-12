// SPDX-License-Identifier: GPL-2.0-or-later
//
// "Update now" falls back to DownloadsPageUrl when the manifest has no asset
// for this platform. It pointed at upstream's openhpsdrzeus.com, which no
// longer resolves (field: v1.130, Win 11). It must be the fork's own releases
// page, which always exists.

using Xunit;

namespace Zeus.Server.Tests;

public class UpdateDownloadsPageTests
{
    [Fact]
    public void DownloadsPage_IsTheForksReleasesPage()
    {
        Assert.Equal("https://github.com/abhishekprakash22/zeus/releases/latest", RepoUpdateService.DownloadsPageUrl);
        Assert.DoesNotContain("openhpsdrzeus.com", RepoUpdateService.DownloadsPageUrl);
    }

    [Fact]
    public void DownloadsPage_IsOnTheSameRepoAsTheUpdateManifest()
    {
        // Both must name the same repository, or the fallback would send
        // operators somewhere the updater doesn't publish.
        var repo = "github.com/abhishekprakash22/zeus/releases/";
        Assert.Contains(repo, RepoUpdateService.DefaultManifestUrl);
        Assert.Contains(repo, RepoUpdateService.DownloadsPageUrl);
    }
}
