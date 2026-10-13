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
// /api/assistant — settings (the key is write-only: GET returns only whether
// one is set and a 4-character hint) and ask. Settings writes are refused
// over the remote tunnel (RemoteWebRtcSession.WriteDeniedPrefixes); asking
// works anywhere the radio's UI does.

namespace Zeus.Server.Assistant;

public static class AssistantEndpoints
{
    public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/assistant/settings", (AssistantSettingsStore store, AssistantService svc) =>
            Results.Ok(new
            {
                settings = AssistantSettingsStore.ToDto(store.Get()),
                manualSections = svc.ManualSections,
            }));

        app.MapPost("/api/assistant/settings", (AssistantSettingsUpdate update, AssistantSettingsStore store) =>
            Results.Ok(AssistantSettingsStore.ToDto(store.Apply(update))));

        app.MapPost("/api/assistant/ask", async (AssistantAskRequest req, AssistantService svc, HttpContext ctx) =>
            Results.Ok(await svc.AskAsync(req, ctx.RequestAborted).ConfigureAwait(false)));

        return app;
    }
}
