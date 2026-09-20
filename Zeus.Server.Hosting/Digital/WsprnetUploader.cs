// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR),
//                         Ramón Martínez (EA5IUE), and contributors.
//
// WSPRnet spot upload. One HTTP GET per spot to wsprnet.org/post, the same
// query the WSJT-X family sends (fields and order per jtdx wsprnet.cpp,
// GPL-3.0): function=wspr, the receiver's call/grid/dial, the slot's UTC date
// and time, then the decode itself — SNR, dt, drift, the transmitter's
// frequency, call, grid and power — plus the reporting software version and
// mode=2 for WSPR-2.
//
// Egress discipline: nothing leaves the machine unless the operator opted in
// AND gave a callsign and grid (SpottingSettings.IdentityResolved). Failures
// are logged once per slot and dropped — a spot is not worth retry machinery,
// and the next slot is two minutes away.

using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Hosting.Digital;

/// <summary>The transmitting station as decoded from a WSPR message.</summary>
public sealed record WsprMessageParts(string Callsign, string? Grid, int? PowerDbm);

public sealed class WsprnetUploader
{
    internal const string Endpoint = "http://wsprnet.org/post";

    private readonly HttpClient _http;
    private readonly ILogger _log;

    public WsprnetUploader(HttpClient http, ILogger log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>Uploaded spots since start — surfaced by /spotting/status.</summary>
    public int Uploaded { get; private set; }

    /// <summary>
    /// Uploads one slot's spots. <paramref name="slotStartUtc"/> is the start of
    /// the two-minute slot the decodes came from (WSPRnet keys on it).
    /// </summary>
    public async Task UploadSlotAsync(
        WsprSpotBatch batch, SpottingSettings id, string version, CancellationToken ct)
    {
        if (!id.IdentityResolved || batch.Spots.Length == 0) return;
        var slotStartUtc = DateTimeOffset.FromUnixTimeMilliseconds(batch.SlotStartUnixMs).UtcDateTime;

        int sent = 0;
        foreach (var spot in batch.Spots)
        {
            if (ct.IsCancellationRequested) return;
            var parts = ParseMessage(spot.Message);
            if (parts is null) continue;          // beacon we can't attribute — skip

            var url = BuildSpotUrl(spot, parts, batch.DialFreqMhz, slotStartUtc, id, version);
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) sent++;
                else
                {
                    _log.LogInformation(
                        "spotting.wsprnet: {Status} for {Call}", (int)resp.StatusCode, parts.Callsign);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _log.LogInformation("spotting.wsprnet: upload failed ({Reason})", ex.Message);
                return;                            // network is down; drop the rest of the slot
            }
        }

        if (sent > 0)
        {
            Uploaded += sent;
            _log.LogInformation("spotting.wsprnet: uploaded {Sent} spot(s)", sent);
        }
    }

    /// <summary>The wsprnet.org/post query for one spot (jtdx wsprnet.cpp field set).</summary>
    internal static string BuildSpotUrl(
        WsprSpotDtoOut spot, WsprMessageParts tx, double dialMhz,
        DateTime slotStartUtc, SpottingSettings id, string version)
    {
        var inv = CultureInfo.InvariantCulture;
        var q = new List<string>
        {
            "function=wspr",
            "rcall=" + Uri.EscapeDataString(id.Callsign),
            "rgrid=" + Uri.EscapeDataString(id.Grid),
            "rqrg=" + dialMhz.ToString("F6", inv),
            "date=" + slotStartUtc.ToString("yyMMdd", inv),
            "time=" + slotStartUtc.ToString("HHmm", inv),
            "sig=" + Math.Round(spot.SnrDb).ToString("F0", inv),
            "dt=" + spot.DtSec.ToString("F1", inv),
            "drift=" + Math.Round(spot.DriftHz).ToString("F0", inv),
            "tqrg=" + spot.FreqMhz.ToString("F6", inv),
            "tcall=" + Uri.EscapeDataString(tx.Callsign),
            "tgrid=" + Uri.EscapeDataString(tx.Grid ?? ""),
            "dbm=" + (tx.PowerDbm?.ToString(inv) ?? ""),
            "version=" + Uri.EscapeDataString(version),
            "mode=2",
        };
        return Endpoint + "?" + string.Join("&", q);
    }

    /// <summary>
    /// Splits a decoded WSPR message into transmitter, grid and power.
    /// Type 1 is "CALL GRID DBM" ("EA5IUE IM76 37"); type 2 drops the grid
    /// ("PJ4/K1ABC 37"); type 3 carries a hashed call and a 6-character grid
    /// ("&lt;PJ4/K1ABC&gt; FK52UD 37"). Returns null for anything else.
    /// </summary>
    internal static WsprMessageParts? ParseMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var tok = message.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length == 0) return null;

        string call = tok[0].Trim('<', '>');
        if (call.Length == 0) return null;

        string? grid = null;
        int? dbm = null;
        if (tok.Length >= 3 && LooksLikeGrid(tok[1]) && int.TryParse(tok[2], out int p3))
        {
            grid = tok[1].ToUpperInvariant();
            dbm = p3;
        }
        else if (tok.Length >= 2 && int.TryParse(tok[1], out int p2))
        {
            dbm = p2;                              // type 2: call + power, no grid
        }
        else if (tok.Length >= 2 && LooksLikeGrid(tok[1]))
        {
            grid = tok[1].ToUpperInvariant();
        }
        else if (tok.Length > 1)
        {
            return null;                           // not a station report
        }
        return new WsprMessageParts(call.ToUpperInvariant(), grid, dbm);
    }

    /// <summary>Maidenhead: 4 or 6 characters, letters then digits (then letters).</summary>
    private static bool LooksLikeGrid(string s)
    {
        if (s.Length != 4 && s.Length != 6) return false;
        if (!char.IsLetter(s[0]) || !char.IsLetter(s[1])) return false;
        if (!char.IsDigit(s[2]) || !char.IsDigit(s[3])) return false;
        return s.Length == 4 || (char.IsLetter(s[4]) && char.IsLetter(s[5]));
    }
}
