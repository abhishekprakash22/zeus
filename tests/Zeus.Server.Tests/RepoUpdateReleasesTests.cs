// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.

using Zeus.Server;
using Zeus.Server.Hosting.Remote;

namespace Zeus.Server.Tests;

public class RepoUpdateReleasesTests
{
    private const string Json = """
    [
      { "tag_name": "v1.148", "name": "ANAN Core 1.148", "published_at": "2026-10-23T02:30:00Z",
        "draft": false, "prerelease": false, "html_url": "https://github.com/x/y/releases/tag/v1.148",
        "assets": [ { "name": "latest.json", "browser_download_url": "https://dl/v1.148/latest.json" },
                    { "name": "ANAN-Core-1.148-aarch64.AppImage", "browser_download_url": "https://dl/a" } ] },
      { "tag_name": "v1.150-rc1", "draft": false, "prerelease": true, "assets": [] },
      { "tag_name": "v1.149", "draft": true, "prerelease": false, "assets": [] },
      { "tag_name": "v1.146", "name": "ANAN Core 1.146", "draft": false, "prerelease": false,
        "assets": [ { "name": "latest.json", "browser_download_url": "https://dl/v1.146/latest.json" } ] },
      { "tag_name": "v1.147", "draft": false, "prerelease": false, "assets": [] },
      { "tag_name": "nightly", "draft": false, "prerelease": false, "assets": [] }
    ]
    """;

    [Fact]
    public void Lists_published_versions_newest_first_and_skips_drafts_prereleases_and_odd_tags()
    {
        var list = RepoUpdateService.ParseReleases(Json, "1.147");
        Assert.Equal(new[] { "v1.148", "v1.147", "v1.146" }, list.Select(r => r.Tag).ToArray());
    }

    [Fact]
    public void Marks_the_installed_version_and_the_older_ones()
    {
        var list = RepoUpdateService.ParseReleases(Json, "1.147");
        var newer = list.Single(r => r.Tag == "v1.148");
        var same = list.Single(r => r.Tag == "v1.147");
        var older = list.Single(r => r.Tag == "v1.146");
        Assert.False(newer.IsOlder);
        Assert.False(newer.IsInstalled);
        Assert.True(same.IsInstalled);
        Assert.False(same.IsOlder);
        Assert.True(older.IsOlder);
    }

    [Fact]
    public void Picks_each_releases_own_manifest()
    {
        var list = RepoUpdateService.ParseReleases(Json, "1.147");
        Assert.Equal("https://dl/v1.148/latest.json", list.Single(r => r.Tag == "v1.148").ManifestUrl);
        Assert.Null(list.Single(r => r.Tag == "v1.147").ManifestUrl);
        Assert.Equal("1.148", list[0].Version);
    }

    [Fact]
    public void Junk_or_non_array_json_lists_nothing()
    {
        Assert.Empty(RepoUpdateService.ParseReleases("{}", "1.0"));
        Assert.Empty(RepoUpdateService.ParseReleases("[]", "1.0"));
    }

    [Theory]
    [InlineData("v1.146", true)]
    [InlineData("1.146", true)]
    [InlineData("v0.15.197", true)]
    [InlineData("v1.150-rc1", false)]
    [InlineData("../../etc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_plain_version_tags_can_be_installed(string? tag, bool ok)
    {
        Assert.Equal(ok, RepoUpdateService.IsValidReleaseTag(tag));
    }

    [Fact]
    public void A_missing_bak_version_record_reads_as_unknown()
    {
        var path = Path.Combine(Path.GetTempPath(), "anan-" + Guid.NewGuid().ToString("N") + ".AppImage");
        Assert.Null(RepoUpdateService.ReadBakVersion(path));
        File.WriteAllText(path + ".bak.version", "1.147\n");
        try
        {
            Assert.Equal("1.147", RepoUpdateService.ReadBakVersion(path));
        }
        finally
        {
            File.Delete(path + ".bak.version");
        }
    }

    [Theory]
    [InlineData("/api/system/update/install")]
    [InlineData("/api/system/update/rollback")]
    public void Installing_or_rolling_back_is_refused_over_remote(string path)
    {
        Assert.True(RemoteWebRtcSession.IsDenied(path, mutating: true));
    }

    [Fact]
    public void The_release_list_is_readable_over_remote()
    {
        Assert.False(RemoteWebRtcSession.IsDenied("/api/system/update/releases", mutating: false));
    }
}
