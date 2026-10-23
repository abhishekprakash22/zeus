// SPDX-License-Identifier: GPL-2.0-or-later
//
// ANAN Core — Protocol-2 SDR for the ANAN.
// Copyright (C) 2026 Apache Labs.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Going back to an earlier release.
//
//   Roll back to previous — instant and offline: every update keeps the image
//   it replaced as <AppImage>.bak (its version recorded beside it); rolling
//   back swaps the two, so the newer build becomes the .bak and the watchdog
//   can restore it if the older one never comes up.
//
//   Install an earlier release — the fork's GitHub releases are listed
//   (newest first, drafts and pre-releases skipped); picking one downloads that
//   release's own latest.json manifest and installs its platform asset through
//   the same verified download → swap → supervised restart as an update.
//
// Either way the version-floor high-water mark is lowered to the target, or
// the older build would come up behind the "downgrade" force-update wall.
// Both are local-only maintenance (RemoteWebRtcSession.WriteDeniedPrefixes).

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Zeus.Server;

public sealed record ReleaseEntry(
    string Tag,
    string Version,
    string? Name,
    string? PublishedAt,
    string? ManifestUrl,
    string? PageUrl,
    bool IsInstalled,
    bool IsOlder);

public sealed record UpdateInstallRequest(string? Tag);

public sealed record ReleaseListResult(
    string InstalledVersion,
    bool CanInstall,
    string? PreviousVersion,
    bool CanRollBack,
    IReadOnlyList<ReleaseEntry> Releases,
    string? Error);

public sealed partial class RepoUpdateService
{
    internal const string DefaultReleasesApiUrl =
        "https://api.github.com/repos/abhishekprakash22/zeus/releases?per_page=20";
    internal const string DefaultReleaseDownloadBase =
        "https://github.com/abhishekprakash22/zeus/releases/download/";

    private readonly object _releasesLock = new();
    private Dictionary<string, string> _manifestByTag = new(StringComparer.OrdinalIgnoreCase);

