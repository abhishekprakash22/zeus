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
// Decodes on the spectrum: the logbook status of heard calls
// (POST /api/log/spectrum-status) and which tag sources the operator shows
// (GET/POST /api/ui/decode-tags, radio-side so the kiosk keeps it).

using System.Text.Json;

namespace Zeus.Server;

/// <summary>Which decode tags are painted on the panadapter.</summary>
public sealed record DecodeTagPrefs(
    bool Enabled = true,
    bool Ft8 = true,
    bool Cw = true,
    bool Spots = true,
    bool Classifier = true);

public static class DecodeTagEndpoints
{
    private static readonly object FileLock = new();

    private static string PrefsPath()
    {
        var dir = Path.GetDirectoryName(PrefsDbPath.Get()) ?? ".";
        return Path.Combine(dir, "decode-tags.json");
    }

    internal static DecodeTagPrefs Load()
    {
        try
        {
            var path = PrefsPath();
            if (!File.Exists(path)) return new DecodeTagPrefs();
            return JsonSerializer.Deserialize<DecodeTagPrefs>(File.ReadAllText(path)) ?? new DecodeTagPrefs();
        }
        catch
        {
            return new DecodeTagPrefs();
        }
    }

    public static IEndpointRouteBuilder MapDecodeTagEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ui/decode-tags", () =>
        {
            lock (FileLock) return Results.Ok(Load());
        });

        app.MapPost("/api/ui/decode-tags", (DecodeTagPrefs req) =>
        {
            lock (FileLock)
            {
                try
                {
                    var path = PrefsPath();
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonSerializer.Serialize(req));
                    return Results.Ok(req);
                }
                catch (Exception)
                {
                    return Results.Ok(new { prefs = req, persisted = false });
                }
            }
        });

        app.MapPost("/api/log/spectrum-status", async (CallStatusRequest req, CallStatusService svc, CancellationToken ct) =>
        {
            var items = await svc.ClassifyAsync(req.Items, ct);
            return Results.Ok(new { items });
        });

        return app;
    }
}
