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
    public const string PsPreferredMarker = "ps-preferred";

    private static string MarkerPath()
    {
        var dir = Path.GetDirectoryName(PrefsDbPath.Get()) ?? ".";
        return Path.Combine(dir, KioskFullscreenMarker);
    }

    private static string PsMarkerPath()
    {
        var dir = Path.GetDirectoryName(PrefsDbPath.Get()) ?? ".";
        return Path.Combine(dir, PsPreferredMarker);
    }

    private static string KeyDecksPath()
    {
        var dir = Path.GetDirectoryName(PrefsDbPath.Get()) ?? ".";
        return Path.Combine(dir, "ui-key-decks.json");
    }

    private static string SpecFracPath()
    {
        var dir = Path.GetDirectoryName(PrefsDbPath.Get()) ?? ".";
        return Path.Combine(dir, "ui-spec-frac.json");
    }

    public static IEndpointRouteBuilder MapUiPrefsEndpoints(this IEndpointRouteBuilder app)
    {
        // Pan/waterfall split per receiver (field request ×2: localStorage
        // alone could not survive the kiosk, whose browser profile is
        // deliberately throwaway — see linux-zeus-preflight.sh). Same
        // durable-directory pattern as the marker files above; the payload
        // is a tiny {"0":0.44,"1":0.5} map, clamped on write.
        // G8NJJ follow-up: user-defined drawer key sets. Payload is
        // {"deck1":["Tun","Mon","Ps","Ctun"],"deck2":[...]} — names validated
        // client-side against its registry; the server just keeps the choice
        // durable across the kiosk's throwaway browser profile.
        app.MapGet("/api/ui/key-decks", () =>
        {
            try
            {
                var path = KeyDecksPath();
                if (!File.Exists(path)) return Results.Ok(new Dictionary<string, string[]>());
                var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(
                    File.ReadAllText(path));
                return Results.Ok(map ?? new Dictionary<string, string[]>());
            }
            catch { return Results.Ok(new Dictionary<string, string[]>()); }
        });
        app.MapPost("/api/ui/key-decks", (Dictionary<string, string[]> req) =>
        {
            var path = KeyDecksPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(req));
            return Results.Ok(req);
        });

        app.MapGet("/api/ui/spec-frac", () =>
        {
            try
            {
                var path = SpecFracPath();
                if (!File.Exists(path)) return Results.Ok(new Dictionary<string, double>());
                var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(
                    File.ReadAllText(path));
                return Results.Ok(map ?? new Dictionary<string, double>());
            }
            catch { return Results.Ok(new Dictionary<string, double>()); }
        });
        app.MapPost("/api/ui/spec-frac", (SpecFracRequest req) =>
        {
            var frac = Math.Clamp(req.Frac, 0.2, 0.7);
            var path = SpecFracPath();
            Dictionary<string, double> map;
            try
            {
                map = File.Exists(path)
                    ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(
                          File.ReadAllText(path)) ?? new()
                    : new();
            }
            catch { map = new(); }
            map[req.Rx.ToString()] = frac;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(map));
            return Results.Ok(new { rx = req.Rx, frac });
        });

        // PureSignal persistence (field request: the PS button forgot its
        // state across sessions). Same marker-file pattern as
        // kiosk-fullscreen; the frontend re-arms PS through the normal
        // /api/tx/ps path at connect, so every guard still applies.
        app.MapGet("/api/ui/ps-preferred", () =>
            Results.Ok(new { on = File.Exists(PsMarkerPath()) }));
        app.MapPost("/api/ui/ps-preferred", (KioskFullscreenRequest req) =>
        {
            var path = PsMarkerPath();
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
        });

        app.MapGet("/api/ui/kiosk-fullscreen", () =>
            Results.Ok(new { on = File.Exists(MarkerPath()) }));

        app.MapPost("/api/ui/kiosk-fullscreen", (KioskFullscreenRequest req, HttpContext ctx) =>
        {
            if (LocalRequestGuard.RejectIfNotLocalSameOrigin(ctx, "kiosk-fullscreen") is { } rejection)
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

public sealed record SpecFracRequest(int Rx, double Frac);
}
