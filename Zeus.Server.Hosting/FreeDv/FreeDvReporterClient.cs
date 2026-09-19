// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// FreeDvReporterClient — the minimal socket.io v4 (Engine.IO 4) client the
// FreeDV Reporter needs, over System.Net.WebSockets. Hand-rolled on purpose:
// the reporter uses one namespace, text frames only and no acks, so the whole
// protocol is a handful of packet prefixes and a NuGet dependency would buy
// nothing. Mirrors freedv-gui 2.1.0 src/util/SocketIoClient.cpp:
//
//   URL   ws://<host>/socket.io/?EIO=4&transport=websocket
//   "0{…}"        Engine.IO open (pingInterval / pingTimeout)  → send "40{auth}"
//   "2" / "3"     Engine.IO ping from the server / our pong
//   "40{sid}"     socket.io namespace connected
//   "42[name,x]"  socket.io event (both directions)
//   "44{…}"       namespace connect error → close
//   "41" / "1"    namespace / transport close

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Zeus.Server.Hosting.FreeDv;

internal sealed class FreeDvReporterClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>socket.io session id from the "40" packet — how the server names us in events.</summary>
    public string? Sid { get; private set; }

    /// <summary>Raised on the receive loop when the namespace connects ("40").</summary>
    public event Action? Connected;

    /// <summary>Raised on the receive loop for every "42" event (args is default when absent).</summary>
    public event Action<string, JsonElement>? EventReceived;

    /// <summary>
    /// Connects, performs the Engine.IO / socket.io handshake with
    /// <paramref name="authJson"/>, then pumps messages until the server
    /// closes, the ping watchdog fires or <paramref name="ct"/> is cancelled.
    /// Returns normally on a clean close; throws on transport errors, and
    /// OperationCanceledException when the ping watchdog or <paramref name="ct"/> fires.
    /// </summary>
    public async Task RunAsync(Uri uri, string authJson, CancellationToken ct)
    {
        await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        while (!watchdog.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(buffer, watchdog.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);

            if (r.MessageType != WebSocketMessageType.Text) continue;
            string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);

            switch (Classify(text))
            {
                case Packet.Open:
                    // The server pings every pingInterval; give up if nothing
                    // arrives within interval + timeout (freedv-gui does the same).
                    if (TryReadPingWindow(text.AsSpan(1), out var window))
                        watchdog.CancelAfter(window);
                    await SendRawAsync("40" + authJson, ct).ConfigureAwait(false);
                    break;
                case Packet.Ping:
                    if (_pingWindow is { } w) watchdog.CancelAfter(w);   // re-arm
                    await SendRawAsync("3", ct).ConfigureAwait(false);
                    break;
                case Packet.NamespaceConnect:
                    Sid = ReadSid(text.AsSpan(2));
                    Connected?.Invoke();
                    break;
                case Packet.Event:
                    if (TryParseEvent(text.AsSpan(2), out var name, out var args))
                        EventReceived?.Invoke(name, args);
                    break;
                case Packet.ConnectError:
                case Packet.Close:
                    return;
            }
        }
    }

    // pingInterval + pingTimeout from the open packet; re-armed on every ping.
    private TimeSpan? _pingWindow;

    private bool TryReadPingWindow(ReadOnlySpan<char> json, out TimeSpan window)
    {
        window = default;
        try
        {
            using var doc = JsonDocument.Parse(json.ToString());
            var root = doc.RootElement;
            int interval = root.TryGetProperty("pingInterval", out var pi) ? pi.GetInt32() : 25000;
            int timeout = root.TryGetProperty("pingTimeout", out var pt) ? pt.GetInt32() : 20000;
            window = TimeSpan.FromMilliseconds(interval + timeout);
            _pingWindow = window;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Emits a socket.io event: 42["name"] or 42["name",data].</summary>
    public Task EmitAsync(string name, object? data, CancellationToken ct)
    {
        string payload = data is null
            ? JsonSerializer.Serialize(new object[] { name })
            : JsonSerializer.Serialize(new[] { name, data });
        return SendRawAsync("42" + payload, ct);
    }

    private async Task SendRawAsync(string text, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token).ConfigureAwait(false);
            }
        }
        catch { /* best effort */ }
        _ws.Dispose();
        _sendLock.Dispose();
    }

    // ---- framing (static, unit-tested) --------------------------------------

    internal enum Packet { Unknown, Open, Close, Ping, NamespaceConnect, Event, ConnectError }

    internal static Packet Classify(string text)
    {
        if (text.Length == 0) return Packet.Unknown;
        switch (text[0])
        {
            case '0': return Packet.Open;
            case '1': return Packet.Close;
            case '2': return Packet.Ping;
            case '4':
                if (text.Length < 2) return Packet.Unknown;
                return text[1] switch
                {
                    '0' => Packet.NamespaceConnect,
                    '1' => Packet.Close,
                    '2' => Packet.Event,
                    '4' => Packet.ConnectError,
                    _ => Packet.Unknown,
                };
            default: return Packet.Unknown;
        }
    }

    /// <summary>Parses the JSON array after "42" into an event name and its first argument.</summary>
    internal static bool TryParseEvent(ReadOnlySpan<char> json, out string name, out JsonElement args)
    {
        name = "";
        args = default;
        try
        {
            using var doc = JsonDocument.Parse(json.ToString());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return false;
            if (root[0].ValueKind != JsonValueKind.String) return false;
            name = root[0].GetString()!;
            if (root.GetArrayLength() > 1) args = root[1].Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static string? ReadSid(ReadOnlySpan<char> json)
    {
        if (json.IsEmpty) return null;
        try
        {
            using var doc = JsonDocument.Parse(json.ToString());
            return doc.RootElement.TryGetProperty("sid", out var sid) && sid.ValueKind == JsonValueKind.String
                ? sid.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }
}
