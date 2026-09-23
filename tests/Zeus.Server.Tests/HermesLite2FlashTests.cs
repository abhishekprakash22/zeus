// SPDX-License-Identifier: GPL-2.0-or-later
//
// The refusals that keep an HL2 alive, pinned without a radio.
//
// The one that matters is the gateware floor. Before release 20200329
// (version 7.0) the HL2 has no slot2: every write lands in the only image
// the board has, and a failed update leaves it with no working gateware and
// no way back without a USB Blaster. From 7.0 an Ethernet update writes
// slot2 and the factory image in slot1 stays as the fallback.
//
// hermeslite.py, the reference implementation, does not check this. These
// tests exist so that Zeus being stricter stays a decision rather than
// something a later edit quietly drops.

using System.Net;
using System.Net.NetworkInformation;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class HermesLite2FlashTests
{
    // VERSION_MAJOR in hermeslite_core.v is the version times ten
    // (8'd74 is gateware 7.4), so 70 is exactly the 7.0 floor.
    private const byte V74 = 74, V70 = 70, V69 = 69, V54 = 54;

    private static DiscoveredRadio Radio(
        byte codeVersion = V74,
        int boardId = 5,
        bool busy = false,
        HpsdrBoardKind board = HpsdrBoardKind.HermesLite2)
    {
        // Byte 0x14 carries the board id in its low six bits; the top two are
        // the wideband type, set here so the mask is actually exercised.
        var raw = new byte[60];
        raw[0] = 0xEF; raw[1] = 0xFE;
        raw[2] = busy ? ReplyParser.StatusBusy : ReplyParser.StatusIdle;
        raw[9] = codeVersion;
        raw[0x0A] = 0x06;                                  // radio_id, NOT the board id
        raw[0x14] = (byte)(0x80 | (boardId & 0x3F));
        return new DiscoveredRadio(
            Ip: IPAddress.Parse("192.168.1.50"),
            Mac: PhysicalAddress.Parse("001122334455"),
            Board: board,
            FirmwareVersion: codeVersion,
            FirmwareString: $"{codeVersion}.2",
            Details: new DiscoveryDetails(
                RawReply: raw,
                RawBoardId: 0x06,
                Busy: busy,
                FixedIpEnabled: false,
                FixedIpOverridesDhcp: false,
                MacAddressModified: false,
                FixedIpAddress: null,
                GatewareBuild: 0,
                HermesLite2MinorVersion: 2));
    }

    [Fact]
    public void AGoodBoardWithAMatchingImageIsAccepted()
    {
        Assert.Null(HermesLite2FlashService.RefusalFor(Radio(), "hl2b5up_main.rbf"));
    }

    [Fact]
    public void GatewareBelowSevenPointZeroIsRefused()
    {
        // 6.9 is the last version with no slot2.
        var refusal = HermesLite2FlashService.RefusalFor(Radio(codeVersion: V69), "hl2b5up_main.rbf");

        Assert.NotNull(refusal);
        Assert.Contains("6.9", refusal);
        Assert.Contains("slot2", refusal);
        // The operator is told what to do instead, not merely told no.
        Assert.Contains("USB Blaster", refusal);
    }

    [Fact]
    public void SevenPointZeroItselfIsAccepted()
    {
        // The floor is inclusive: 7.0 is the release that introduced slot2.
        Assert.Null(HermesLite2FlashService.RefusalFor(Radio(codeVersion: V70), "hl2b5up_main.rbf"));
    }

    [Fact]
    public void AnHl1EraVersionIsRefused()
    {
        // 5.4 is what hermeslite_core.v reports for BOARD==2 — well under the
        // floor, and the check must not be fooled by it being a plausible
        // two-digit number.
        Assert.NotNull(HermesLite2FlashService.RefusalFor(Radio(codeVersion: V54), "hl2b5up_main.rbf"));
    }

    [Fact]
    public void AStreamingRadioIsRefused()
    {
        var refusal = HermesLite2FlashService.RefusalFor(Radio(busy: true), "hl2b5up_main.rbf");

        Assert.NotNull(refusal);
        Assert.Contains("streaming", refusal);
    }

    [Fact]
    public void AnImageForAnotherBoardRevisionIsRefused()
    {
        // A board 5 unit offered a board 4 image.
        var refusal = HermesLite2FlashService.RefusalFor(Radio(boardId: 5), "hl2b4up_main.rbf");

        Assert.NotNull(refusal);
        Assert.Contains("hl2b5", refusal);
    }

    [Fact]
    public void BoardIdComesFromByte0x14_NotTheRadioId()
    {
        // The decoder in hermeslite.py reads radio_id at 0x0A and board_id as
        // the low six bits of 0x14. Reading the wrong one would accept an
        // image for the wrong board revision, so it is pinned explicitly.
        var radio = Radio(boardId: 4);

        Assert.Equal(4, HermesLite2FlashService.BoardIdOf(radio));
        Assert.Equal(0x06, radio.Details.RawBoardId);
    }

    [Fact]
    public void ARadioThatIsNotAnHl2IsRefused()
    {
        var refusal = HermesLite2FlashService.RefusalFor(
            Radio(board: HpsdrBoardKind.Hermes), "hl2b5up_main.rbf");

        Assert.NotNull(refusal);
        Assert.Contains("not a Hermes-Lite 2", refusal);
    }

    [Fact]
    public void NothingDiscoveredIsRefused()
    {
        Assert.NotNull(HermesLite2FlashService.RefusalFor(null, "hl2b5up_main.rbf"));
    }

    [Fact]
    public void NothingDiscoveredWhileConnectedSaysWhy()
    {
        // The state every operator is actually in when they open the panel:
        // Zeus is streaming from the radio, so the radio does not answer
        // discovery. "Not found" would send them hunting for a network fault
        // that is not there.
        var refusal = HermesLite2FlashService.RefusalFor(null, "hl2b5up_main.rbf", connected: true);

        Assert.NotNull(refusal);
        Assert.Contains("disconnect", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnconventionalFileNameSkipsTheRevisionCheck()
    {
        // A gateware you built yourself is not obliged to be called
        // hl2b5up_anything. hermeslite.py makes the same check optional
        // (filename_checks), and refusing here would block the one case where
        // a local file is the whole point.
        Assert.Null(HermesLite2FlashService.RefusalFor(Radio(boardId: 5), "my_own_build.rbf"));
        Assert.False(HermesLite2FlashService.LooksConventional("my_own_build.rbf"));
    }

    [Fact]
    public void AConventionalNameIsStillHeldToItsClaim()
    {
        // Waiving the check for unnamed files must not waive it for a file
        // that says which board it is for and says the wrong one.
        Assert.True(HermesLite2FlashService.LooksConventional("hl2b4up_main.rbf"));
        Assert.NotNull(HermesLite2FlashService.RefusalFor(Radio(boardId: 5), "hl2b4up_main.rbf"));
    }

    [Fact]
    public void TheSevenPointZeroFloorIsNeverWaivedByFileName()
    {
        // The revision check is a convenience; this one keeps the board
        // alive. An unconventional name must not slip past it.
        Assert.NotNull(HermesLite2FlashService.RefusalFor(
            Radio(codeVersion: V69), "my_own_build.rbf"));
    }
}
