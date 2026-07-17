// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class StreamingHubDisplaySubscriptionTests
{
    private const byte DisplayStreamRequest = 0x22;
    private const byte DisplayStreamPreferred = 2;

    [Fact]
    public async Task DisplayFramesOnlyFanOutToSubscribedClients()
    {
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        using var ws = new ScriptedWebSocket();
        var attach = hub.AttachClientAsync(ws, CancellationToken.None);

        var frame = new DisplayFrame(
            Seq: 1,
            TsUnixMs: 1_700_000_000_000,
            RxId: 0,
            BodyFlags: DisplayBodyFlags.PanValid | DisplayBodyFlags.WfValid,
            Width: 2,
            CenterHz: 14_254_000,
            HzPerPixel: 46.875f,
            PanDb: new float[] { -110f, -90f },
            WfDb: new float[] { -120f, -100f });

        hub.Broadcast(in frame);
        await Task.Delay(50);
        Assert.DoesNotContain(ws.SentFrames, payload => payload[0] == (byte)MsgType.DisplayFrame);

        ws.QueueBinary(new byte[] { DisplayStreamRequest, 1 });
        await WaitUntilAsync(() => hub.DisplaySubscriberCount == 1);

        hub.Broadcast(in frame);
        await WaitUntilAsync(() => ws.SentFrames.Any(payload => payload[0] == (byte)MsgType.DisplayFrame));

        ws.QueueClose();
        await attach.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, hub.DisplaySubscriberCount);
    }

    [Fact]
    public async Task PreferredDisplayClientSuppressesLegacyDisplayFanout()
    {
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        using var legacy = new ScriptedWebSocket();
        using var preferred = new ScriptedWebSocket();
        var legacyAttach = hub.AttachClientAsync(legacy, CancellationToken.None);
        var preferredAttach = hub.AttachClientAsync(preferred, CancellationToken.None);

        var frame = new DisplayFrame(
            Seq: 1,
            TsUnixMs: 1_700_000_000_000,
            RxId: 0,
            BodyFlags: DisplayBodyFlags.PanValid | DisplayBodyFlags.WfValid,
            Width: 2,
            CenterHz: 14_254_000,
            HzPerPixel: 46.875f,
            PanDb: new float[] { -110f, -90f },
            WfDb: new float[] { -120f, -100f });

        legacy.QueueBinary(new byte[] { DisplayStreamRequest, 1 });
        preferred.QueueBinary(new byte[] { DisplayStreamRequest, DisplayStreamPreferred });
        await WaitUntilAsync(() => hub.DisplaySubscriberCount == 2);
        await WaitUntilAsync(() => hub.PreferredDisplaySubscriberCount == 1);

        hub.Broadcast(in frame);
        await WaitUntilAsync(() => preferred.SentFrames.Any(payload => payload[0] == (byte)MsgType.DisplayFrame));
        Assert.DoesNotContain(legacy.SentFrames, payload => payload[0] == (byte)MsgType.DisplayFrame);

        preferred.QueueClose();
        await preferredAttach.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => hub.PreferredDisplaySubscriberCount == 0);

        hub.Broadcast(in frame);
        await WaitUntilAsync(() => legacy.SentFrames.Any(payload => payload[0] == (byte)MsgType.DisplayFrame));

        legacy.QueueClose();
        await legacyAttach.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DisplayStreamRequestTracksEnableAndDisable()
    {
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        using var ws = new ScriptedWebSocket();
        var attach = hub.AttachClientAsync(ws, CancellationToken.None);

        ws.QueueBinary(new byte[] { DisplayStreamRequest, 1 });
        await WaitUntilAsync(() => hub.DisplaySubscriberCount == 1);
        Assert.True(hub.DisplayStreamRequested);

        ws.QueueBinary(new byte[] { DisplayStreamRequest, 0 });
        await WaitUntilAsync(() => hub.DisplaySubscriberCount == 0);
        Assert.False(hub.DisplayStreamRequested);

        ws.QueueClose();
        await attach.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        Assert.True(predicate());
    }

    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Channel<byte[]?> _receives = Channel.CreateUnbounded<byte[]?>();
        private readonly ConcurrentQueue<byte[]> _sent = new();
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;
        private WebSocketState _state = WebSocketState.Open;

        public byte[][] SentFrames => _sent.ToArray();

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void QueueBinary(byte[] payload) => _receives.Writer.TryWrite(payload);

        public void QueueClose() => _receives.Writer.TryWrite(null);

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _receives.Writer.TryComplete();
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            _receives.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _receives.Writer.TryComplete();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            var payload = await _receives.Reader.ReadAsync(cancellationToken);
            if (payload is null)
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            Buffer.BlockCopy(payload, 0, buffer.Array!, buffer.Offset, payload.Length);
            return new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Binary, true);
        }

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            var payload = await _receives.Reader.ReadAsync(cancellationToken);
            if (payload is null)
            {
                _state = WebSocketState.CloseReceived;
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            payload.AsSpan().CopyTo(buffer.Span);
            return new ValueWebSocketReceiveResult(payload.Length, WebSocketMessageType.Binary, true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            var copy = new byte[buffer.Count];
            Buffer.BlockCopy(buffer.Array!, buffer.Offset, copy, 0, buffer.Count);
            _sent.Enqueue(copy);
            return Task.CompletedTask;
        }

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            _sent.Enqueue(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
