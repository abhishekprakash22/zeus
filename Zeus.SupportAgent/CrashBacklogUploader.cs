// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.SupportAgent;

/// <summary>
/// Re-uploads crash records that were captured on a previous sidecar run but
/// never successfully shared. Success is recorded with a sibling marker file so
/// retries stay explicit and durable.
/// </summary>
public static class CrashBacklogUploader
{
    private const int MaxUploadsPerRun = 5;

    public static async Task UploadEligibleAsync(
        string crashDir,
        ISupportBrokerClient broker,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        try
        {
            foreach (var path in EligibleRecords(crashDir).Take(MaxUploadsPerRun))
            {
                if (ct.IsCancellationRequested) return;

                var file = Path.GetFileName(path);
                string? json = TryReadAllText(path);
                var outcome = await CrashAutoShare.TryShareAsync(
                        autoShareEnabled: true,
                        crashRecordJson: json,
                        broker,
                        ct)
                    .ConfigureAwait(false);

                if (outcome == CrashShareOutcome.Uploaded)
                {
                    MarkUploaded(path);
                    log?.Invoke($"crash backlog upload: {file} ok");
                }
                else
                {
                    log?.Invoke($"crash backlog upload: {file} failed");
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"crash backlog upload: error ({ex.GetType().Name})");
        }
    }

    public static void MarkUploaded(string recordPath)
    {
        try
        {
            File.WriteAllText(UploadedMarkerPath(recordPath), DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // Best-effort marker only; the broker caps duplicates per callsign.
        }
    }

    public static string UploadedMarkerPath(string recordPath) => recordPath + ".uploaded";

    private static IEnumerable<string> EligibleRecords(string crashDir)
    {
        try
        {
            if (!Directory.Exists(crashDir)) return [];
            return Directory.GetFiles(crashDir, "crash-*.json")
                .Where(f => !File.Exists(UploadedMarkerPath(f)))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string? TryReadAllText(string path)
    {
        try { return File.ReadAllText(path); } catch { return null; }
    }
}
