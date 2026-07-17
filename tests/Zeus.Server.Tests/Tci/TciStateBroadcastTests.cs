// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;
using Zeus.Server.Tci;

namespace Zeus.Server.Tests.Tci;

public class TciStateBroadcastTests
{
    [Fact]
    public void DiffCommands_WithNullPrevious_EmitsFullSet()
    {
        var next = CreateTestState();

        var commands = TciStateBroadcast.DiffCommands(null, next);

        Assert.Equal(
            new[]
            {
                "mute:false;",
                "rx_mute:0,false;",
                "rx_mute:1,false;",
                "rit_enable:0,false;",
                "rit_offset:0,0;",
                "xit_enable:0,false;",
                "xit_offset:0,0;",
                "agc_mode:0,MEDIUM;",
                "sql_enable:0,false;",
                "sql_level:0,0;",
                "lock:0,false;",
                "vfo_lock:0,0,false;",
            },
            commands);
    }

    [Fact]
    public void DiffCommands_WithUnchangedState_EmitsNothing()
    {
        var prev = CreateTestState() with
        {
            Rx1Muted = true,
            Rx2Muted = true,
            RitEnabled = true,
            RitHz = 250,
            XitEnabled = true,
            XitHz = -180,
            Agc = new AgcConfig(AgcMode.Fast),
            Squelch = new SquelchConfig(Enabled: true, Level: 40),
            VfoLocked = true,
        };

        var commands = TciStateBroadcast.DiffCommands(prev, prev with { });

        Assert.Empty(commands);
    }

    [Fact]
    public void DiffCommands_WithDefaultEquivalentAgcAndSquelch_EmitsNothing()
    {
        var prev = CreateTestState() with { Agc = null, Squelch = null };
        var next = prev with
        {
            Agc = new AgcConfig(AgcMode.Med),
            Squelch = new SquelchConfig(),
        };

        var commands = TciStateBroadcast.DiffCommands(prev, next);

        Assert.Empty(commands);
    }

    [Fact]
    public void DiffCommands_WhenRx1MuteChanges_EmitsMasterAndRx1Mute()
    {
        var prev = CreateTestState();
        var next = prev with { Rx1Muted = true };

        var commands = TciStateBroadcast.DiffCommands(prev, next);

        Assert.Equal(
            new[]
            {
                "mute:true;",
                "rx_mute:0,true;",
            },
            commands);
    }

    [Fact]
    public void DiffCommands_WhenRx2MuteChanges_EmitsRx2MuteOnly()
    {
        var prev = CreateTestState();
        var next = prev with { Rx2Muted = true };

        var commands = TciStateBroadcast.DiffCommands(prev, next);

        Assert.Equal(new[] { "rx_mute:1,true;" }, commands);
    }

    [Fact]
    public void DiffCommands_WhenRitEnableChanges_EmitsRitEnable()
    {
        var prev = CreateTestState();
        var next = prev with { RitEnabled = true };

        var commands = TciStateBroadcast.DiffCommands(prev, next);

        Assert.Equal(new[] { "rit_enable:0,true;" }, commands);
    }

    [Fact]
    public void DiffCommands_WhenRitOffsetChanges_EmitsIntegerRitOffset()
    {
        var prev = CreateTestState();
        var next = prev with { RitHz = 250 };

        var commands = TciStateBroadcast.DiffCommands(prev, next);

        Assert.Equal(new[] { "rit_offset:0,250;" }, commands);
    }

    [Fact]
    public void DiffCommands_WhenXitEnableChanges_EmitsXitEnable()
    {
        var prev = CreateTestState();
        var next = prev with { XitEnabled = true };

        var commands = TciStateBroadcast.DiffCommands(prev, next);

        Assert.Equal(new[] { "xit_enable:0,true;" }, commands);
    }

    [Fact]
    public void DiffCommands_WhenXitOffsetChanges_EmitsIntegerXitOffset()
    {
        var prev = CreateTestState();
        var next = prev with { XitHz = -180 };

        var commands = TciStateBroadcast.DiffCommands(prev, next);

        Assert.Equal(new[] { "xit_offset:0,-180;" }, commands);
    }

    [Fact]
    public void DiffCommands_WhenAgcModeChanges_EmitsMappedAgcMode()
    {
        var prev = CreateTestState();
        var next = prev with { Agc = new AgcConfig(AgcMode.Fast) };

        var commands = TciStateBroadcast.DiffCommands(prev, next);

        Assert.Equal(new[] { "agc_mode:0,FAST;" }, commands);
    }

    [Fact]
    public void DiffCommands_WhenSquelchPairChanges_EmitsSqlPair()
    {
        var prev = CreateTestState();
        var next = prev with { Squelch = new SquelchConfig(Enabled: true, Level: 40) };

        var commands = TciStateBroadcast.DiffCommands(prev, next);

        Assert.Equal(
            new[]
            {
                "sql_enable:0,true;",
                "sql_level:0,40;",
            },
            commands);
    }

    [Fact]
    public void DiffCommands_WhenSquelchEnableChanges_EmitsSqlEnableOnly()
    {
        var prev = CreateTestState() with { Squelch = new SquelchConfig(Enabled: false, Level: 40) };
        var next = prev with { Squelch = new SquelchConfig(Enabled: true, Level: 40) };

        var commands = TciStateBroadcast.DiffCommands(prev, next);

        Assert.Equal(new[] { "sql_enable:0,true;" }, commands);
    }

    [Fact]
    public void DiffCommands_WhenSquelchLevelChanges_EmitsSqlLevelOnly()
    {
        var prev = CreateTestState() with { Squelch = new SquelchConfig(Enabled: true, Level: 20) };
        var next = prev with { Squelch = new SquelchConfig(Enabled: true, Level: 40) };

        var commands = TciStateBroadcast.DiffCommands(prev, next);

        Assert.Equal(new[] { "sql_level:0,40;" }, commands);
    }

    [Fact]
    public void DiffCommands_WhenVfoLockChanges_EmitsLockAndVfoLock()
    {
        var prev = CreateTestState();
        var next = prev with { VfoLocked = true };

        var commands = TciStateBroadcast.DiffCommands(prev, next);

        Assert.Equal(
            new[]
            {
                "lock:0,true;",
                "vfo_lock:0,0,true;",
            },
            commands);
    }

    private static StateDto CreateTestState() =>
        new(
            Status: ConnectionStatus.Connected,
            Endpoint: "192.168.1.100:1024",
            VfoHz: 14074000,
            Mode: RxMode.USB,
            FilterLowHz: 150,
            FilterHighHz: 2850,
            SampleRate: 192000,
            AgcTopDb: 80.0,
            AttenDb: 0,
            Nr: new NrConfig(),
            ZoomLevel: 1,
            AutoAttEnabled: true,
            AttOffsetDb: 0,
            AdcOverloadWarning: false);
}
