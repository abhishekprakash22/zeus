// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Pins the network-egress safety guarantees of the WSJT-X broadcaster:
//   - DISABLED (the default) ⇒ no datagram leaves the box;
//   - ENABLED ⇒ a well-formed type-12 LoggedADIF datagram is emitted;
//   - a send to a dead endpoint never throws (a broken listener must not break
//     the /api/log/entry POST).

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Server;
using Zeus.Server.Wsjtx;

namespace Zeus.Server.Tests;

public sealed class WsjtxUdpBroadcasterTests : IDisposable
{
    private readonly string _prefsDbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-wsjtxbc-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_prefsDbPath)) File.Delete(_prefsDbPath); } catch { }
    }

    private static (WsjtxUdpBroadcaster bc, WsjtxManagementService mgmt, LogEntry entry, FakeLogbookPlugin log)
        Build(WsjtxConfigStore store)
    {
        var mgmt = new WsjtxManagementService(NullLogger<WsjtxManagementService>.Instance, store);
        var log = new FakeLogbookPlugin();
        var bridge = new LogbookPluginBridge(NullLogger<LogbookPluginBridge>.Instance);
        bridge.Attach(log);
        var entry = new LogEntry(
            Id: "qso-1",
            QsoDateTimeUtc: new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            Callsign: "K1ABC",
            Name: null,
            FrequencyMhz: 14.074,
            Band: "20M",
            Mode: "FT8",
            RstSent: "-12",
            RstRcvd: "-07",
            Grid: null,
            Country: null,
            Dxcc: null,
            CqZone: null,
            ItuZone: null,
            State: null,
            Comment: null,
            CreatedUtc: new DateTime(2026, 5, 1, 12, 0, 1, DateTimeKind.Utc));
        log.AdifById[entry.Id] = "<CALL:5>K1ABC <EOR>";
        var bc = new WsjtxUdpBroadcaster(NullLogger<WsjtxUdpBroadcaster>.Instance, mgmt, bridge);
        return (bc, mgmt, entry, log);
    }

    [Fact]
    public async Task Disabled_Broadcast_Sends_Nothing()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        using var store = new WsjtxConfigStore(NullLogger<WsjtxConfigStore>.Instance, _prefsDbPath);
        var (bc, mgmt, entry, _) = Build(store);
        mgmt.SetConfig(new WsjtxRuntimeConfig(Enabled: false, Host: "127.0.0.1", Port: port));

        await bc.BroadcastLoggedQsoAsync(entry);

        // Nothing should arrive on the listener when egress is disabled.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await listener.ReceiveAsync(cts.Token));
    }

    [Fact]
    public async Task Enabled_Broadcast_Emits_LoggedAdif_Datagram()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        using var store = new WsjtxConfigStore(NullLogger<WsjtxConfigStore>.Instance, _prefsDbPath);
        var (bc, mgmt, entry, _) = Build(store);
        mgmt.SetConfig(new WsjtxRuntimeConfig(Enabled: true, Host: "127.0.0.1", Port: port, InstanceId: "Zeus"));

        await bc.BroadcastLoggedQsoAsync(entry);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await listener.ReceiveAsync(cts.Token);

        // Magic header + the call we logged in the ADIF payload.
        Assert.Equal(new byte[] { 0xAD, 0xBC, 0xCB, 0xDA }, result.Buffer[..4]);
        Assert.Contains("K1ABC", System.Text.Encoding.UTF8.GetString(result.Buffer));
    }

    [Fact]
    public async Task SendQsoLogged_Emits_Type5_Alongside_Type12()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        using var store = new WsjtxConfigStore(NullLogger<WsjtxConfigStore>.Instance, _prefsDbPath);
        var (bc, mgmt, entry, _) = Build(store);
        mgmt.SetConfig(new WsjtxRuntimeConfig(
            Enabled: true, Host: "127.0.0.1", Port: port, SendQsoLogged: true));

        await bc.BroadcastLoggedQsoAsync(entry);

        // Two datagrams must arrive: a type-12 LoggedADIF and a type-5 QSOLogged
        // (order not asserted). Both carry the WSJT-X magic.
        var types = new List<uint>();
        for (int i = 0; i < 2; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = await listener.ReceiveAsync(cts.Token);
            Assert.Equal(new byte[] { 0xAD, 0xBC, 0xCB, 0xDA }, result.Buffer[..4]);
            types.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(result.Buffer.AsSpan(8, 4)));
        }
        Assert.Contains(WsjtxMessage.LoggedAdifType, types);
        Assert.Contains(WsjtxMessage.QsoLoggedType, types);
    }

    [Fact]
    public async Task Multicast_Broadcast_Never_Throws()
    {
        using var store = new WsjtxConfigStore(NullLogger<WsjtxConfigStore>.Instance, _prefsDbPath);
        var (bc, mgmt, entry, _) = Build(store);
        mgmt.SetConfig(new WsjtxRuntimeConfig(
            Enabled: true, Transport: "multicast", MulticastGroup: "224.0.0.73", MulticastTtl: 1, Port: 2237));

        // A multicast send must complete without surfacing an exception into the
        // log POST path, regardless of whether anything is joined to the group.
        await bc.BroadcastLoggedQsoAsync(entry);
    }

    [Fact]
    public async Task Enabled_Send_To_Dead_Endpoint_Never_Throws()
    {
        // Reserve a loopback port, then close it so nothing is listening. A UDP
        // send must not surface an exception into the log POST path.
        int deadPort;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            deadPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

        using var store = new WsjtxConfigStore(NullLogger<WsjtxConfigStore>.Instance, _prefsDbPath);
        var (bc, mgmt, entry, _) = Build(store);
        mgmt.SetConfig(new WsjtxRuntimeConfig(Enabled: true, Host: "127.0.0.1", Port: deadPort));

        // Must complete without throwing.
        await bc.BroadcastLoggedQsoAsync(entry);
    }

    private sealed class FakeLogbookPlugin : ILogbookPlugin
    {
        public Dictionary<string, string> AdifById { get; } = new(StringComparer.Ordinal);

        public Task<string> ExportAdifAsync(IEnumerable<string>? ids = null, CancellationToken ct = default)
        {
            var id = ids?.FirstOrDefault();
            return Task.FromResult(id is not null && AdifById.TryGetValue(id, out var adif) ? adif : "");
        }

        public Task<LogbookEntrySnapshot> CreateAsync(LogbookNewEntry entry, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LogbookPage> GetEntriesAsync(int skip, int take, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LogbookEntrySnapshot>> GetByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LogbookWorkedSummary?> GetWorkedSummaryAsync(string callsign, int recentTake, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetDigitalWorkedCallsignsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> UpdateQrzUploadStatusAsync(string id, string qrzLogId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LogbookExportFileResult> ExportAdifToFileAsync(string? directory = null, IEnumerable<string>? ids = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LogbookImportResult> ImportAdifAsync(string adifText, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
