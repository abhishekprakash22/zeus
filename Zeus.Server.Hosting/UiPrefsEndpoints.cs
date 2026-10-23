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

    private static string FlagScalePath()
    {
        var dir = Path.GetDirectoryName(PrefsDbPath.Get()) ?? ".";
        return Path.Combine(dir, "ui-flag-scale.json");
    }

    private static string WfSpeedPath()
    {
        var dir = Path.GetDirectoryName(PrefsDbPath.Get()) ?? ".";
        return Path.Combine(dir, "ui-wf-speed.json");
    }

    // The G2 pane's SPD key cycles ×½ ×1 ×2 ×4; anything else (stale or
    // hand-edited) reads back as ×1.
    internal static readonly double[] WfSpeedFactors = { 0.5, 1.0, 2.0, 4.0 };

    private static string G2CardsPath()
    {
        var dir = Path.GetDirectoryName(PrefsDbPath.Get()) ?? ".";
        return Path.Combine(dir, "ui-g2-cards.json");
    }

    internal const int MaxG2Cards = 16;

    /// <summary>
    /// Keep only sane entries: short ids, finite numbers, bounded counts. The
    /// client clamps boxes on screen when it reads them; this only refuses junk.
    /// </summary>
    internal static G2CardLayout NormalizeG2Cards(G2CardLayout? layout)
    {
        static bool IdOk(string? id) =>
            !string.IsNullOrWhiteSpace(id) && id.Length <= 40;
        var boxes = new Dictionary<string, G2CardBox>();
        if (layout?.Boxes is { } bs)
        {
            foreach (var kv in bs)
            {
                if (boxes.Count >= MaxG2Cards) break;
                var b = kv.Value;
                if (!IdOk(kv.Key) || b is null) continue;
                if (!double.IsFinite(b.X) || !double.IsFinite(b.Y) || !double.IsFinite(b.W) || !double.IsFinite(b.H)) continue;
                if (b.W <= 0 || b.H <= 0 || b.W > 20000 || b.H > 20000 || Math.Abs(b.X) > 20000 || Math.Abs(b.Y) > 20000) continue;
                boxes[kv.Key] = b;
            }
        }
        var hidden = new List<string>();
        if (layout?.Hidden is { } hs)
        {
            foreach (var h in hs)
            {
                if (hidden.Count >= MaxG2Cards) break;
                if (IdOk(h) && !hidden.Contains(h)) hidden.Add(h);
            }
        }
        return new G2CardLayout(boxes, hidden);
    }

    private static string FilterViewPath()
    {
        var dir = Path.GetDirectoryName(PrefsDbPath.Get()) ?? ".";
        return Path.Combine(dir, "ui-filter-view.json");
    }

    /// <summary>The filter card's display views; anything else reads as the passband.</summary>
    internal static readonly string[] FilterViews = { "pass", "aspec", "scope", "awf", "cw", "lvl" };

    internal static string NormalizeFilterView(string? view) =>
        view is not null && Array.IndexOf(FilterViews, view) >= 0 ? view : "pass";

    internal static double NormalizeWfSpeedFactor(double factor)
    {
        foreach (var f in WfSpeedFactors)
        {
            if (f == factor) return f;
        }
        return 1.0;
    }

    private static string EffectsPath()
    {
        var dir = Path.GetDirectoryName(PrefsDbPath.Get()) ?? ".";
        return Path.Combine(dir, "ui-effects.json");
    }

    // Display effects level (Off / Subtle / Full). Anything else reads as the
    // default, Subtle.
    internal static string NormalizeEffectsLevel(string? level) =>
        level is "off" or "subtle" or "full" ? level! : "subtle";

    internal static double ClampFlagScale(double scale) =>
        double.IsFinite(scale) ? Math.Clamp(scale, 1.0, 2.0) : 1.0;

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

        // G2 receiver-flag scale (field request: flag text and mini S-meter
        // too small on the 8-inch panel). One value for both flags; same
        // durable-directory pattern as spec-frac so the kiosk keeps it.
        // Per-pane waterfall speed (G2 SPD key), per receiver. Server-side for
        // the same reason as spec-frac: the kiosk's browser profile is
        // throwaway, so localStorage alone forgot the setting every launch.
        app.MapGet("/api/ui/wf-speed", () =>
        {
            try
            {
                var path = WfSpeedPath();
                if (!File.Exists(path)) return Results.Ok(new Dictionary<string, double>());
                var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(
                    File.ReadAllText(path)) ?? new Dictionary<string, double>();
                var clean = new Dictionary<string, double>();
                foreach (var kv in map) clean[kv.Key] = NormalizeWfSpeedFactor(kv.Value);
                return Results.Ok(clean);
            }
            catch { return Results.Ok(new Dictionary<string, double>()); }
        });
        app.MapPost("/api/ui/wf-speed", (WfSpeedRequest req) =>
        {
            var factor = NormalizeWfSpeedFactor(req.Factor);
            try
            {
                var path = WfSpeedPath();
                Dictionary<string, double> map;
                try
                {
                    map = File.Exists(path)
                        ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(
                              File.ReadAllText(path)) ?? new()
                        : new();
                }
                catch { map = new(); }
                map[req.Rx.ToString()] = factor;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(map));
                return Results.Ok(new { rx = req.Rx, factor });
            }
            catch (Exception)
            {
                return Results.Ok(new { rx = req.Rx, factor, persisted = false });
            }
        });

        // G2 floating cards (filter, S-meter): position + size per card and the
        // list of closed cards. Server-side: the kiosk's browser profile is
        // throwaway, so localStorage alone forgot them on every launch.
        app.MapGet("/api/ui/g2-cards", () =>
        {
            try
            {
                var path = G2CardsPath();
                if (!File.Exists(path)) return Results.Ok(new G2CardLayout(new(), new()));
                var layout = System.Text.Json.JsonSerializer.Deserialize<G2CardLayout>(File.ReadAllText(path));
                return Results.Ok(NormalizeG2Cards(layout));
            }
            catch { return Results.Ok(new G2CardLayout(new(), new())); }
        });
        app.MapPost("/api/ui/g2-cards", (G2CardLayout req) =>
        {
            var layout = NormalizeG2Cards(req);
            try
            {
                var path = G2CardsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(layout));
                return Results.Ok(layout);
            }
            catch (Exception)
            {
                return Results.Ok(layout);
            }
        });

        // Filter card display view, per receiver (Passband / Audio spectrum /
        // Scope / Audio waterfall / CW pitch / Levels). Server-side for the same
        // reason as wf-speed: the kiosk's browser profile is throwaway.
        app.MapGet("/api/ui/filter-view", () =>
        {
            try
            {
                var path = FilterViewPath();
                if (!File.Exists(path)) return Results.Ok(new Dictionary<string, string>());
                var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(path)) ?? new Dictionary<string, string>();
                var clean = new Dictionary<string, string>();
                foreach (var kv in map) clean[kv.Key] = NormalizeFilterView(kv.Value);
                return Results.Ok(clean);
            }
            catch { return Results.Ok(new Dictionary<string, string>()); }
        });
        app.MapPost("/api/ui/filter-view", (FilterViewRequest req) =>
        {
            var view = NormalizeFilterView(req.View);
            try
            {
                var path = FilterViewPath();
                Dictionary<string, string> map;
                try
                {
                    map = File.Exists(path)
                        ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                              File.ReadAllText(path)) ?? new()
                        : new();
                }
                catch { map = new(); }
                map[req.Rx.ToString()] = view;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(map));
                return Results.Ok(new { rx = req.Rx, view });
            }
            catch (Exception)
            {
                return Results.Ok(new { rx = req.Rx, view, persisted = false });
            }
        });

        // Display effects level, radio-wide. Server-side for the same reason
        // as spec-frac and wf-speed: the kiosk's browser profile is throwaway.
        // GET answers level:null until an operator has chosen one, so the
        // client keeps its own default instead of being overruled by ours.
        app.MapGet("/api/ui/effects", () =>
        {
            try
            {
                var path = EffectsPath();
                if (!File.Exists(path)) return Results.Ok(new { level = (string?)null });
                var doc = System.Text.Json.JsonSerializer.Deserialize<EffectsRequest>(File.ReadAllText(path));
                return Results.Ok(new { level = (string?)NormalizeEffectsLevel(doc?.Level) });
            }
            catch { return Results.Ok(new { level = (string?)null }); }
        });
        app.MapPost("/api/ui/effects", (EffectsRequest req) =>
        {
            var level = NormalizeEffectsLevel(req.Level);
            try
            {
                var path = EffectsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new EffectsRequest(level)));
                return Results.Ok(new { level });
            }
            catch (Exception)
            {
                return Results.Ok(new { level, persisted = false });
            }
        });

        app.MapGet("/api/ui/flag-scale", () =>
        {
            try
            {
                var path = FlagScalePath();
                if (!File.Exists(path)) return Results.Ok(new { scale = 1.0 });
                var stored = System.Text.Json.JsonSerializer.Deserialize<FlagScaleRequest>(
                    File.ReadAllText(path));
                return Results.Ok(new { scale = ClampFlagScale(stored?.Scale ?? 1.0) });
            }
            catch { return Results.Ok(new { scale = 1.0 }); }
        });
        app.MapPost("/api/ui/flag-scale", (FlagScaleRequest req) =>
        {
            var scale = ClampFlagScale(req.Scale);
            try
            {
                var path = FlagScalePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new FlagScaleRequest(scale)));
                return Results.Ok(new { scale });
            }
            catch (Exception)
            {
                // Read-only data dir etc. — preference storage is best-effort.
                return Results.Ok(new { scale, persisted = false });
            }
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

public sealed record FlagScaleRequest(double Scale);

public sealed record WfSpeedRequest(int Rx, double Factor);

public sealed record FilterViewRequest(int Rx, string? View);

public sealed record G2CardBox(double X, double Y, double W, double H);

public sealed record G2CardLayout(Dictionary<string, G2CardBox>? Boxes, List<string>? Hidden);

public sealed record EffectsRequest(string? Level);
}
