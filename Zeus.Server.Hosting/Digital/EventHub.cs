// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — SSE hub for GET /events.
//
// Replaces the legacy 0x38/0x39/0x3A WS frames (now RESERVED in
// realtime/ws-client.ts). Payloads are byte-identical to those frames, so store
// ingest is unchanged — only the transport moved.
//
// Three named events, consumed by api/digital-plugin.ts:
//     ft8decode   → Ft8DecodeBatch
//     txstatus    → Ft8TxStatus
//     wsprspot    → WSPR spot
//
// The client re-hydrates (/ft8, /ft8/tx, /wspr) on EVERY open including
// auto-reconnects, because SSE replays nothing across a gap. So this hub is
// deliberately fire-and-forget: no replay buffer, no per-client state.

using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Zeus.Server.Hosting.Digital;

public sealed class EventHub
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly List<Channel<string>> _clients = new();
    private readonly object _sync = new();

    /// <summary>Register a subscriber. Dispose the returned token to unsubscribe.</summary>
    public (ChannelReader<string> Reader, IDisposable Lease) Subscribe()
    {
        // Bounded + DropOldest: a wedged client must never stall the decoder or
        // grow unboundedly. Losing frames for a stuck reader is correct — the
        // client re-hydrates on reconnect anyway.
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_sync) _clients.Add(ch);
        return (ch.Reader, new Lease(this, ch));
    }

    public void PublishFt8Decode(Ft8DecodeBatch batch) => Publish("ft8decode", batch);
    public void PublishTxStatus(Ft8TxStatus status) => Publish("txstatus", status);
    public void PublishWsprSpot(object spot) => Publish("wsprspot", spot);

    private void Publish<T>(string eventName, T payload)
    {
        string data;
        try { data = JsonSerializer.Serialize(payload, Json); }
        catch { return; }   // never let a serialization fault reach the audio path

        // SSE framing: "event: <name>\ndata: <json>\n\n"
        var frame = new StringBuilder()
            .Append("event: ").Append(eventName).Append('\n')
            .Append("data: ").Append(data).Append("\n\n")
            .ToString();

        lock (_sync)
        {
            foreach (var c in _clients) c.Writer.TryWrite(frame);
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly EventHub _hub;
        private readonly Channel<string> _ch;
        public Lease(EventHub hub, Channel<string> ch) { _hub = hub; _ch = ch; }
        public void Dispose()
        {
            lock (_hub._sync) _hub._clients.Remove(_ch);
            _ch.Writer.TryComplete();
        }
    }
}
