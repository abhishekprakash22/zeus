// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// UiPrefsEndpoints — tiny window-chrome preferences the LAUNCHER needs before
// the frontend exists. First tenant: the kiosk full-screen marker. The
// Fullscreen API cannot restore itself at page load (gesture-gated), but the
// AppImage kiosk launcher CAN start Chromium with --start-fullscreen — it
// just needs to know the operator's last choice from outside the browser.
// The marker is a plain file beside zeus-prefs.db so a bash launcher can
// test it with [ -f ]; the frontend fire-and-forgets POSTs on every
// fullscreen state change. Same-origin/local guarded like /api/app/quit.

namespace Zeus.Server;

public static class UiPrefsEndpoints
{
    public const string KioskFullscreenMarker = "kiosk-fullscreen";

    private static string MarkerPath()
    {
        var dir = Path.GetDirectoryName(PrefsDbPath.Get()) ?? ".";
        return Path.Combine(dir, KioskFullscreenMarker);
    }

    public static IEndpointRouteBuilder MapUiPrefsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ui/kiosk-fullscreen", () =>
            Results.Ok(new { on = File.Exists(MarkerPath()) }));

        app.MapPost("/api/ui/kiosk-fullscreen", (KioskFullscreenRequest req, HttpContext ctx) =>
        {
            if (LocalRequestGuard.RejectIfNotLocalSameOrigin(ctx) is { } rejection)
                return rejection;
            try
            {
                var path = MarkerPath();
                if (req.On)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "1");
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return Results.Ok(new { on = req.On });
            }
            catch (Exception)
            {
                // Read-only data dir etc. — preference storage is best-effort.
                return Results.Ok(new { on = req.On, persisted = false });
            }
        });

        return app;
    }

    public sealed record KioskFullscreenRequest(bool On);
}
