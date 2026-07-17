// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Regression for issue #486 (zeus-1sc): QRZ.com uploads used to emit
// local-clock times because LiteDB's default BsonMapper can round-trip
// DateTime with Kind=Local on read, even when the field was originally UTC.
// .ToString() formats the stored value without timezone conversion, so the
// local-kinded value reached QRZ as if it were UTC and stamped every QSO at
// the operator's wall-clock hour. Award credit and DXCC matching for anyone
// outside UTC was wrong.
//
// The logbook plugin pins the ADIF export path. Core keeps this QRZ egress
// regression because QRZ publishing remains in Zeus core.

using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class AdifUtcTimezoneTests
{
    // 19:30:00 UTC on 2026-05-24. Pick a wall-clock hour high enough that
    // a CET-summer browser (UTC+2) shifts it into the next day — without
    // the fix, ToString("yyyyMMdd") on a Local-kinded version would emit
    // either 20260524 or 20260525 depending on the test box's TZ, but
    // never the right value consistently. With the fix it's always
    // 20260524 / 193000.
    private static readonly DateTime QsoUtc =
        new(2026, 5, 24, 19, 30, 0, DateTimeKind.Utc);
    private const string ExpectedDate = "20260524";
    private const string ExpectedTime = "193000";

    /// <summary>Build a Kind=Local DateTime that represents <see cref="QsoUtc"/>
    /// in the test host's local zone. This matches the shape that can reach
    /// QRZ after a LiteDB round-trip: the moment in time is right, but the
    /// <see cref="DateTime.Kind"/> is wrong. A naive <c>.ToString("HHmmss")</c>
    /// on this value would format the local clock, not the UTC clock.</summary>
    private static DateTime QsoSeenAsLocal => QsoUtc.ToLocalTime();

    [Fact]
    public void QrzService_AdifConversion_EmitsUtcClock_EvenWhenKindIsLocal()
    {
        var entry = new LogEntry(
            Id: "test-id",
            QsoDateTimeUtc: QsoSeenAsLocal,
            Callsign: "EA5IUE",
            Name: null,
            FrequencyMhz: 21.065,
            Band: "15m",
            Mode: "CW",
            RstSent: "599",
            RstRcvd: "599",
            Grid: null,
            Country: null,
            Dxcc: null,
            CqZone: null,
            ItuZone: null,
            State: null,
            Comment: null,
            CreatedUtc: DateTime.UtcNow);

        var adif = QrzService.ConvertLogEntryToAdif(entry);

        Assert.Contains($"<QSO_DATE:8>{ExpectedDate}", adif);
        Assert.Contains($"<TIME_ON:6>{ExpectedTime}", adif);
    }
}
