// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR),
//                         Ramón Martínez (EA5IUE), and contributors.
//
// HermesLite2FlashService — in-app gateware update for the Hermes-Lite 2,
// over Ethernet. The counterpart to SaturnFlashService, which does the same
// job for the ANAN G2 but over PCIe register I/O on /dev/xdma0_user and so
// only works with Zeus running on the radio itself. The HL2 is updated from
// anywhere on the LAN.
//
// The protocol is the one softerhardware's hermeslite.py implements, and
// Quisk before it (Jim N2ADR). UDP to port 1024, replies are 60 bytes with
// the EF FE cookie and a type byte at offset 2:
//
//   erase    EF FE 03 02 + 56 zero bytes            -> reply type 3
//   program  EF FE 03 01 + blocks(BE32) + 256 bytes -> reply type 4, per block
//
// The block count rides in every program packet, not just the first. The
// last block is padded to 256 with 0xFF. The HL2 reboots itself on success.
//
// WHY THIS IS SAFE, and the one case where it is not:
//
// From gateware 20200329 (version 7.0) the HL2 keeps a factory image in
// slot1 and an application image in slot2. On power-up the factory image
// loads first and tries to hand over to the application; if that fails, the
// factory image keeps running. An Ethernet update writes slot2 ONLY, so the
// rescue image is never the one at risk — structurally the same property
// that makes the Saturn path safe, where only the primary slot is written
// and the golden image is never addressed.
//
// Below 7.0 there is no slot2. Every write lands in slot1, and a failed
// update leaves the board with no working gateware and no way back without
// a USB Blaster or a Raspberry Pi. So Zeus refuses those boards outright.
// Note that hermeslite.py does NOT check this — it will happily write a
// pre-7.0 board. Zeus has the version in hand from discovery, and declining
// to look would be a decision, not an oversight.

using System.Net;
using System.Net.Sockets;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

public sealed class HermesLite2FlashService
{
    private const int HlPort = 1024;
    private const int ReplyBytes = 60;
    private const int BlockBytes = 256;

    // Reply type bytes, at offset 2 of the 60-byte answer.
    private const byte ReplyErased = 3;
    private const byte ReplyProgrammed = 4;

    // VERSION_MAJOR in hermeslite_core.v is the version times ten
    // (localparam VERSION_MAJOR = (BOARD==2) ? 8'd54 : 8'd74 — 7.4), so
    // gateware 7.0, where slot2 begins, is the byte 70.
    private const byte MinSlot2Major = 70;

    // .rbf files open with 32 bytes of 0xFF then this. A truncated download
    // or an .sof/.pof by mistake is caught here rather than by the flash.
    private static readonly byte[] RbfMagic = { 0x6A, 0xF7, 0xF7, 0xF7 };
    private const int RbfPadBytes = 32;

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    private readonly TxService _tx;
    private readonly IRadioDiscovery _discovery;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<HermesLite2FlashService> _log;

    private readonly object _lock = new();
    private string _phase = "idle";   // idle|downloading|erasing|writing|done|error
    private double _progress;
    private string _detail = "";
    private string? _error;

    public HermesLite2FlashService(
        TxService tx, IRadioDiscovery discovery,
        IHttpClientFactory http, ILogger<HermesLite2FlashService> log)
    {
        _tx = tx;
        _discovery = discovery;
        _http = http;
        _log = log;
    }

    public object Status()
    {
        lock (_lock)
        {
            return new
            {
                phase = _phase,
                progress = _progress,
                detail = _detail,
                error = _error,
                transport = "ethernet",
                slot = "application (slot2) — the factory image in slot1 is never written",
            };
        }
    }

    /// <summary>
    /// The board id the .rbf filename has to match. It is NOT
    /// DiscoveryDetails.RawBoardId: that is byte 0x0A, which hermeslite.py
    /// calls radio_id. The board id is the low six bits of byte 0x14, and it
    /// is the number in "hl2b5up_main.rbf".
    /// </summary>
    internal static int BoardIdOf(DiscoveredRadio radio) =>
        radio.Details.RawReply.Length > 0x14 ? radio.Details.RawReply[0x14] & 0x3F : -1;

