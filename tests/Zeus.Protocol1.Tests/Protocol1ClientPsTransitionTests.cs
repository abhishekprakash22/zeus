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

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Xunit;
using Zeus.Contracts;

namespace Zeus.Protocol1.Tests;

/// <summary>
/// Issue #1302: the HermesC10 (ANAN-G2E, P1) receiver count must NEVER be
/// flipped on a live stream — the classic-Hermes gateware applies the count
/// mid-frame into a free-running EP6 builder and one wrong-length frame
/// permanently sync-shifts the byte stream. These loopback tests pin the safe
/// stop/drain/pre-announce/restart transition
/// (<see cref="Protocol1Client.SetPsEnabledAsync"/>), the connect-while-armed
/// direct 4-DDC start, the start-handshake retry, and the feedback stall
/// watchdog. Fake-radio UDP socket on loopback, same pattern as
/// <see cref="Protocol1Client_AdcOverloadEventTests"/> — no real radio, no
/// off-host traffic.
/// </summary>
public class Protocol1ClientPsTransitionTests
{
    private enum PacketKind { Start, Stop, Ep2Data, Other }

    private sealed record ObservedPacket(PacketKind Kind, byte[] Raw);

    /// <summary>
    /// Loopback fake radio: records every datagram the client sends (in
    /// arrival order) and captures the client's endpoint from the first one.
    /// </summary>
    private sealed class FakeRadio : IDisposable
    {
        public readonly Socket Socket;
        private readonly Thread _thread;
        private readonly List<ObservedPacket> _packets = new();
        private volatile bool _stop;
        private readonly TaskCompletionSource<IPEndPoint> _clientEp =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeRadio()
        {
            Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveTimeout = 100,
            };
            Socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _thread = new Thread(ReceiveLoop) { IsBackground = true };
            _thread.Start();
        }

        public IPEndPoint LocalEp => (IPEndPoint)Socket.LocalEndPoint!;
        public Task<IPEndPoint> ClientEp => _clientEp.Task;

        private void ReceiveLoop()
        {
            var buf = new byte[2048];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (!_stop)
            {
                int n;
                try { n = Socket.ReceiveFrom(buf, ref remote); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { continue; }
                catch (Exception) { return; }
                _clientEp.TrySetResult((IPEndPoint)remote);
                var raw = buf.AsSpan(0, n).ToArray();
                var kind = Classify(raw);
                lock (_packets) _packets.Add(new ObservedPacket(kind, raw));
            }
        }

        private static PacketKind Classify(byte[] p)
        {
            if (p.Length >= 4 && p[0] == 0xEF && p[1] == 0xFE)
            {
                if (p[2] == 0x04) return (p[3] & 0x01) != 0 ? PacketKind.Start : PacketKind.Stop;
                if (p[2] == 0x01 && p[3] == 0x02 && p.Length == 1032) return PacketKind.Ep2Data;
            }
            return PacketKind.Other;
        }

        public List<ObservedPacket> Snapshot()
        {
            lock (_packets) return new List<ObservedPacket>(_packets);
        }

        public int Count { get { lock (_packets) return _packets.Count; } }

        public void Dispose()
        {
            _stop = true;
            try { Socket.Close(); } catch { }
            Socket.Dispose();
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Extract the NumReceiversMinusOne values carried by any Config
    /// (C0 &amp; 0xFE == 0x00) C&amp;C frame inside an EP2 data packet;
    /// empty when the packet carries no Config register.</summary>
    private static List<int> ConfigNumRxMinusOne(byte[] ep2)
    {
        var result = new List<int>();
        foreach (int frameStart in new[] { 8, 8 + 512 })
        {
            if (ep2[frameStart] != 0x7F || ep2[frameStart + 1] != 0x7F || ep2[frameStart + 2] != 0x7F)
                continue;
            byte c0 = ep2[frameStart + 3];
            if ((c0 & 0xFE) != 0x00) continue;
            byte c4 = ep2[frameStart + 7];
            result.Add((c4 >> 3) & 0x07);
        }
        return result;
    }

    private static byte[] BuildValid1DdcEp6(uint seq)
    {
        var packet = new byte[PacketParser.PacketLength];
        packet[0] = 0xEF; packet[1] = 0xFE; packet[2] = 0x01; packet[3] = 0x06;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), seq);
        foreach (int f in new[] { 8, 8 + 512 })
        {
            packet[f] = 0x7F; packet[f + 1] = 0x7F; packet[f + 2] = 0x7F;
        }
        return packet;
    }