    private static string ReleasesApiUrl()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_UPDATE_RELEASES_URL");
        return string.IsNullOrWhiteSpace(env) ? DefaultReleasesApiUrl : env.Trim();
    }

    [GeneratedRegex(@"^v?\d+\.\d+(?:\.\d+)?$")]
    private static partial Regex ReleaseTagPattern();

    internal static bool IsValidReleaseTag(string? tag) =>
        !string.IsNullOrWhiteSpace(tag) && ReleaseTagPattern().IsMatch(tag.Trim());

    internal static bool SameRelease(string? a, string? b) =>
        TryParseReleaseVersion(a, out var x) && TryParseReleaseVersion(b, out var y) && x.CompareTo(y) == 0;

    /// <summary>
    /// GitHub's releases JSON → entries, newest first; drafts, pre-releases and
    /// tags that are not plain versions are skipped. Pure; exported for tests.
    /// </summary>
    internal static IReadOnlyList<ReleaseEntry> ParseReleases(string json, string installedVersion)
    {
        var list = new List<ReleaseEntry>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            if (r.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True) continue;
            var tag = r.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (!IsValidReleaseTag(tag)) continue;
            var version = tag!.Trim().TrimStart('v', 'V');
            string? manifest = null;
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    if (a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        && string.Equals(n.GetString(), "latest.json", StringComparison.OrdinalIgnoreCase)
                        && a.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String)
                    {
                        manifest = u.GetString();
                        break;
                    }
                }
            }
            string? Str(string name) =>
                r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            list.Add(new ReleaseEntry(
                tag.Trim(),
                version,
                Str("name"),
                Str("published_at"),
                manifest,
                Str("html_url"),
                SameRelease(version, installedVersion),
                IsReleaseNewer(installedVersion, version)));
        }
        list.Sort((x, y) =>
        {
            bool okX = TryParseReleaseVersion(x.Version, out var vx);
            bool okY = TryParseReleaseVersion(y.Version, out var vy);
            return okX && okY ? vy.CompareTo(vx) : 0;
        });
        return list;
    }

    /// <summary>The recent releases, plus what "roll back to previous" would restore.</summary>
    public async Task<ReleaseListResult> GetReleasesAsync(CancellationToken ct)
    {
        string installed = InstalledVersion();
        string? appImage = ResolveAppImagePath();
        string? previous = appImage is null ? null : ReadBakVersion(appImage);
        bool canRollBack = appImage is not null && File.Exists(appImage + ".bak");
        try
        {
            using var resp = await _httpClientFactory.CreateClient("ZeusUpdates")
                .GetAsync(ReleasesApiUrl(), ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var releases = ParseReleases(json, installed);
            lock (_releasesLock)
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in releases)
                {
                    if (r.ManifestUrl is { Length: > 0 }) map[r.Tag] = r.ManifestUrl;
                }
                _manifestByTag = map;
            }
            return new ReleaseListResult(installed, appImage is not null, previous, canRollBack, releases, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "release list fetch failed");
            return new ReleaseListResult(installed, appImage is not null, previous, canRollBack,
                Array.Empty<ReleaseEntry>(), $"could not list releases (offline?): {Trunc(ex.Message)}");
        }
    }

    /// <summary>Install a specific release (older or newer) in place. False when it cannot start.</summary>
    public bool BeginInstallRelease(string? tag)
    {
        if (!IsValidReleaseTag(tag))
        {
            SetApply("failed", 0, error: "not a release tag");
            return false;
        }
        lock (_applyLock)
        {
            if (_applyTask is { IsCompleted: false }) return true;
        }
        string? appImage = ResolveAppImagePath();
        if (appImage is null)
        {
            SetApply("unsupported", 0, error:
                "no AppImage found to replace — installing an earlier release in place needs the AppImage; "
                + "on other platforms, download it from the release page");
            return false;
        }
        string t = tag!.Trim();
        SetApply("downloading", 0, t.TrimStart('v', 'V'));
        var task = Task.Run(() => InstallReleaseAsync(appImage, t));
        lock (_applyLock) _applyTask = task;
        return true;
    }

    private async Task InstallReleaseAsync(string appImagePath, string tag)
    {
        string version = tag.TrimStart('v', 'V');
        try
        {
            string? manifestUrl;
            lock (_releasesLock) _manifestByTag.TryGetValue(tag, out manifestUrl);
            manifestUrl ??= DefaultReleaseDownloadBase + Uri.EscapeDataString(tag) + "/latest.json";

            var http = _httpClientFactory.CreateClient("ZeusUpdates");
            using var resp = await http.GetAsync(manifestUrl).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var manifest = JsonSerializer.Deserialize<ZeusDownloadManifest>(
                await resp.Content.ReadAsStringAsync().ConfigureAwait(false), ManifestJsonOptions);
            if (manifest is null)
            {
                SetApply("failed", 0, version, $"release {tag} has no readable manifest");
                return;
            }
            var asset = SelectDownloadAsset(
                manifest.Assets,
                RuntimePlatform(),
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture,
                IsRunningFromAppImage(),
                IsServerMode());
            if (asset?.Url is not { Length: > 0 } url)
            {
                SetApply("failed", 0, version, $"release {tag} has no asset for this platform");
                return;
            }
            string target = NullIfEmpty(manifest.Version ?? string.Empty) ?? version;
            _log.LogInformation("self-update: installing chosen release {Tag} (v{Version})", tag, target);
            await InstallTargetAsync(appImagePath, url, target, asset.Size, FormatDigest(asset.Sha256),
                lowerVersionFloor: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "self-update: installing release {Tag} failed", tag);
            SetApply("failed", 0, version, ex.Message);
        }
    }

    /// <summary>Swap back to the image the last update replaced (no download). False when there is none.</summary>
    public bool BeginRollBackToPrevious()
    {
        lock (_applyLock)
        {
            if (_applyTask is { IsCompleted: false }) return true;
        }
        string? appImage = ResolveAppImagePath();
        if (appImage is null || !File.Exists(appImage + ".bak"))
        {
            SetApply("unsupported", 0, error: "no previous version is kept on this radio");
            return false;
        }
        string? previous = ReadBakVersion(appImage);
        SetApply("swapping", 100, previous);
        var task = Task.Run(() => RollBackAsync(appImage, previous));
        lock (_applyLock) _applyTask = task;
        return true;
    }

    private async Task RollBackAsync(string appImagePath, string? previousVersion)
    {
        try
        {
            string bakPath = appImagePath + ".bak";
            string swapPath = appImagePath + ".swap";
            string? running = NullIfEmpty(InstalledVersion());
            // current → .swap, .bak → current, .swap → .bak: the newer build is
            // now the .bak, so the watchdog restores it if the older one fails.
            File.Move(appImagePath, swapPath, overwrite: true);
            File.Move(bakPath, appImagePath, overwrite: true);
            File.Move(swapPath, bakPath, overwrite: true);
            WriteBakVersion(appImagePath, running);
            WriteVersionFloor(previousVersion);
            _log.LogInformation("self-update: rolled back {Path} to v{Version}", appImagePath, previousVersion ?? "?");
            await HandOffAsync(appImagePath, bakPath, previousVersion).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "self-update: roll back failed");
            SetApply("failed", 0, previousVersion, ex.Message);
        }
    }

    private static string BakVersionPath(string appImagePath) => appImagePath + ".bak.version";

    /// <summary>The version of the kept .bak image, when recorded.</summary>
    internal static string? ReadBakVersion(string appImagePath)
    {
        try
        {
            var p = BakVersionPath(appImagePath);
            return File.Exists(p) ? NullIfEmpty(File.ReadAllText(p)) : null;
        }
        catch
        {
            return null;
        }
    }

    private void WriteBakVersion(string appImagePath, string? version)
    {
        try
        {
            var p = BakVersionPath(appImagePath);
            if (version is null) File.Delete(p);
            else File.WriteAllText(p, version + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "bak version record failed");
        }
    }

    /// <summary>
    /// Lower the version-floor high-water mark to an operator-chosen version
    /// (unknown → remove it), so that build starts without the downgrade wall.
    /// </summary>
    private void WriteVersionFloor(string? version)
    {
        try
        {
            var path = PrefsDbPath.VersionFloorPath();
            if (string.IsNullOrWhiteSpace(version))
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, version.Trim() + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "version floor lower failed");
        }
    }
}