    /// <summary>Everything that would make a write unsafe, in one place, so
    /// it can be tested without a radio and reported without starting a job.</summary>
    internal static string? RefusalFor(DiscoveredRadio? radio, string fileName)
    {
        if (radio is null)
            return "no Hermes-Lite 2 answered discovery";
        if (radio.Board != HpsdrBoardKind.HermesLite2)
            return $"the radio that answered is a {radio.Board}, not a Hermes-Lite 2";
        if (radio.Details.Busy)
            return "the HL2 is streaming — disconnect the radio before updating gateware";

        if (radio.FirmwareVersion < MinSlot2Major)
        {
            var shown = $"{radio.FirmwareVersion / 10}.{radio.FirmwareVersion % 10}";
            return $"this HL2 runs gateware {shown}, which predates 7.0 and has no slot2. " +
                   "Every write would land in the only image the board has, and a failure " +
                   "would leave it with no working gateware. Update it with a USB Blaster " +
                   "or a Raspberry Pi first; Zeus will not write it over Ethernet.";
        }

        var boardId = BoardIdOf(radio);
        if (boardId < 0)
            return "discovery reply too short to read the board id";
        if (fileName.Length > 0 && !fileName.Contains($"hl2b{boardId}", StringComparison.OrdinalIgnoreCase))
            return $"this board reports id {boardId}, so it needs an hl2b{boardId} image — " +
                   $"'{fileName}' is for a different board revision";

        return null;
    }

    /// <summary>What a write would do, without doing it: which board answered,
    /// what it runs, and whether the chosen image is allowed to touch it.</summary>
    public async Task<object> CompareAsync(string url, CancellationToken ct)
    {
        var radio = await FindAsync(ct).ConfigureAwait(false);
        var name = FileNameOf(url);
        var refusal = RefusalFor(radio, name);
        if (radio is null) return new { ok = false, error = refusal };

        return new
        {
            ok = refusal is null,
            error = refusal,
            ip = radio.Ip.ToString(),
            boardId = BoardIdOf(radio),
            runningGateware = $"{radio.FirmwareVersion / 10}.{radio.FirmwareVersion % 10}" +
                              (radio.Details.HermesLite2MinorVersion is { } m ? $".{m}" : ""),
            busy = radio.Details.Busy,
            image = name,
        };
    }

    /// <summary>
    /// Start an update. Async because every guard that matters needs the
    /// discovery reply, and refusing before a multi-megabyte download is
    /// better than refusing after it.
    /// </summary>
    public async Task<(bool Ok, string? Refusal)> StartAsync(string url, CancellationToken ct)
    {
        if (_tx.MoxOwner is not null)
            return (false, "TX is keyed — unkey before updating gateware");
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return (false, "gateware URL must be https");

        lock (_lock)
        {
            if (_phase is not ("idle" or "done" or "error"))
                return (false, "a gateware update is already running");
        }

        var radio = await FindAsync(ct).ConfigureAwait(false);
        var refusal = RefusalFor(radio, FileNameOf(url));
        if (refusal is not null) return (false, refusal);

        lock (_lock)
        {
            // Re-checked under the lock: the discovery await above is a wide
            // enough window for a second request to have claimed the job.
            if (_phase is not ("idle" or "done" or "error"))
                return (false, "a gateware update is already running");
            _phase = "downloading"; _progress = 0; _detail = url; _error = null;
        }

        var ip = radio!.Ip;
        _ = Task.Run(() => RunJob(url, ip), CancellationToken.None);
        return (true, null);
    }

    private async Task<DiscoveredRadio?> FindAsync(CancellationToken ct)
    {
        var found = await _discovery
            .DiscoverAsync(TimeSpan.FromMilliseconds(700), ct)
            .ConfigureAwait(false);
        return found.FirstOrDefault(r => r.Board == HpsdrBoardKind.HermesLite2)
               ?? found.FirstOrDefault();
    }

    private static string FileNameOf(string url)
    {
        var cut = url.Split('?')[0].TrimEnd('/');
        var slash = cut.LastIndexOf('/');
        return slash >= 0 ? cut[(slash + 1)..] : cut;
    }