    // Same framing as 1-DDC at the header level — the layouts differ only in
    // the payload below the USB header, and zero payload decodes fine.
    private static byte[] BuildValid4DdcEp6(uint seq) => BuildValid1DdcEp6(seq);
    private static byte[] BuildValid2DdcEp6(uint seq) => BuildValid1DdcEp6(seq);

    private static byte[] BuildGarbageDatagram()
    {
        // Correct EP6 length, broken Metis magic: arrives, never parses —
        // the #1302 sync-shifted-stream shape as seen by the host.
        return new byte[PacketParser.PacketLength];
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail($"timed out waiting for: {what}");
    }

    private static async Task<(Protocol1Client client, IPEndPoint clientEp)> ConnectAndStartAsync(
        FakeRadio radio, Protocol1Client client, CancellationToken ct)
    {
        await client.ConnectAsync(radio.LocalEp, ct);
        await client.StartAsync(
            new StreamConfig(HpsdrSampleRate.Rate48k, PreampOn: false, Atten: HpsdrAtten.Zero), ct);
        var clientEp = await radio.ClientEp.WaitAsync(TimeSpan.FromSeconds(5), ct);
        return (client, clientEp);
    }

    // ------------------------------------------------------------------
    // F1: arm on a live C10 stream = stop ×3 → drain → pre-announce the new
    // count while stopped → start. Never a live Config numRx flip.
    // ------------------------------------------------------------------
    [Fact]
    public async Task ArmOnLiveC10_StopsDrainsPreannouncesThenStarts()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client { PsTransitionDrainMs = 50 };
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        int stalls = 0;
        client.PsFeedbackStalled += () => Interlocked.Increment(ref stalls);

        var (_, clientEp) = await ConnectAndStartAsync(radio, client, cts.Token);

        // Feed valid 1-DDC packets so the start handshake completes and the
        // RX loop is demonstrably parsing the pre-arm format.
        using var feeder = new CancellationTokenSource();
        uint preSeq = 1000;
        var feedTask = Task.Run(async () =>
        {
            while (!feeder.IsCancellationRequested)
            {
                radio.Socket.SendTo(BuildValid1DdcEp6(preSeq++), clientEp);
                await Task.Delay(40);
            }
        });
        await WaitUntilAsync(() => client.TotalFrames > 0, TimeSpan.FromSeconds(5), "pre-arm EP6 parsed");

        feeder.Cancel();
        await feedTask;
        int mark = radio.Count;

        await client.SetPsEnabledAsync(true, cts.Token);
        Assert.True(client.PsEnabled);

        // Give the async start-handshake sender a beat to emit the start.
        await WaitUntilAsync(
            () => radio.Snapshot().Skip(mark).Any(p => p.Kind == PacketKind.Start),
            TimeSpan.FromSeconds(5), "start command after transition");

        var window = radio.Snapshot().Skip(mark).ToList();
        int stops = window.Count(p => p.Kind == PacketKind.Stop);
        Assert.True(stops >= 3, $"expected ≥3 stop commands (all platforms), saw {stops}");

        int firstStop = window.FindIndex(p => p.Kind == PacketKind.Stop);
        int preAnnounce = window.FindIndex(p =>
            p.Kind == PacketKind.Ep2Data && ConfigNumRxMinusOne(p.Raw).Contains(3));
        int firstStart = window.FindIndex(p => p.Kind == PacketKind.Start);
        Assert.True(firstStop >= 0, "no stop observed");
        Assert.True(preAnnounce > firstStop,
            $"pre-announce Config numRx=3 must follow the stop (stop@{firstStop}, preannounce@{preAnnounce})");
        Assert.True(firstStart > preAnnounce,
            $"start must follow the pre-announce (preannounce@{preAnnounce}, start@{firstStart})");
        // Once the NEW count has been announced (stream stopped), nothing may
        // re-announce the OLD count before start — that would re-create the
        // live-flip this fix forbids.
        Assert.DoesNotContain(
            window.Skip(preAnnounce).Take(firstStart - preAnnounce).Where(p => p.Kind == PacketKind.Ep2Data),
            p => ConfigNumRxMinusOne(p.Raw).Contains(0));

