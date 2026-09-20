// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR),
//                         Ramón Martínez (EA5IUE), and contributors.
//
// PSK Reporter upload. The wire format is IPFIX (RFC 7011) over UDP to
// report.pskreporter.info:4739, with PSK Reporter's two private templates —
// the layout below follows freedv-gui 2.1.0 src/reporting/pskreporter.cpp
// (GPL-2.1) field for field:
//
//   header      0x000A, length, export time, sequence number, observation id
//   RX template 0x9992: receiverCallsign, receiverLocator, decodingSoftware
//   TX template 0x9993: senderCallsign, frequency (5 B), sNR (1 B), mode,
//                       informationSource (1 B), flowStartSeconds (4 B)
//
// Strings are length-prefixed (one byte), sets are padded to a 4-byte
// boundary, and every integer is big-endian.
//
// Cadence: spots are collected and flushed on a slow timer. PSK Reporter asks
// clients not to report more often than every five minutes, and a report with
// no spots is skipped entirely — a station that hears nothing sends nothing.

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Hosting.Digital;

/// <summary>One heard station, as PSK Reporter's sender record.</summary>
public sealed record PskSpot(string Callsign, long FrequencyHz, int SnrDb, string Mode, long FlowStartUnix);

public sealed class PskReporterUploader
{
    internal const string Host = "report.pskreporter.info";
    internal const int Port = 4739;

    // The two private templates, verbatim (freedv-gui pskreporter.cpp).
    private static readonly byte[] RxTemplate =
    {
        0x00, 0x03, 0x00, 0x24, 0x99, 0x92, 0x00, 0x03, 0x00, 0x00,
        0x80, 0x02, 0xFF, 0xFF, 0x00, 0x00, 0x76, 0x8F,
        0x80, 0x04, 0xFF, 0xFF, 0x00, 0x00, 0x76, 0x8F,
        0x80, 0x08, 0xFF, 0xFF, 0x00, 0x00, 0x76, 0x8F,
        0x00, 0x00,
    };
    private static readonly byte[] TxTemplate =
    {
        0x00, 0x02, 0x00, 0x34, 0x99, 0x93, 0x00, 0x06,
        0x80, 0x01, 0xFF, 0xFF, 0x00, 0x00, 0x76, 0x8F,
        0x80, 0x05, 0x00, 0x05, 0x00, 0x00, 0x76, 0x8F,
        0x80, 0x06, 0x00, 0x01, 0x00, 0x00, 0x76, 0x8F,
        0x80, 0x0A, 0xFF, 0xFF, 0x00, 0x00, 0x76, 0x8F,
        0x80, 0x0B, 0x00, 0x01, 0x00, 0x00, 0x76, 0x8F,
        0x00, 0x96, 0x00, 0x04,
    };

    private readonly ILogger _log;
    private readonly object _sync = new();
    private readonly List<PskSpot> _pending = new();
    private readonly uint _observationId = (uint)Random.Shared.Next(1, int.MaxValue);
    private uint _sequence;

    public PskReporterUploader(ILogger log) => _log = log;

    /// <summary>Uploaded spots since start — surfaced by /spotting/status.</summary>
    public int Uploaded { get; private set; }

    public int PendingCount { get { lock (_sync) return _pending.Count; } }

    /// <summary>Queues a heard station. Duplicates in one report are collapsed.</summary>
    public void Add(PskSpot spot)
    {
        lock (_sync)
        {
            if (_pending.Count >= 512) return;    // nothing sane reports this much
            int i = _pending.FindIndex(s =>
                string.Equals(s.Callsign, spot.Callsign, StringComparison.OrdinalIgnoreCase)
                && s.Mode == spot.Mode);
            if (i >= 0) _pending[i] = spot;       // keep the most recent report
            else _pending.Add(spot);
        }
    }

    public void Clear() { lock (_sync) _pending.Clear(); }

