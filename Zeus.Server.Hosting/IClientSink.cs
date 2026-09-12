namespace Zeus.Server;

/// <summary>
/// A consumer of <see cref="StreamingHub"/>'s broadcast fan-out. Implemented by
/// the WebSocket <c>ClientSession</c> and by the remote-access WebRTC sink
/// (<c>RemoteFrameSink</c>), so a remote session rides the exact same fan-out as
/// <c>/ws</c> clients — the Broadcast methods are unchanged, and when no remote
/// sink is attached the <c>/ws</c> path behaves identically to before.
///
/// Only the members the broadcast loops touch are abstracted. The priority
/// members carry default implementations that fall back to the plain bulk
/// queue, so sinks without a priority lane (the remote sink, test fakes)
/// keep their pre-priority behaviour and compile untouched.
/// </summary>
internal interface IClientSink
{
    /// <summary>Whether this consumer wants display frames (used to skip the heavy serialize).</summary>
    bool WantsDisplay { get; }

    /// <summary>Enqueue a serialized frame. Must be non-blocking (callers run on the DSP thread).</summary>
    bool TryEnqueue(byte[] payload);

    /// <summary>Enqueue a 0x3B dial frame with priority semantics — it must
    /// never wait behind (or be evicted by) bulk display traffic. Default:
    /// the plain bulk queue; ClientSession overrides with a latest-value
    /// slot the send loop flushes ahead of the bulk drain. Returns false
    /// only when the fallback bulk enqueue dropped (priority slots never
    /// fail — latest-wins replacement is the design, not a drop).</summary>
    bool EnqueuePriorityVfo(byte[] payload) => TryEnqueue(payload);

    /// <summary>Same contract for the 0x3C state push.</summary>
    bool EnqueuePriorityState(byte[] payload) => TryEnqueue(payload);
}
