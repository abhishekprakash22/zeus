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

namespace Zeus.Server.Hosting;

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

        string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrWhiteSpace(appImage) || !File.Exists(appImage))
        {
            // Service-mode tarball or a source checkout: nothing safe to swap.
            SetApply("unsupported", 0, error:
                "not running from an AppImage ($APPIMAGE unset) — update the install manually");
            return false;
        }

        SetApply("downloading", 0);
        var task = Task.Run(() => ApplyAsync(appImage));
        lock (_applyLock) _applyTask = task;
        return true;
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
            File.SetUnixFileMode(tmpPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(tmpPath, appImagePath, overwrite: true);

            // Hand off: a detached shell waits for THIS process to release the
            // listen port, then execs the new image with the same working dir.
            SetApply("restarting", 100, version);
            _log.LogInformation("self-update: swapped {Path} to v{Version}; restarting", appImagePath, version);
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { "-c", $"sleep 2; exec \"$0\"", appImagePath },
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