    /// <summary>Sends everything queued as one datagram. No spots, no packet.</summary>
    public async Task FlushAsync(SpottingSettings id, string software, CancellationToken ct)
    {
        if (!id.IdentityResolved) return;
        PskSpot[] spots;
        lock (_sync)
        {
            if (_pending.Count == 0) return;
            spots = _pending.ToArray();
            _pending.Clear();
        }

        var datagram = BuildDatagram(
            id.Callsign, id.Grid, software, spots,
            sequence: unchecked(_sequence++),
            observationId: _observationId,
            exportTimeUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        try
        {
            using var udp = new UdpClient();
            await udp.SendAsync(datagram, datagram.Length, Host, Port).WaitAsync(ct).ConfigureAwait(false);
            Uploaded += spots.Length;
            _log.LogInformation("spotting.pskreporter: reported {N} spot(s)", spots.Length);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            _log.LogInformation("spotting.pskreporter: send failed ({Reason})", ex.Message);
        }
    }

    /// <summary>
    /// The complete IPFIX datagram for one report: header, both templates, the
    /// receiver record and the sender records.
    /// </summary>
    internal static byte[] BuildDatagram(
        string receiverCallsign, string receiverGrid, string software,
        IReadOnlyList<PskSpot> spots, uint sequence, uint observationId, long exportTimeUnix)
    {
        int rxSet = PadTo4(4 + 1 + Len(receiverCallsign) + 1 + Len(receiverGrid) + 1 + Len(software));
        int txSet = spots.Count == 0 ? 0 : PadTo4(4 + spots.Sum(SenderRecordSize));
        int total = 16 + RxTemplate.Length + (spots.Count > 0 ? TxTemplate.Length : 0) + rxSet + txSet;

        var buf = new byte[total];
        var span = buf.AsSpan();

        // ---- message header -------------------------------------------------
        BinaryPrimitives.WriteUInt16BigEndian(span[..2], 0x000A);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], (ushort)total);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..8], (uint)exportTimeUnix);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..16], observationId);
        int at = 16;

        // ---- templates ------------------------------------------------------
        RxTemplate.CopyTo(span[at..]);
        at += RxTemplate.Length;
        if (spots.Count > 0)
        {
            TxTemplate.CopyTo(span[at..]);
            at += TxTemplate.Length;
        }

        // ---- receiver set (0x9992) -----------------------------------------
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(at, 2), 0x9992);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(at + 2, 2), (ushort)rxSet);
        int p = at + 4;
        p = WriteString(span, p, receiverCallsign);
        p = WriteString(span, p, receiverGrid);
        _ = WriteString(span, p, software);
        at += rxSet;

        // ---- sender set (0x9993) -------------------------------------------
        if (spots.Count > 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(at, 2), 0x9993);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(at + 2, 2), (ushort)txSet);
            p = at + 4;
            foreach (var s in spots)
            {
                p = WriteString(span, p, s.Callsign);
                // frequency: 5 bytes, big-endian (room for 10 GHz and up)
                span[p++] = (byte)((s.FrequencyHz >> 32) & 0xFF);
                span[p++] = (byte)((s.FrequencyHz >> 24) & 0xFF);
                span[p++] = (byte)((s.FrequencyHz >> 16) & 0xFF);
                span[p++] = (byte)((s.FrequencyHz >> 8) & 0xFF);
                span[p++] = (byte)(s.FrequencyHz & 0xFF);
                span[p++] = unchecked((byte)(sbyte)Math.Clamp(s.SnrDb, -128, 127));
                p = WriteString(span, p, s.Mode);
                span[p++] = 1;                     // informationSource: 1 = automatic decode
                BinaryPrimitives.WriteUInt32BigEndian(span.Slice(p, 4), (uint)s.FlowStartUnix);
                p += 4;
            }
        }
        return buf;
    }

    internal static int SenderRecordSize(PskSpot s) =>
        1 + Len(s.Callsign) + 5 + 1 + 1 + Len(s.Mode) + 1 + 4;

    private static int Len(string s) => Encoding.ASCII.GetByteCount(s);
    private static int PadTo4(int n) => (n % 4) == 0 ? n : n + (4 - (n % 4));

    private static int WriteString(Span<byte> span, int at, string value)
    {
        int n = Math.Min(Len(value), 254);
        span[at] = (byte)n;
        Encoding.ASCII.GetBytes(value.AsSpan(0, n), span.Slice(at + 1, n));
        return at + 1 + n;
    }

    /// <summary>
    /// The transmitting station in an FT8/FT4 message: the caller in "CQ CALL
    /// GRID" / "CQ DX CALL GRID", otherwise the second call in "TO FROM report"
    /// — which is the station actually on the air. Returns null for anything
    /// without a plausible callsign (73, RR73-only, telemetry, free text).
    /// </summary>
    internal static string? ExtractSenderCallsign(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var tok = message.Trim().ToUpperInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length < 2) return null;

        if (tok[0] == "CQ")
        {
            // "CQ CALL GRID", or "CQ DX CALL GRID" / "CQ EU CALL GRID" where the
            // second token is a directional tag, not a call.
            for (int i = 1; i < tok.Length; i++)
                if (LooksLikeCallsign(tok[i])) return Strip(tok[i]);
            return null;
        }
        return LooksLikeCallsign(tok[1]) ? Strip(tok[1]) : null;
    }

    private static string Strip(string call) => call.Trim('<', '>');

    // Protocol words that pass the shape test below but name no station.
    private static readonly string[] NotCallsigns = { "RR73", "RRR", "73", "RR", "TU", "DX", "CQ", "QRZ" };

    /// <summary>
    /// A callsign has at least one digit and one letter, 3–11 characters, and
    /// only callsign characters — and is neither a protocol word (RR73) nor a
    /// Maidenhead locator (IO91, IM76HD), both of which pass that shape test.
    /// </summary>
    private static bool LooksLikeCallsign(string s)
    {
        s = s.Trim('<', '>');
        if (s.Length < 3 || s.Length > 11) return false;
        if (Array.IndexOf(NotCallsigns, s) >= 0) return false;
        if (LooksLikeGrid(s)) return false;
        bool digit = false, letter = false;
        foreach (char c in s)
        {
            if (char.IsAsciiDigit(c)) digit = true;
            else if (char.IsAsciiLetterUpper(c)) letter = true;
            else if (c != '/') return false;
        }
        return digit && letter;
    }

    /// <summary>Maidenhead: two letters, two digits, optionally two more letters.</summary>
    private static bool LooksLikeGrid(string s)
    {
        if (s.Length != 4 && s.Length != 6) return false;
        if (!char.IsAsciiLetterUpper(s[0]) || !char.IsAsciiLetterUpper(s[1])) return false;
        if (!char.IsAsciiDigit(s[2]) || !char.IsAsciiDigit(s[3])) return false;
        return s.Length == 4 || (char.IsAsciiLetterUpper(s[4]) && char.IsAsciiLetterUpper(s[5]));
    }
}