        // Parser-state reset: post-arm sequence numbering restarts HIGHER than
        // the pre-arm counter without ever counting a bogus gap.
        for (uint s = 2000; s < 2005; s++)
            radio.Socket.SendTo(BuildValid4DdcEp6(s), clientEp);
        await WaitUntilAsync(() => client.PsPairedPacketCount >= 3, TimeSpan.FromSeconds(5), "post-arm 4-DDC parsed");
        Assert.Equal(0, client.DroppedFrames);
        // The transition window itself (drain, discard, tick re-seed) must
        // never trip the F4 stall watchdog.
        Assert.Equal(0, Volatile.Read(ref stalls));

        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ArmOnLiveHermesII_StopsDrainsPreannouncesTwoDdcThenStarts()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client { PsTransitionDrainMs = 50 };
        client.SetBoardKind(HpsdrBoardKind.HermesII);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        int stalls = 0;
        client.PsFeedbackStalled += () => Interlocked.Increment(ref stalls);

        var (_, clientEp) = await ConnectAndStartAsync(radio, client, cts.Token);

        using var feeder = new CancellationTokenSource();
        uint preSeq = 1000;
        var feedTask = Task.Run(async () =>
        {
            while (!feeder.IsCancellationRequested)
            {
                radio.Socket.SendTo(BuildValid1DdcEp6(preSeq++), clientEp);
                await Task.Delay(40);
            }
        });
        await WaitUntilAsync(() => client.TotalFrames > 0, TimeSpan.FromSeconds(5), "pre-arm EP6 parsed");

        feeder.Cancel();
        await feedTask;
        int mark = radio.Count;

        await client.SetPsEnabledAsync(true, cts.Token);
        Assert.True(client.PsEnabled);

        await WaitUntilAsync(
            () => radio.Snapshot().Skip(mark).Any(p => p.Kind == PacketKind.Start),
            TimeSpan.FromSeconds(5), "start command after transition");

        var window = radio.Snapshot().Skip(mark).ToList();
        int firstStop = window.FindIndex(p => p.Kind == PacketKind.Stop);
        int preAnnounce = window.FindIndex(p =>
            p.Kind == PacketKind.Ep2Data && ConfigNumRxMinusOne(p.Raw).Contains(1));
        int firstStart = window.FindIndex(p => p.Kind == PacketKind.Start);
        Assert.True(firstStop >= 0, "no stop observed");
        Assert.True(preAnnounce > firstStop,
            $"numRx=1 pre-announce must follow the stop (stop@{firstStop}, preannounce@{preAnnounce})");
        Assert.True(firstStart > preAnnounce,
            $"start must follow the pre-announce (preannounce@{preAnnounce}, start@{firstStart})");
        Assert.DoesNotContain(
            window.Skip(preAnnounce).Take(firstStart - preAnnounce).Where(p => p.Kind == PacketKind.Ep2Data),
            p => ConfigNumRxMinusOne(p.Raw).Contains(0));

        for (uint s = 2000; s < 2005; s++)
            radio.Socket.SendTo(BuildValid2DdcEp6(s), clientEp);
        await WaitUntilAsync(() => client.PsPairedPacketCount >= 3, TimeSpan.FromSeconds(5), "post-arm 2-DDC parsed");
        Assert.Equal(0, client.DroppedFrames);
        Assert.Equal(0, Volatile.Read(ref stalls));

        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // F1: disarm goes through the same transition, announcing numRx=0.
    // ------------------------------------------------------------------
    [Fact]
    public async Task DisarmOnLiveC10_TransitionsBackToSingleRx()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client { PsTransitionDrainMs = 50 };
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        client.SetPsEnabled(true); // seeded before start — connect-while-armed
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        int stalls = 0;
        client.PsFeedbackStalled += () => Interlocked.Increment(ref stalls);

        var (_, clientEp) = await ConnectAndStartAsync(radio, client, cts.Token);
        radio.Socket.SendTo(BuildValid4DdcEp6(1), clientEp);
        await WaitUntilAsync(() => client.TotalFrames > 0, TimeSpan.FromSeconds(5), "armed EP6 parsed");

        int mark = radio.Count;
        await client.SetPsEnabledAsync(false, cts.Token);
        Assert.False(client.PsEnabled);

        await WaitUntilAsync(
            () => radio.Snapshot().Skip(mark).Any(p => p.Kind == PacketKind.Start),
            TimeSpan.FromSeconds(5), "start command after disarm transition");

