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
// Rewind (I/Q time-shift) endpoints:
//   GET  /api/time-shift            buffer + replay status
//   POST /api/time-shift/config     { enabled, seconds } — saved radio-side
//   POST /api/time-shift/replay     { delaySeconds } — start, or move, a replay
//   POST /api/time-shift/pause      { paused }
//   POST /api/time-shift/renew      keep a replay alive (about once a second)
//   POST /api/time-shift/live       back to live reception

using System.Text.Json;

namespace Zeus.Server;

public sealed record TimeShiftReplayRequest(double DelaySeconds);

public sealed record TimeShiftPauseRequest(bool Paused);

public static class TimeShiftEndpoints
{
    private static readonly object FileLock = new();

    private static string PrefsPath()
    {
        var dir = Path.GetDirectoryName(PrefsDbPath.Get()) ?? ".";
        return Path.Combine(dir, "time-shift.json");
    }

    /// <summary>The saved setting, sanitised (off, 60 s when missing or junk).</summary>
    public static TimeShiftPrefs Load()
    {
        try
        {
            var path = PrefsPath();
            if (!File.Exists(path)) return new TimeShiftPrefs();
            var p = JsonSerializer.Deserialize<TimeShiftPrefs>(File.ReadAllText(path)) ?? new TimeShiftPrefs();
            return p with { Seconds = DspPipelineService.ClampTimeShiftSeconds(p.Seconds) };
        }
        catch
        {
            return new TimeShiftPrefs();
        }
    }

    public static IEndpointRouteBuilder MapTimeShiftEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/time-shift", (DspPipelineService dsp) => Results.Ok(dsp.GetTimeShiftStatus()));

        app.MapPost("/api/time-shift/config", (TimeShiftPrefs req, DspPipelineService dsp) =>
        {
            var prefs = req with { Seconds = DspPipelineService.ClampTimeShiftSeconds(req.Seconds) };
            lock (FileLock)
            {
                try
                {
                    var path = PrefsPath();
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonSerializer.Serialize(prefs));
                }
                catch (Exception)
                {
                    // Read-only data dir: the setting still applies this session.
                }
            }
            return Results.Ok(dsp.ConfigureTimeShift(prefs));
        });

        app.MapPost("/api/time-shift/replay", (TimeShiftReplayRequest req, DspPipelineService dsp) =>
            Results.Ok(dsp.StartTimeShiftReplay(req.DelaySeconds)));

        app.MapPost("/api/time-shift/pause", (TimeShiftPauseRequest req, DspPipelineService dsp) =>
            Results.Ok(dsp.PauseTimeShiftReplay(req.Paused)));

        app.MapPost("/api/time-shift/renew", (DspPipelineService dsp) => Results.Ok(dsp.RenewTimeShiftReplay()));

        app.MapPost("/api/time-shift/live", (DspPipelineService dsp) => Results.Ok(dsp.StopTimeShiftReplay()));

        return app;
    }
}
