// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// On-demand <see cref="IRxAudioSink"/> for desktop/native-audio mode. The
/// host already plays RX audio through its native sink, so we normally send
/// nothing over the websocket. But a browser-side consumer (the DeepCW
/// decoder panel running in the Photino webview, which is itself a WS client)
/// needs the PCM stream — it raises <c>MsgType.AudioStreamRequest</c> while
/// active. While at least one client wants audio, this sink fans the same
/// single stream out via <see cref="StreamingHub.Broadcast(in AudioFrame)"/>;
/// otherwise it's inert (one volatile read per DSP tick, the same cost model
/// as the hub's empty-clients short-circuit).
///
/// Registered ONLY when <c>ShareOverLan</c> is off — with ShareOverLan on, an
/// ungated <see cref="WebSocketAudioSink"/> already broadcasts to every WS
/// client (including the local webview), so adding this gated sink too would
/// double the 0x02 frames.
/// </summary>
internal sealed class GatedWebSocketAudioSink : IRxAudioSink
{
    private readonly StreamingHub _hub;

    public GatedWebSocketAudioSink(StreamingHub hub) => _hub = hub;

    public void Publish(in AudioFrame frame)
    {
        if (!_hub.AudioStreamRequested) return;
        _hub.Broadcast(in frame);
    }

    /// <summary>
    /// Mute-exempt monitor audio (TX Monitor preview, Recorder playback) now
    /// reaches streaming listeners too. Field find: a REMOTE operator keyed
    /// MOX, saw their modulation on the TX display, toggled MON — and heard
    /// nothing, because the exempt lane historically dead-ended at the desktop
    /// NativeAudioSink (MON predates remote operation). An operator who
    /// pressed MON asked to hear their processed TX audio wherever they are
    /// listening; the hub's audio gate still applies, and while MON is on the
    /// TX-suppressed silence publisher stands down, so this is the only audio
    /// stream in flight — no interleave with the silence lane is possible.
    /// (Remote self-monitor carries the WebRTC round trip, so the returned
    /// audio is delayed — that is inherent to monitoring over a link, and MON
    /// remains the operator's own toggle.)
    /// </summary>
    public void PublishExempt(in AudioFrame frame)
    {
        // UNGATED, deliberately. Exempt frames exist only when the operator
        // explicitly asked to hear something (MON, Recorder playback) — that
        // press IS the audio request, and it must not defer to the passive
        // stream-interest counter. Field data forced this: monitor frames
        // measured LOUDER than the audible RX stream (-45 vs -55 dBFS) yet
        // arrived nowhere; whatever the interest counter's state in that
        // moment, an explicit monitor request outranks it. Broadcast with no
        // listeners is a near-free early-out in the hub.
        _hub.Broadcast(in frame);
    }
}
