// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed partial class RepoUpdateService
{
    private const string ForceReasonDowngrade = "downgrade";
    private readonly object _versionFloorLock = new();
    private bool _versionFloorInitialized;
    private string? _startupForceReason;
    private string? _startupRequiredVersion;

    internal readonly record struct VersionFloorDecision(
        bool ForceUpdate,
        string? ForceReason,
        string? RequiredVersion,
        string? PersistVersion);

    internal readonly record struct LinuxReleaseArtifact(string Name, bool IsDirectory);

    internal static VersionFloorDecision EvaluateVersionFloor(string? storedFloor, string currentVersion)
    {
        var current = NullIfEmpty(currentVersion);
        if (current is null || !TryParseReleaseVersion(current, out _))
            return new VersionFloorDecision(false, null, null, null);

        var stored = NullIfEmpty(storedFloor ?? string.Empty);
        if (stored is not null && TryParseReleaseVersion(stored, out _) && IsReleaseNewer(stored, current))
        {
            return new VersionFloorDecision(
                ForceUpdate: true,
                ForceReason: ForceReasonDowngrade,
                RequiredVersion: stored,
                PersistVersion: stored);
        }

        return new VersionFloorDecision(false, null, null, current);
    }

    internal static IReadOnlyList<LinuxReleaseArtifact> SelectObsoleteLinuxReleaseArtifacts(
        IEnumerable<LinuxReleaseArtifact> entries,
        string runningVersion)
    {
        if (!TryParseReleaseVersion(runningVersion, out var current))
            return Array.Empty<LinuxReleaseArtifact>();

        var selected = new List<LinuxReleaseArtifact>();
        foreach (var entry in entries)
        {
            if (!TryGetLinuxArtifactVersion(entry, out var artifactVersion))
                continue;
            if (artifactVersion.CompareTo(current) < 0)
                selected.Add(entry);
        }

        return selected;
    }

    private RepoUpdateStatus ApplyStartupPolicy(RepoUpdateStatus status)
    {
        if (status.IsGitRepo) return status;

        // STANDALONE: the force-update wall (App.tsx -> <ForceUpdateGate/>) sends the
        // operator to DownloadsPageUrl (openhpsdrzeus.com/download) and offers no way
        // past it. The "downgrade" reason is decided purely from a locally stored
        // version high-water mark, so installing an older build than one previously
        // run on this machine is enough to trigger it. With the download host
        // unavailable, that wall is unrecoverable: it demands an upgrade that cannot
        // be fetched. A local build must not lock its operator out on that basis.
        //
        // This does not disable update *checking* — RepoUpdateService still reports
        // status normally, and the Updates panel still works if a manifest URL is
        // reachable (see ZEUS_UPDATE_MANIFEST_URL). It only declines to make the
        // result a blocking wall.
        // NOTE: this file is in namespace Zeus.Server, while ZeusStandalone lives in
        // the child namespace Zeus.Server.Hosting, so the reference is fully
        // qualified (same as ZeusHost.cs).
        if (Zeus.Server.Hosting.ZeusStandalone.Enabled) return status;

        EnsureVersionFloorInitialized();
        if (_startupForceReason is null) return status;

        return status with
        {
            ForceUpdate = true,
            ForceReason = _startupForceReason,
            MinVersion = _startupRequiredVersion,
            UpdateAction = "openRelease",
            ReleaseUrl = DownloadsPageUrl,
        };
    }

    private void EnsureVersionFloorInitialized()
    {
        if (_repoRoot is not null) return;

        lock (_versionFloorLock)
        {
            if (_versionFloorInitialized) return;
            _versionFloorInitialized = true;

            try
            {
                var path = PrefsDbPath.VersionFloorPath();
                string? stored = null;
                try
                {
                    if (File.Exists(path))
                        stored = File.ReadAllText(path).Trim();
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "version floor read failed for {Path}", path);
                }

                var decision = EvaluateVersionFloor(stored, InstalledVersion());
                _startupForceReason = decision.ForceReason;
                _startupRequiredVersion = decision.RequiredVersion;

                if (decision.PersistVersion is { Length: > 0 })
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllText(path, decision.PersistVersion + Environment.NewLine);
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "version floor write failed for {Path}", path);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "version floor guard failed");
                _startupForceReason = null;
                _startupRequiredVersion = null;
            }
        }
    }

    private void RunLinuxFirstRunCleanup()
    {
        if (_repoRoot is not null) return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZEUS_PREFS_PATH"))) return;

        try
        {
            var cleanupRoot = ResolveLinuxCleanupRoot();
            if (cleanupRoot is null || !Directory.Exists(cleanupRoot)) return;

            var entries = Directory.EnumerateFileSystemEntries(cleanupRoot)
                .Select(path => new LinuxReleaseArtifact(
                    Path.GetFileName(path) ?? string.Empty,
                    Directory.Exists(path)))
                .ToList();
            var obsolete = SelectObsoleteLinuxReleaseArtifacts(entries, InstalledVersion());

            foreach (var entry in obsolete)
            {
                var path = Path.Combine(cleanupRoot, entry.Name);
                try
                {
                    var attrs = File.GetAttributes(path);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        _log.LogInformation("Skipping old Zeus artifact symlink {Path}", path);
                        continue;
                    }

                    if (entry.IsDirectory)
                        Directory.Delete(path, recursive: true);
                    else
                        File.Delete(path);

                    _log.LogInformation("Deleted old Zeus Linux artifact {Path}", path);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "old Zeus Linux artifact cleanup failed for {Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Linux first-run cleanup failed");
        }
    }

    private static string? ResolveLinuxCleanupRoot()
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImage))
        {
            var appImageDir = Path.GetDirectoryName(Path.GetFullPath(appImage));
            return string.IsNullOrWhiteSpace(appImageDir) ? null : appImageDir;
        }

        var exeDir = new DirectoryInfo(AppContext.BaseDirectory);
        if (!TryGetLinuxArtifactVersion(new LinuxReleaseArtifact(exeDir.Name, IsDirectory: true), out _))
            return null;
        return exeDir.Parent?.FullName;
    }

    private static bool TryGetLinuxArtifactVersion(LinuxReleaseArtifact entry, out Version version)
    {
        version = new Version(0, 0, 0);
        var match = entry.IsDirectory
            ? LinuxTarballDirPattern().Match(entry.Name)
            : LinuxAppImagePattern().Match(entry.Name);
        if (!match.Success && !entry.IsDirectory)
            match = LinuxTarballArchivePattern().Match(entry.Name);
        if (!match.Success) return false;
        return TryParseReleaseVersion(match.Groups["version"].Value, out version);
    }

    [GeneratedRegex(@"^OpenhpsdrZeus(?:-Server)?-(?<version>\d+\.\d+\.\d+)-linux-(?:x86_64|aarch64)\.AppImage$")]
    private static partial Regex LinuxAppImagePattern();

    [GeneratedRegex(@"^openhpsdr-zeus-(?<version>\d+\.\d+\.\d+)-linux-(?:x64|arm64)$", RegexOptions.IgnoreCase)]
    private static partial Regex LinuxTarballDirPattern();

    [GeneratedRegex(@"^openhpsdr-zeus-(?<version>\d+\.\d+\.\d+)-linux-(?:x64|arm64)\.tar\.gz$", RegexOptions.IgnoreCase)]
    private static partial Regex LinuxTarballArchivePattern();
}
