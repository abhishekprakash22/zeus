// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// In-place AppImage self-update (field report: the update flow opened a
// browser page and left the operator to download, move, and chmod the file
// by hand). The AppImage runtime hands a running image its own on-disk path
// in $APPIMAGE, and the release manifest carries URL + size + sha256 — so
// the server can do the whole ritual itself: stream the new image to a
// sibling temp file with progress, verify the digest, set the exec bit,
// atomically swap it over the current file (same directory, same
// filesystem), then hand off through a tiny detached shell that waits for
// this process to exit (freeing the listen port) before exec'ing the new
// image. The frontend polls, watches the server die and come back, and
// reloads.

using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

// NOTE: RepoUpdateService is declared in `namespace Zeus.Server` even though
// it lives in the Hosting project — the partial must match or it silently
// becomes an unrelated class (the v0.15.22 CI failure: eight 'no definition'
// errors, all one namespace).
namespace Zeus.Server;

public sealed partial class RepoUpdateService
{
    public sealed record ApplyStatus(
        string Phase,          // idle | downloading | verifying | swapping | restarting | failed | unsupported
        double Percent,
        string? TargetVersion,
        string? Error);

    private readonly object _applyLock = new();
    private ApplyStatus _apply = new("idle", 0, null, null);
    private Task? _applyTask;

    public ApplyStatus GetApplyStatus()
    {
        lock (_applyLock) return _apply;
    }

    private void SetApply(string phase, double pct, string? version = null, string? error = null)
    {
        lock (_applyLock) _apply = new ApplyStatus(phase, pct, version ?? _apply.TargetVersion, error);
    }

    /// <summary>Kick the in-place update. Returns false (with status
    /// 'unsupported'/'failed') when preconditions fail synchronously.</summary>
    public bool BeginApply()
    {
        lock (_applyLock)
        {
            if (_applyTask is { IsCompleted: false }) return true; // already running
        }

        string? appImage = ResolveAppImagePath();
        if (appImage is null)
        {
            // Service-mode tarball or a source checkout with no remembered
            // image: nothing safe to swap.
            SetApply("unsupported", 0, error:
                "no AppImage found to update — launch Zeus from the .AppImage once "
                + "(or set ZEUS_APPIMAGE_PATH), then this button can update it");
            return false;
        }

        SetApply("downloading", 0);
        var task = Task.Run(() => ApplyAsync(appImage));
        lock (_applyLock) _applyTask = task;
        return true;
    }

    private const string ShortcutMarker = "X-Zeus-Managed=true";

    /// <summary>Where the AppImage path is remembered between runs, so a Zeus
    /// launched from the bare inner binary (extracted AppRun, wrapper script —
    /// $APPIMAGE unset) can still update the real image. XDG-aware.</summary>
    private static string AppImagePathFile()
    {
        string cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(cfg, "openhpsdr-zeus", "appimage-path");
    }

    /// <summary>Called at startup: when running under the AppImage runtime,
    /// remember where the image lives for future bare-binary runs.</summary>
    public void RecordAppImagePath()
    {
        try
        {
            string? appImage = ResolveAppImagePath();
            if (appImage is null) return;
            string file = AppImagePathFile();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (!File.Exists(file) || File.ReadAllText(file).Trim() != appImage)
                File.WriteAllText(file, appImage + "\n");
        }
        catch
        {
            // best-effort memory, never fatal
        }
    }

    /// <summary>Rollback sentinel: written just before the update restart,
    /// removed by the NEW instance at startup as its proof of life. If the
    /// handoff shell still sees it after the timeout, the new build never
    /// came up — it restores the .bak and relaunches the previous version.
    /// </summary>
    private static string PendingSentinelFile() =>
        Path.Combine(Path.GetDirectoryName(AppImagePathFile())!, "update-pending");