        var window = radio.Snapshot().Skip(mark).ToList();
        Assert.True(window.Count(p => p.Kind == PacketKind.Stop) >= 3);
        int firstStop = window.FindIndex(p => p.Kind == PacketKind.Stop);
        int preAnnounce = window.FindIndex(p =>
            p.Kind == PacketKind.Ep2Data && ConfigNumRxMinusOne(p.Raw).Contains(0));
        int firstStart = window.FindIndex(p => p.Kind == PacketKind.Start);
        Assert.True(firstStop >= 0, "no stop observed");
        Assert.True(preAnnounce >= 0, "no numRx=0 pre-announce observed");
        // The 4→1 direction is the same mid-frame length change (num_loops
        // 18→62) as the arm direction — announcing the new count on a still-
        // running stream would sync-shift EP6 just the same. The stop MUST
        // precede the pre-announce.
        Assert.True(preAnnounce > firstStop,
            $"numRx=0 pre-announce must follow the stop (stop@{firstStop}, preannounce@{preAnnounce})");
        Assert.True(firstStart > preAnnounce, "start must follow the numRx=0 pre-announce");
        // And nothing may re-announce the OLD (4-DDC) count between the
        // pre-announce and start — mirror of the arm-direction check.
        Assert.DoesNotContain(
            window.Skip(preAnnounce).Take(firstStart - preAnnounce).Where(p => p.Kind == PacketKind.Ep2Data),
            p => ConfigNumRxMinusOne(p.Raw).Contains(3));
        // The transition window itself must never trip the F4 stall watchdog.
        Assert.Equal(0, Volatile.Read(ref stalls));

        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // F2: SetPsEnabledAsync is idempotent — the post-connect resync after a
    // connect-while-armed must NOT restart the stream.
    // ------------------------------------------------------------------
    [Fact]
    public async Task SetPsEnabledAsync_AlreadyInMode_NoWireTraffic()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client { PsTransitionDrainMs = 50 };
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        client.SetPsEnabled(true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (_, clientEp) = await ConnectAndStartAsync(radio, client, cts.Token);
        radio.Socket.SendTo(BuildValid4DdcEp6(1), clientEp);
        await WaitUntilAsync(() => client.TotalFrames > 0, TimeSpan.FromSeconds(5), "EP6 parsed");

        int mark = radio.Count;
        await client.SetPsEnabledAsync(true, cts.Token); // resync no-op
        await Task.Delay(150, cts.Token);

        var window = radio.Snapshot().Skip(mark).ToList();
        Assert.DoesNotContain(window, p => p.Kind == PacketKind.Stop);
        Assert.True(client.PsEnabled);

        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // HL2 keeps its shipped behaviour: the async setter degrades to a flag
    // store — no stop/restart on any non-C10 board.
    // ------------------------------------------------------------------
    [Fact]
    public async Task SetPsEnabledAsync_Hl2_FlagOnly_NoRestart()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client();
        // Default board is HermesLite2.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (_, clientEp) = await ConnectAndStartAsync(radio, client, cts.Token);
        radio.Socket.SendTo(BuildValid1DdcEp6(1), clientEp);
        await WaitUntilAsync(() => client.TotalFrames > 0, TimeSpan.FromSeconds(5), "EP6 parsed");

        int mark = radio.Count;
        await client.SetPsEnabledAsync(true, cts.Token);
        Assert.True(client.PsEnabled);
        await Task.Delay(150, cts.Token);
        Assert.DoesNotContain(radio.Snapshot().Skip(mark), p => p.Kind == PacketKind.Stop);

        await client.SetPsEnabledAsync(false, cts.Token);
        Assert.False(client.PsEnabled);
        Assert.DoesNotContain(radio.Snapshot().Skip(mark), p => p.Kind == PacketKind.Stop);

        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // F2: connect-with-PS-already-armed starts DIRECTLY in 4-DDC — the
    // connect-hygiene stop (stale-run guard) and the Config numRx=3
    // pre-announce both precede the very first start command, and no
    // restart transition (stop AFTER start) ever happens.
    // ------------------------------------------------------------------
    [Fact]
    public async Task ConnectWhileArmed_C10_PreannouncesFourDdcBeforeStart_NoTransition()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client { PsTransitionDrainMs = 50 };
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        client.SetPsEnabled(true); // RadioService seeds this before StartAsync
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (_, clientEp) = await ConnectAndStartAsync(radio, client, cts.Token);
        // Radio replies in 4-DDC immediately (its IF_last_chan was corrected
        // by the pre-announce while run=0).
        radio.Socket.SendTo(BuildValid4DdcEp6(0), clientEp);
        radio.Socket.SendTo(BuildValid4DdcEp6(1), clientEp);
        await WaitUntilAsync(() => client.PsPairedPacketCount >= 2, TimeSpan.FromSeconds(5),
            "4-DDC packets parsed from the first packet");

        var all = radio.Snapshot();
        int hygieneStop = all.FindIndex(p => p.Kind == PacketKind.Stop);
        int preAnnounce = all.FindIndex(p =>
            p.Kind == PacketKind.Ep2Data && ConfigNumRxMinusOne(p.Raw).Contains(3));
        int firstStart = all.FindIndex(p => p.Kind == PacketKind.Start);
        Assert.True(preAnnounce >= 0, "no numRx=3 pre-announce before start");
        // Stale-run guard: the connect-hygiene stop must precede the
        // pre-announce so a radio left streaming by a crashed host is never
        // count-flipped live.
        Assert.True(hygieneStop >= 0 && hygieneStop < preAnnounce,
            $"connect-hygiene stop must precede the pre-announce (stop@{hygieneStop}, preannounce@{preAnnounce})");
        Assert.True(firstStart > preAnnounce,
            $"pre-announce must precede the first start (preannounce@{preAnnounce}, start@{firstStart})");
        // No restart transition on a connect-while-armed: never a stop after
        // the stream started.
        int lastStop = all.FindLastIndex(p => p.Kind == PacketKind.Stop);
        Assert.True(lastStop < firstStart,
            $"no stop may follow the start — that would be a live transition (start@{firstStart}, stop@{lastStop})");

        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConnectWhileArmed_HermesII_PreannouncesTwoDdcBeforeStart_NoTransition()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client { PsTransitionDrainMs = 50 };
        client.SetBoardKind(HpsdrBoardKind.HermesII);
        client.SetPsEnabled(true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (_, clientEp) = await ConnectAndStartAsync(radio, client, cts.Token);
        radio.Socket.SendTo(BuildValid2DdcEp6(0), clientEp);
        radio.Socket.SendTo(BuildValid2DdcEp6(1), clientEp);
        await WaitUntilAsync(() => client.PsPairedPacketCount >= 2, TimeSpan.FromSeconds(5),
            "2-DDC packets parsed from the first packet");

        var all = radio.Snapshot();
        int hygieneStop = all.FindIndex(p => p.Kind == PacketKind.Stop);
        int preAnnounce = all.FindIndex(p =>
            p.Kind == PacketKind.Ep2Data && ConfigNumRxMinusOne(p.Raw).Contains(1));
        int firstStart = all.FindIndex(p => p.Kind == PacketKind.Start);
        Assert.True(preAnnounce >= 0, "no numRx=1 pre-announce before start");
        Assert.True(hygieneStop >= 0 && hygieneStop < preAnnounce,
            $"connect-hygiene stop must precede the pre-announce (stop@{hygieneStop}, preannounce@{preAnnounce})");
        Assert.True(firstStart > preAnnounce,
            $"pre-announce must precede the first start (preannounce@{preAnnounce}, start@{firstStart})");
        int lastStop = all.FindLastIndex(p => p.Kind == PacketKind.Stop);
        Assert.True(lastStop < firstStart,
            $"no stop may follow the start — that would be a live transition (start@{firstStart}, stop@{lastStop})");

        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // Non-C10 boards get no pre-announce at connect — wire identical.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Connect_Hl2_SendsNoPreannounceFrames()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (_, clientEp) = await ConnectAndStartAsync(radio, client, cts.Token);
        radio.Socket.SendTo(BuildValid1DdcEp6(1), clientEp);
        await WaitUntilAsync(() => client.TotalFrames > 0, TimeSpan.FromSeconds(5), "EP6 parsed");

        int firstStart = radio.Snapshot().FindIndex(p => p.Kind == PacketKind.Start);
        Assert.True(firstStart >= 0);
        Assert.DoesNotContain(radio.Snapshot().Take(firstStart), p => p.Kind == PacketKind.Ep2Data);
        // HL2 byte-identity: no C10-style connect-hygiene stop either.
        Assert.DoesNotContain(radio.Snapshot().Take(firstStart), p => p.Kind == PacketKind.Stop);

        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // Connect hygiene (stale-run guard): every C10 connect sends stop ×3 and
    // drains BEFORE the pre-announce and start, so a radio left streaming by
    // a crashed host (run=1, stale IF_last_chan) is realigned instead of
    // live-flipped by our first Config frame.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Connect_C10_HygieneStopPrecedesPreannounceAndStart()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client { PsTransitionDrainMs = 50 };
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (_, clientEp) = await ConnectAndStartAsync(radio, client, cts.Token);
        radio.Socket.SendTo(BuildValid1DdcEp6(1), clientEp);
        await WaitUntilAsync(() => client.TotalFrames > 0, TimeSpan.FromSeconds(5), "EP6 parsed");

        var all = radio.Snapshot();
        int firstStop = all.FindIndex(p => p.Kind == PacketKind.Stop);
        int preAnnounce = all.FindIndex(p =>
            p.Kind == PacketKind.Ep2Data && ConfigNumRxMinusOne(p.Raw).Contains(0));
        int firstStart = all.FindIndex(p => p.Kind == PacketKind.Start);
        Assert.True(firstStop >= 0, "no connect-hygiene stop observed");
        Assert.True(firstStart >= 0, "no start observed");
        Assert.True(all.Take(firstStart).Count(p => p.Kind == PacketKind.Stop) >= 3,
            "connect-hygiene stop must be sent 3× before start");
        Assert.True(preAnnounce > firstStop,
            $"pre-announce must follow the hygiene stop (stop@{firstStop}, preannounce@{preAnnounce})");
        Assert.True(firstStart > preAnnounce,
            $"start must follow the pre-announce (preannounce@{preAnnounce}, start@{firstStart})");

        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // F4 negative: a FULLY dead radio (zero datagrams) is the RX-timeout
    // teardown's job — the stall watchdog must stay silent. Pins the
    // datagram-freshness term of the stall condition AND the production
    // invariant PsStallTimeoutMs > datagram window.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Watchdog_QuietOnDeadRadio_TimeoutTeardownOwnsFailure()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client
        {
            PsTransitionDrainMs = 50,
            StartHandshakeTimeoutMs = 100,
            StartHandshakeAttempts = 3,
            // PsStallTimeoutMs deliberately stays at its production default:
            // with zero datagrams both watchdog ticks age together, so the
            // stall condition (ok-age ≥ 2000 && datagram-age ≤ 1000) can
            // never be true — that relationship is what this test pins.
        };
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        client.SetPsEnabled(true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        int stalls = 0;
        client.PsFeedbackStalled += () => Interlocked.Increment(ref stalls);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => disconnected.TrySetResult();

        await ConnectAndStartAsync(radio, client, cts.Token);
        // Radio sends NOTHING. Existing teardown must fire, watchdog must not.
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.Equal(0, Volatile.Read(ref stalls));

        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // F4: after a stall trip + disarm/re-arm (the production auto-disarm →
    // operator-re-arm cycle), the latch must have reset so a SECOND
    // misframed period fires again — a latch surviving the transition would
    // silently disable the guard for the rest of the session.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Watchdog_FiresAgainAfterDisarmRearm()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client
        {
            PsTransitionDrainMs = 50,
            PsStallTimeoutMs = 300,
            StartHandshakeTimeoutMs = 100,
        };
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        client.SetPsEnabled(true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        int stalls = 0;
        client.PsFeedbackStalled += () => Interlocked.Increment(ref stalls);

        var (_, clientEp) = await ConnectAndStartAsync(radio, client, cts.Token);

        using var feeder = new CancellationTokenSource();
        var feedTask = Task.Run(async () =>
        {
            while (!feeder.IsCancellationRequested)
            {
                radio.Socket.SendTo(BuildGarbageDatagram(), clientEp);
                await Task.Delay(20);
            }
        });

        await WaitUntilAsync(() => Volatile.Read(ref stalls) >= 1, TimeSpan.FromSeconds(5), "first stall");

        await client.SetPsEnabledAsync(false, cts.Token);
        await client.SetPsEnabledAsync(true, cts.Token);

        await WaitUntilAsync(() => Volatile.Read(ref stalls) >= 2, TimeSpan.FromSeconds(5),
            "second stall after disarm/re-arm");

        feeder.Cancel();
        await feedTask;
        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // Post-transition liveness: EP2 must RESUME after the arm transition
    // (un-paused TX loop, RX-paced), carrying the NEW count in its rotation
    // Config frames. A bug leaving _txPaused latched would starve the radio
    // of Config/freq frames and pass every ordering-only assertion.
    // ------------------------------------------------------------------
    [Fact]
    public async Task ArmTransition_Ep2ResumesCarryingNewCount()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client { PsTransitionDrainMs = 50 };
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (_, clientEp) = await ConnectAndStartAsync(radio, client, cts.Token);
        radio.Socket.SendTo(BuildValid1DdcEp6(1), clientEp);
        await WaitUntilAsync(() => client.TotalFrames > 0, TimeSpan.FromSeconds(5), "pre-arm EP6 parsed");

        int mark = radio.Count;
        await client.SetPsEnabledAsync(true, cts.Token);

        // Stream 4-DDC EP6 — RX pacing releases the TX loop, which must have
        // been un-paused by the transition.
        using var feeder = new CancellationTokenSource();
        uint seq = 0;
        var feedTask = Task.Run(async () =>
        {
            while (!feeder.IsCancellationRequested)
            {
                radio.Socket.SendTo(BuildValid4DdcEp6(seq++), clientEp);
                await Task.Delay(5);
            }
        });

        await WaitUntilAsync(() =>
        {
            var snap = radio.Snapshot();
            int firstStart = snap.FindIndex(mark, p => p.Kind == PacketKind.Start);
            if (firstStart < 0) return false;
            return snap.Skip(firstStart + 1).Any(p =>
                p.Kind == PacketKind.Ep2Data && ConfigNumRxMinusOne(p.Raw).Contains(3));
        }, TimeSpan.FromSeconds(10), "EP2 resumed with numRx=3 after the transition");

        feeder.Cancel();
        await feedTask;
        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // Teardown vs transition race (#1302 audit): StopAsync during an
    // in-flight transition must leave the radio STOPPED — the transition may
    // not send start into a session being torn down (that orphans a ~5 k
    // pkt/s stream at an abandoned port and poisons the next connect).
    // ------------------------------------------------------------------
    [Fact]
    public async Task StopDuringTransition_RadioIsLeftStopped()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client { PsTransitionDrainMs = 400 };
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (_, clientEp) = await ConnectAndStartAsync(radio, client, cts.Token);
        radio.Socket.SendTo(BuildValid1DdcEp6(1), clientEp);
        await WaitUntilAsync(() => client.TotalFrames > 0, TimeSpan.FromSeconds(5), "EP6 parsed");

        // Kick the arm transition and tear the session down mid-drain.
        var transition = client.SetPsEnabledAsync(true);
        await Task.Delay(120, cts.Token); // inside stop ×3 (~60 ms) + 400 ms drain
        await client.StopAsync(CancellationToken.None);
        await transition; // must complete without faulting

        // Let any stray async sender flush, then require the wire to END
        // stopped: no start after the final stop.
        await Task.Delay(300, cts.Token);
        var all = radio.Snapshot();
        int lastStop = all.FindLastIndex(p => p.Kind == PacketKind.Stop);
        int lastStart = all.FindLastIndex(p => p.Kind == PacketKind.Start);
        Assert.True(lastStop >= 0, "no stop observed");
        Assert.True(lastStart < lastStop,
            $"radio must be left stopped — start@{lastStart} after stop@{lastStop} would orphan the stream (#1302)");

        await client.DisconnectAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // F3: start-handshake retry — no valid EP6 ⇒ re-send start, bounded, then
    // the existing consecutive-timeout teardown fires Disconnected.
    // ------------------------------------------------------------------
    [Fact]
    public async Task StartHandshake_RetriesStart_ThenExistingTeardownFires()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client
        {
            StartHandshakeTimeoutMs = 150,
            StartHandshakeAttempts = 3,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => disconnected.TrySetResult();

        await ConnectAndStartAsync(radio, client, cts.Token);
        // Radio never answers. Expect 3 SendStartStop(start) calls total
        // (initial + 2 retries); each call emits 3 datagrams on macOS, 1
        // elsewhere.
        int perSend = OperatingSystem.IsMacOS() ? 3 : 1;
        await WaitUntilAsync(
            () => radio.Snapshot().Count(p => p.Kind == PacketKind.Start) >= 3 * perSend,
            TimeSpan.FromSeconds(5), "start re-sends");

        // Existing teardown semantics: once the handshake gives up, the
        // consecutive-timeout give-up fires Disconnected as before.
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // F4: feedback watchdog — armed C10, datagrams flowing, zero parses for
    // the stall window ⇒ PsFeedbackStalled fires exactly once (latched).
    // ------------------------------------------------------------------
    [Fact]
    public async Task Watchdog_FiresOnce_WhenDatagramsArriveButNothingParses()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client
        {
            PsStallTimeoutMs = 300,
            StartHandshakeTimeoutMs = 100,
        };
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        client.SetPsEnabled(true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        int stalls = 0;
        client.PsFeedbackStalled += () => Interlocked.Increment(ref stalls);

        var (_, clientEp) = await ConnectAndStartAsync(radio, client, cts.Token);

        // Flood garbage: datagrams arrive at rate, none parse — the #1302
        // permanently-sync-shifted stream as seen from the host.
        using var feeder = new CancellationTokenSource();
        var feedTask = Task.Run(async () =>
        {
            while (!feeder.IsCancellationRequested)
            {
                radio.Socket.SendTo(BuildGarbageDatagram(), clientEp);
                await Task.Delay(20);
            }
        });

        await WaitUntilAsync(() => Volatile.Read(ref stalls) >= 1, TimeSpan.FromSeconds(5), "watchdog fired");
        Assert.True(client.Ps4DdcSyncFailCount > 0, "sync-fail counter must be visible");

        // Latched: keep flooding well past another stall window — no re-fire.
        await Task.Delay(700, cts.Token);
        Assert.Equal(1, Volatile.Read(ref stalls));

        feeder.Cancel();
        await feedTask;
        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Watchdog_HermesII_FiresOnce_WhenDatagramsArriveButNothingParses()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client
        {
            PsStallTimeoutMs = 300,
            StartHandshakeTimeoutMs = 100,
        };
        client.SetBoardKind(HpsdrBoardKind.HermesII);
        client.SetPsEnabled(true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        int stalls = 0;
        client.PsFeedbackStalled += () => Interlocked.Increment(ref stalls);

        var (_, clientEp) = await ConnectAndStartAsync(radio, client, cts.Token);

        using var feeder = new CancellationTokenSource();
        var feedTask = Task.Run(async () =>
        {
            while (!feeder.IsCancellationRequested)
            {
                radio.Socket.SendTo(BuildGarbageDatagram(), clientEp);
                await Task.Delay(20);
            }
        });

        await WaitUntilAsync(() => Volatile.Read(ref stalls) >= 1, TimeSpan.FromSeconds(5), "watchdog fired");
        Assert.True(client.Ps4DdcSyncFailCount > 0, "sync-fail counter must be visible");

        await Task.Delay(700, cts.Token);
        Assert.Equal(1, Volatile.Read(ref stalls));

        feeder.Cancel();
        await feedTask;
        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // F4 negative: a healthy armed stream never trips the watchdog.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Watchdog_QuietOnHealthyArmedStream()
    {
        using var radio = new FakeRadio();
        using var client = new Protocol1Client { PsStallTimeoutMs = 300 };
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        client.SetPsEnabled(true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        int stalls = 0;
        client.PsFeedbackStalled += () => Interlocked.Increment(ref stalls);

        var (_, clientEp) = await ConnectAndStartAsync(radio, client, cts.Token);

        uint seq = 0;
        for (int i = 0; i < 40; i++)
        {
            radio.Socket.SendTo(BuildValid4DdcEp6(seq++), clientEp);
            await Task.Delay(25, cts.Token);
        }
        Assert.Equal(0, Volatile.Read(ref stalls));

        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // Pre-start SetPsEnabledAsync just seeds the flag (no socket yet).
    // ------------------------------------------------------------------
    [Fact]
    public async Task SetPsEnabledAsync_BeforeStart_SeedsFlagWithoutTransition()
    {
        using var client = new Protocol1Client();
        client.SetBoardKind(HpsdrBoardKind.HermesC10);
        await client.SetPsEnabledAsync(true);
        Assert.True(client.PsEnabled);
        await client.SetPsEnabledAsync(false);
        Assert.False(client.PsEnabled);
    }
}
