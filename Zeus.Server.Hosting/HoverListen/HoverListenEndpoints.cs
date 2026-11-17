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
// Hover to listen: the preview lease (POST /api/hover-preview, renewed at
// pointer rate; POST /api/hover-preview/stop) and the operator's on/off
// choice (GET/POST /api/ui/hover-listen, radio-side so the kiosk keeps it).

using System.Text.Json;

namespace Zeus.Server;

public sealed record HoverPreviewRequest(long Hz);

/// <summary>Hover to listen is opt-in: it changes what the operator hears.</summary>
public sealed record HoverListenPrefs(bool Enabled = false);

public static class HoverListenEndpoints
{
    private static readonly object FileLock = new();

    private static string PrefsPath()
    {
        var dir = Path.GetDirectoryName(PrefsDbPath.Get()) ?? ".";
        return Path.Combine(dir, "hover-listen.json");
    }

    internal static HoverListenPrefs Load()
    {
        try
        {
            var path = PrefsPath();
            if (!File.Exists(path)) return new HoverListenPrefs();
            return JsonSerializer.Deserialize<HoverListenPrefs>(File.ReadAllText(path)) ?? new HoverListenPrefs();
        }
        catch
        {
            return new HoverListenPrefs();
        }
    }

    public static IEndpointRouteBuilder MapHoverListenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/hover-preview", (HoverPreviewRequest req, DspPipelineService dsp) =>
        {
            if (req.Hz <= 0) return Results.BadRequest(new { error = "hz must be positive" });
            return Results.Ok(dsp.RequestHoverPreview(req.Hz));
        });

        app.MapPost("/api/hover-preview/stop", (DspPipelineService dsp) =>
        {
            dsp.StopHoverPreview();
            return Results.Ok(new HoverPreviewResult(false, null));
        });

        app.MapGet("/api/ui/hover-listen", () =>
        {
            lock (FileLock) return Results.Ok(Load());
        });

        app.MapPost("/api/ui/hover-listen", (HoverListenPrefs req) =>
        {
            lock (FileLock)
            {
                try
                {
                    var path = PrefsPath();
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonSerializer.Serialize(req));
                }
                catch (Exception)
                {
                    // Read-only data dir: the choice still applies this session.
                }
                return Results.Ok(req);
            }
        });

        return app;
    }
}