    /// <summary>Called at startup: confirm a just-applied update so the
    /// handoff supervisor stands down, and log the outcome. Safe no-op when
    /// no update is pending.</summary>
    public void ConfirmPendingUpdate()
    {
        try
        {
            string sentinel = PendingSentinelFile();
            if (!File.Exists(sentinel)) return;
            string version = File.ReadAllText(sentinel).Trim();
            File.Delete(sentinel);
            _log.LogInformation(
                "self-update: new instance up — update {Version} confirmed, rollback disarmed",
                version.Length > 0 ? version : "(unknown)");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "self-update: pending-update confirmation failed");
        }
    }

    /// <summary>$APPIMAGE, else ZEUS_APPIMAGE_PATH, else the path recorded by
    /// a previous AppImage-launched run — validated to still exist.</summary>
    private static string? ResolveAppImagePath()
    {
        foreach (string? candidate in new[]
        {
            Environment.GetEnvironmentVariable("APPIMAGE"),
            Environment.GetEnvironmentVariable("ZEUS_APPIMAGE_PATH"),
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
        }
        try
        {
            string file = AppImagePathFile();
            if (File.Exists(file))
            {
                string remembered = File.ReadAllText(file).Trim();
                if (remembered.Length > 0 && File.Exists(remembered)) return remembered;
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }

    /// <summary>Best-effort: make sure a Desktop launcher exists and points at
    /// the live AppImage. Runs after every applied update and once at startup,
    /// so the shortcut survives deletion, path moves, and manual installs.
    /// A launcher without our marker (user-customized) is never touched.</summary>
    public void EnsureDesktopShortcut()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return;
            string? appImage = ResolveAppImagePath();
            if (appImage is null) return;

            string? desktop = ResolveDesktopDir();
            if (desktop is null || !Directory.Exists(desktop)) return;

            string launcher = Path.Combine(desktop, "openhpsdr-zeus.desktop");
            if (File.Exists(launcher))
            {
                string existing = File.ReadAllText(launcher);
                if (!existing.Contains(ShortcutMarker, StringComparison.Ordinal))
                    return;                       // the operator made this their own
                if (existing.Contains($"Exec=\"{appImage}\"", StringComparison.Ordinal))
                    return;                       // already correct
            }

            string content =
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=OpenHPSDR Zeus\n" +
                "Comment=Software-defined radio (self-updating AppImage)\n" +
                $"Exec=\"{appImage}\"\n" +
                "Icon=radio\n" +
                "Terminal=false\n" +
                "Categories=HamRadio;Network;AudioVideo;\n" +
                ShortcutMarker + "\n";
            File.WriteAllText(launcher, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(launcher,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            _log.LogInformation("desktop shortcut ensured at {Path}", launcher);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "desktop shortcut ensure skipped");
        }
    }

    /// <summary>XDG_DESKTOP_DIR from ~/.config/user-dirs.dirs when present,
    /// else ~/Desktop.</summary>
    private static string? ResolveDesktopDir()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;
        try
        {
            string cfg = Path.Combine(home, ".config", "user-dirs.dirs");
            if (File.Exists(cfg))
            {
                foreach (var line in File.ReadLines(cfg))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("XDG_DESKTOP_DIR=", StringComparison.Ordinal)) continue;
                    string v = t["XDG_DESKTOP_DIR=".Length..].Trim('"');
                    v = v.Replace("$HOME", home, StringComparison.Ordinal);
                    if (v.Length > 0) return v;
                }
            }
        }
        catch
        {
            // fall through to the default
        }
        return Path.Combine(home, "Desktop");
    }

    private async Task ApplyAsync(string appImagePath)
    {
        try
        {
            // Fresh manifest check so we install exactly what status advertises.
            var status = await GetStatusAsync(fetch: true, CancellationToken.None).ConfigureAwait(false);
            string? url = status.ReleaseDownloadUrl;
            string? version = status.LatestVersion;
            if (string.IsNullOrWhiteSpace(url))
            {
                SetApply("failed", 0, version, "manifest has no downloadable asset for this platform");
                return;
            }
            SetApply("downloading", 0, version);

            string tmpPath = appImagePath + ".next";
            long expected = status.ReleaseAssetSizeBytes ?? -1;

            // Disk-space gate BEFORE the first byte: the download needs the
            // asset's size plus headroom (the .bak costs nothing — it's a
            // same-filesystem rename of the existing file).
            try
            {
                long need = (expected > 0 ? expected : 300L * 1024 * 1024) + 64L * 1024 * 1024;
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(appImagePath)) ?? "/");
                long free = drive.AvailableFreeSpace;
                if (free < need)
                {
                    SetApply("failed", 0, version,
                        $"not enough disk space: {free / (1024 * 1024)} MB free, " +
                        $"{need / (1024 * 1024)} MB needed for the download");
                    return;
                }
            }
            catch
            {
                // If the platform can't answer, proceed — the write will fail
                // loudly on a genuinely full disk.
            }

            var http = _httpClientFactory.CreateClient("ZeusUpdates");
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? expected;
                await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var dst = new FileStream(
                    tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                    done += n;
                    if (total > 0) SetApply("downloading", Math.Min(99.0, done * 100.0 / total), version);
                }
            }

            // Digest check against the manifest (format 'sha256:<hex>' or bare hex).
            SetApply("verifying", 100, version);
            string? digest = status.ReleaseAssetDigest;
            if (!string.IsNullOrWhiteSpace(digest))
            {
                string want = digest.Split(':').Last().Trim().ToLowerInvariant();
                await using var f = File.OpenRead(tmpPath);
                byte[] got = await SHA256.HashDataAsync(f).ConfigureAwait(false);
                string gotHex = Convert.ToHexString(got).ToLowerInvariant();
                if (!string.Equals(want, gotHex, StringComparison.Ordinal))
                {
                    File.Delete(tmpPath);
                    SetApply("failed", 100, version, $"sha256 mismatch (expected {want[..12]}…, got {gotHex[..12]}…)");
                    return;
                }
            }

            // Exec bit, then the atomic swap — same directory, same filesystem.
            SetApply("swapping", 100, version);
            // CA1416: the whole apply path is Linux-only in practice (it exists
            // because $APPIMAGE does), but the analyzer wants the platform
            // stated in a guard it can verify.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmpPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            // Keep the previous image as .bak (same-directory rename — free,
            // atomic, and the running process keeps its open handle), then
            // promote the verified download.
            string bakPath = appImagePath + ".bak";
            File.Move(appImagePath, bakPath, overwrite: true);
            File.Move(tmpPath, appImagePath, overwrite: true);

            // Arm the rollback: sentinel present = new instance not yet
            // confirmed. The new build's startup deletes it (proof of life);
            // the handoff shell rolls back if it survives the timeout.
            string sentinel = PendingSentinelFile();
            Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
            File.WriteAllText(sentinel, (version ?? "") + "\n");

            EnsureDesktopShortcut();

            // Hand off: a detached shell waits for THIS process to release the
            // listen port, then execs the new image with the same working dir.
            SetApply("restarting", 100, version);
            _log.LogInformation("self-update: swapped {Path} to v{Version}; restarting", appImagePath, version);
            // Supervising handoff ($0=image, $1=sentinel, $2=bak): wait for
            // this process to free the port, start the new image, then watch
            // the sentinel. Removed within the timeout -> new build confirmed,
            // supervisor exits. Still present -> the new build never came up:
            // kill it, restore the .bak, relaunch the previous version.
            const string supervise =
                "sleep 2\n" +
                "\"$0\" &\n" +
                "NEW=$!\n" +
                "i=0\n" +
                "while [ $i -lt 120 ]; do\n" +
                "  [ ! -e \"$1\" ] && exit 0\n" +
                "  sleep 1\n" +
                "  i=$((i+1))\n" +
                "done\n" +
                "kill $NEW 2>/dev/null\n" +
                "sleep 2\n" +
                "[ -e \"$2\" ] && mv -f \"$2\" \"$0\"\n" +
                "rm -f \"$1\"\n" +
                "exec \"$0\"\n";
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { "-c", supervise, appImagePath, sentinel, bakPath },
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(appImagePath) ?? "/",
            };
            Process.Start(psi);
            await Task.Delay(600).ConfigureAwait(false);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "self-update apply failed");
            SetApply("failed", 0, error: ex.Message);
        }
    }
}
