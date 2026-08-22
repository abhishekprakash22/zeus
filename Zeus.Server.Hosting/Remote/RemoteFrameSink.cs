using System.Threading.Channels;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// Bridges <see cref="Zeus.Server.StreamingHub"/>'s broadcast frames onto a
/// remote session's WebRTC frames channel. Registered only after the SPAKE2+
/// password unlocks (ADR-0008), and the actual send is gated again by the
/// session's egress guard, so a frame can never leave a locked session.
///
/// A bounded drop-oldest queue drained on a background task keeps the DSP thread
/// (which calls <see cref="TryEnqueue"/> via the hub fan-out) from ever blocking
/// on the data channel — mirroring the WebSocket ClientSession's backpressure.
/// </summary>
internal sealed class RemoteFrameSink : Zeus.Server.IClientSink, IDisposable
{
    // Small bound: stale spectrum/meter frames are worthless, so drop-oldest.
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    // AUDIO RIDES ALONE. Field find (the last layer of the MON-during-TX
    // onion): audio frames shared the 8-slot lossy queue above with the
    // full unpaced display-frame rate — display pacing happens AFTER
    // dequeue, so the queue carries every DSP display tick. Transmission is
    // the link's busiest moment in both directions (TX spectrum + meters out,
    // mic RTP in), the drain falls behind, and drop-oldest evicts the
    // monitor's audio frames indiscriminately — decimated Opus input, silence
    // in the operator's ear. At unkey the mic stops, the queue unclogs, and
    // the monitor's server-side tail sails through: the field-reported
    // 'split second of MON audio at unkey', mechanism and all. RX-mode audio
    // always survived because reception is the quiet time. Audio now has its
    // own lane; spectrum can never evict it again. Thread safety holds by
    // partition: this drain only ever carries 0x02 frames (Opus encoder +
    // SendAudio), the frames drain only ever touches the data channel — the
    // two paths share no mutable state beyond volatile session guards.
    private readonly Channel<byte[]> _audioQueue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly Func<byte[], bool> _send;
    private readonly CancellationTokenSource _cts = new();

    /// <param name="send">The session's gated send (returns false while locked/closed).</param>
    public RemoteFrameSink(Func<byte[], bool> send)
    {
        _send = send;
        _ = DrainAsync(_queue);
        _ = DrainAsync(_audioQueue);
    }

    public bool WantsDisplay => true;

    public bool TryEnqueue(byte[] payload)
    {
        bool isAudio = payload.Length >= 1
            && payload[0] == (byte)Zeus.Contracts.MsgType.AudioPcm;
        return isAudio
            ? _audioQueue.Writer.TryWrite(payload)
            : _queue.Writer.TryWrite(payload);
    }

    private async Task DrainAsync(Channel<byte[]> queue)
    {
        try
        {
            await foreach (var payload in queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                _send(payload);
        }
        catch (OperationCanceledException) { /* disposed */ }
        catch { /* session torn down underneath us */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        _audioQueue.Writer.TryComplete();
    }
}