    private async Task RunJob(string url, IPAddress ip)
    {
        try
        {
            byte[] image;
            using (var client = _http.CreateClient())
            {
                client.Timeout = TimeSpan.FromMinutes(5);
                image = await client.GetByteArrayAsync(url).ConfigureAwait(false);
            }
            ValidateRbf(image);

            var blocks = (image.Length + BlockBytes - 1) / BlockBytes;
            _log.LogWarning(
                "hl2.gateware: writing slot2 on {Ip} — {Bytes} bytes, {Blocks} blocks, from {Url}",
                ip, image.Length, blocks, url);

            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = (int)StepTimeout.TotalMilliseconds;
            var target = new IPEndPoint(ip, HlPort);

            Set("erasing", 0, "erasing the application slot");
            var erase = new byte[ReplyBytes];
            erase[0] = 0xEF; erase[1] = 0xFE; erase[2] = 0x03; erase[3] = 0x02;
            if (Exchange(udp, target, erase) is not ReplyErased)
                throw new InvalidOperationException("the HL2 did not acknowledge the erase");

            // The block count is part of the fixed header of every program
            // packet, not a one-off preamble — hermeslite.py builds `cmd`
            // once and prepends it to each block.
            var packet = new byte[4 + 4 + BlockBytes];
            packet[0] = 0xEF; packet[1] = 0xFE; packet[2] = 0x03; packet[3] = 0x01;
            packet[4] = (byte)(blocks >> 24); packet[5] = (byte)(blocks >> 16);
            packet[6] = (byte)(blocks >> 8); packet[7] = (byte)blocks;

            for (int b = 0; b < blocks; b++)
            {
                var offset = b * BlockBytes;
                var count = Math.Min(BlockBytes, image.Length - offset);
                // A short final block is padded with 0xFF — erased-flash
                // value, so the tail programs to the same state it erased to.
                packet.AsSpan(8, BlockBytes).Fill(0xFF);
                image.AsSpan(offset, count).CopyTo(packet.AsSpan(8, count));

                if (Exchange(udp, target, packet) is not ReplyProgrammed)
                    throw new InvalidOperationException(
                        $"the HL2 did not acknowledge block {b + 1} of {blocks}");

                if ((b & 0x1F) == 0 || b == blocks - 1)
                    Set("writing", (double)(b + 1) / blocks, $"block {b + 1} of {blocks}");
            }

            Set("done", 1, "written — the HL2 restarts on its own; the new gateware runs after that");
            _log.LogWarning("hl2.gateware: slot2 written on {Ip}, board restarting", ip);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _phase = "error";
                _error = ex.Message;
                // Said here as well as in the UI: on a board with slot2 this
                // is recoverable, and the operator should be told so rather
                // than left to assume the radio is gone.
                _detail = "power-cycle the HL2 and try again — the factory image in slot1 is untouched";
            }
            _log.LogError(ex, "hl2.gateware: update failed on {Ip}", ip);
        }
    }

    private static void ValidateRbf(byte[] image)
    {
        if (image.Length < RbfPadBytes + RbfMagic.Length)
            throw new InvalidOperationException("the downloaded file is too small to be an .rbf");
        for (int i = 0; i < RbfPadBytes; i++)
            if (image[i] != 0xFF)
                throw new InvalidOperationException(
                    "unexpected file header — this does not look like an .rbf gateware image");
        if (!image.AsSpan(RbfPadBytes, RbfMagic.Length).SequenceEqual(RbfMagic))
            throw new InvalidOperationException(
                "unexpected file header — this does not look like an .rbf gateware image");
    }

    /// <summary>One request, one reply, returning the reply's type byte.
    /// One attempt only, as the reference tool does: a retry after a write
    /// the board may already have applied is not a safe thing to guess at.</summary>
    private static int Exchange(UdpClient udp, IPEndPoint target, byte[] payload)
    {
        udp.Send(payload, payload.Length, target);
        var from = new IPEndPoint(IPAddress.Any, 0);
        byte[] reply;
        try { reply = udp.Receive(ref from); }
        catch (SocketException) { return -1; }
        if (reply.Length != ReplyBytes) return -1;
        if (reply[0] != 0xEF || reply[1] != 0xFE) return -1;
        if (!from.Address.Equals(target.Address)) return -1;
        return reply[2];
    }

    private void Set(string phase, double progress, string detail)
    {
        lock (_lock) { _phase = phase; _progress = progress; _detail = detail; _error = null; }
    }
}
