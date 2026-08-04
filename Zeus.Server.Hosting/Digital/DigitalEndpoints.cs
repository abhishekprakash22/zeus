// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Digital-mode HTTP surface, served from CORE under the plugin route prefix
// the frontend already targets: /api/plugins/org.openhpsdr.digital/...
//
// The contract is dictated by zeus-web/src/api/digital-plugin.ts and the stores
// it feeds — those are already written and tested, so this matches them exactly
// rather than inventing shapes. GET /status is the liveness probe AND the mode
// gate: 2xx unlocks the digital UI, 404/503 hides it.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Zeus.Server.Hosting.Digital;

public static class DigitalEndpoints
{
    public static IEndpointRouteBuilder MapDigitalEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup($"/api/plugins/{DigitalService.PluginId}");

        // Liveness probe / mode gate.
        g.MapGet("/status", (DigitalService d) => Results.Ok(new DigitalStatus
        {
            Ft8Enabled = d.Ft8Enabled,
            WsprEnabled = d.WsprEnabled,
            DecoderAvailable = Ft8Native.Available,
            DecodeLatencyMs = d.Decoder.LastLatencyMs,
            Clock = ClockStatusDto.From(d.Clock.Status),
            Call = d.Callsign,
            Grid = d.Grid,
        }));

        // ---- FT8 ------------------------------------------------------------
        g.MapGet("/ft8", (DigitalService d) => Results.Ok(new { enabled = d.Ft8Enabled, mode = d.Mode }));
        g.MapPost("/ft8/enable", (DigitalService d) => { d.EnableFt8(true); return Results.Ok(new { enabled = true }); });
        g.MapPost("/ft8/disable", (DigitalService d) => { d.EnableFt8(false); return Results.Ok(new { enabled = false }); });

        g.MapGet("/ft8/tx", (DigitalService d) => Results.Ok(d.BuildTxStatus()));

        // THE STAGE. Keyed verbatim — the backend never rewrites or invents a
        // message. Deliberately NO late-start cutoff (see SlotClock): a stage is
        // useful until KeyLeadMs before the boundary, because we have not keyed
        // yet. Upstream's fixed +2.5 s cutoff vs the runner's 2.0 s decoder settle
        // left ~500 ms of slack; Pi decode jitter ate it and the reply slipped a
        // full cycle — "someone answered my CQ and Zeus never replied".
        g.MapPost("/ft8/tx", (TxStageRequest req, DigitalService d) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
                return Results.BadRequest(new { error = "message required" });

            var mode = string.Equals(req.Mode, "FT4", StringComparison.OrdinalIgnoreCase)
                ? DigitalMode.Ft4 : DigitalMode.Ft8;
            d.Mode = mode == DigitalMode.Ft4 ? "FT4" : "FT8";
            if (req.AudioHz is int hz) d.AudioHz = hz;

            d.Stages.Put(new TxStage(
                Message: req.Message.Trim(),
                AudioHz: d.AudioHz,
                Slot: (req.Slot ?? "even").ToLowerInvariant(),
                Mode: mode,
                StagedAtMs: d.Clock.UtcNowMs));

            d.Events.PublishTxStatus(d.BuildTxStatus());
            return Results.Ok(d.BuildTxStatus());
        });

        g.MapPost("/ft8/tx/arm", (ArmRequest req, DigitalService d) =>
        {
            if (!d.TryArm(req.Enabled, out var error))
                return Results.Json(new
                {
                    error = "clock not synchronised",
                    message = error,
                    clock = ClockStatusDto.From(d.Clock.Status),
                }, statusCode: StatusCodes.Status409Conflict);

            return Results.Ok(d.BuildTxStatus());
        });

        g.MapPost("/ft8/tx/halt", (DigitalService d) => { d.Halt(); return Results.Ok(d.BuildTxStatus()); });

        // ---- WSPR -----------------------------------------------------------
        // Real backend (WsprService): 120 s slot decoder + autonomous beacon.
        g.MapGet("/wspr", (WsprService w) => Results.Ok(w.StatusDto()));

        // ---- CW skimmer (DeepCW phase 2) ----
        g.MapGet("/cwskim", (CwSkimmerService cw) => Results.Ok(cw.StatusDto()));
        g.MapPost("/cwskim/enable", (CwSkimEnableRequest req, CwSkimmerService cw) =>
            Results.Ok(new { ok = cw.Enable(req.Receiver ?? 0) }));
        g.MapPost("/cwskim/disable", (CwSkimmerService cw) =>
        {
            cw.Disable();
            return Results.Ok(new { ok = true });
        });
        g.MapPost("/wspr/enable", (WsprEnableRequest req, WsprService w, DigitalService d) =>
        {
            bool ok = w.Enable(req.Receiver ?? 0, req.DialFreqMhz ?? 14.0956);
            d.EnableWspr(ok);
            return Results.Ok(new { enabled = ok, nativeAvailable = w.NativeAvailable });
        });
        g.MapPost("/wspr/disable", (WsprService w, DigitalService d) =>
        {
            w.Disable();
            w.Arm(false);          // never leave a beacon armed on a dead decoder
            d.EnableWspr(false);
            return Results.Ok(new { enabled = false });
        });
        g.MapPost("/wspr/tx/settings", (WsprTxSettingsRequest req, WsprService w) =>
        {
            w.TxSettings(req.Call, req.Grid4, req.DBm, req.AudioHz, req.TxPercent);
            return Results.Ok(new { ok = true });
        });
        g.MapPost("/wspr/tx/arm", (ArmRequest req, WsprService w) =>
            Results.Ok(new { enabled = w.Arm(req.Enabled) }));
        g.MapPost("/wspr/tx/halt", (WsprService w) =>
        {
            w.Arm(false);
            return Results.Ok(new { ok = true });
        });

        // ---- config ---------------------------------------------------------
        g.MapPost("/config/identity", (IdentityRequest req, DigitalService d) =>
        {
            d.Callsign = req.Call;
            d.Grid = req.Grid;
            return Results.Ok(new { ok = true });
        });
        g.MapPost("/config/wsjtx-live", (WsjtxLiveRequest _) => Results.Ok(new { ok = true }));
        g.MapPost("/config/spotting", () => Results.Ok(new { ok = true }));
        g.MapGet("/spotting/status", () => Results.Ok(new { enabled = false, uploaded = 0 }));

        // ---- SSE ------------------------------------------------------------
        // Replaces the legacy 0x38/0x39/0x3A WS frames (RESERVED in ws-client.ts).
        // The client re-hydrates on EVERY open including auto-reconnects, because
        // SSE replays nothing across a gap — so no replay buffer here.
        g.MapGet("/events", async (HttpContext ctx, DigitalService d) =>
        {
            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var (reader, lease) = d.Events.Subscribe();
            try
            {
                await ctx.Response.WriteAsync(": connected\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                d.Events.PublishTxStatus(d.BuildTxStatus());

                await foreach (var frame in reader.ReadAllAsync(ctx.RequestAborted))
                {
                    await ctx.Response.WriteAsync(frame, ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { /* client went away — normal */ }
            finally { lease.Dispose(); }
        });

        return app;
    }
}
