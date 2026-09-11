using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// One remote-access WebRTC session (Phase 1). Answers a browser offer, runs the
/// SPAKE2+ password handshake over a reliable "control" DataChannel, and gates
/// all radio egress behind <see cref="RemoteSession"/> — nothing flows on the
/// "frames" channel until the password proves out (ADR-0008).
///
/// Auth wire protocol on the control channel (JSON, one message per send):
///   server → {t:"auth-params", salt, iterations, memoryKib, parallelism}
///   client → {t:"auth-share",  share}        (shareP)
///   server → {t:"auth-share",  share}        (shareV; still LOCKED)
///   client → {t:"auth-confirm",confirm}      (confirmP)
///   server → {t:"auth-ok",     confirm} + UNLOCK   | {t:"auth-fail"} + close
///
/// The real radio-frame bridge (StreamingHub → frames channel) attaches on
/// <see cref="Unlocked"/>; this class owns the gate, not the DSP wiring.
/// </summary>
public sealed class RemoteWebRtcSession
{
    private readonly ILogger _log;
    private readonly RemoteVerifierMaterial _verifier;
    private readonly RemoteSession _session;
    private readonly RTCPeerConnection _pc;
    private readonly Zeus.Server.StreamingHub? _hub;
    private readonly Guid _sinkId = Guid.NewGuid();

    // Loopback REST tunnel (read-write). When supplied, post-unlock requests on
    // the "api" channel are proxied to the radio's own local Kestrel and the
    // response returned. GET/HEAD read the radio's chrome; POST/PUT/DELETE/PATCH
    // drive it (VFO/mode/band/filter/AGC/drive/MOX/TUN/…). Null → channel inert.
    private readonly IHttpClientFactory? _httpFactory;
    private readonly string? _loopbackBaseUrl;

    // Dead-man TX safety: set once this session proxies a TX-keying request
    // (MOX/TUN on). If the WebRTC session then drops while still keyed, Close()
    // best-effort un-keys the radio so a lost link can never leave a remote
    // station transmitting into its antenna/amp. Volatile: written on the
    // data-channel callback thread, read on the teardown thread.
    private volatile bool _remoteTxArmed;

    // TX lease (field hardening, piece 3). Close() only fires once THIS side
    // notices the peer is gone, and SIPSorcery's ICE/DTLS failure detection
    // can take tens of seconds — a G2-1K keyed into an antenna for half a
    // minute after the operator's phone lost signal is not a safety story.
    // So a remote key-down is a LEASE, not a latch: the client pulses a
    // 1-byte keepalive (0x23) on the control channel every ~250 ms for the
    // life of the session; a watchdog un-keys the radio the moment the
    // pulses stop while the radio is keyed, independent of ICE state.
    // Keyed = radio truth (MoxState frames passing through TrySendFrame) AND
    // remote-armed (this session asked for it) — a desk operator keying
    // locally while a remote watches is never un-keyed by a remote hiccup.
    private const byte MsgTypeTxLeaseKeepalive = 0x23;
    private static readonly TimeSpan TxLeaseDeadline = TimeSpan.FromMilliseconds(1500);
    // Host-side zombie guard: a session whose keepalives have stopped for this
    // long is torn down (unwinds stream gates, frees the slot) without waiting
    // for ICE to notice. Generous on purpose — browser tab throttling is the
    // client's problem to solve (it pulses from a Worker), not a reason to
    // drop an idle but healthy session.
    private static readonly TimeSpan SessionLeaseDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeaseTickPeriod = TimeSpan.FromMilliseconds(250);
    private long _lastKeepaliveMs;          // 0 = never received (lease not yet in force)
    private volatile bool _radioKeyed;       // last MoxState seen on the wire
    private Timer? _leaseTimer;
    private int _leaseTripped;

    // Piece 3: owner service — operator slot + shared auth throttle. Null in
    // tests and the spike harness, in which case both policies are inert.
    private readonly RemoteWebRtcService? _owner;

    private RTCDataChannel? _control;
    private RTCDataChannel? _frames;
    private RTCDataChannel? _api;
    private RemoteFrameSink? _sink;

    // Remote voice TX: the browser's mic arrives as an inbound Opus audio track.
    // _micPipeline decodes + re-blocks it to the 960-sample f32le cadence the TX
    // ingest expects; _lastAudioSeq drives single-packet PLC across RTP gaps.
    // Only wired when the offer carries audio AND a hub is present (the real
    // radio host) — RX-only clients and transport tests stay audio-free.
    private RemoteMicAudioPipeline? _micPipeline;
    private int _lastAudioSeq = -1;
    private bool _audioWired;

    // SOTA RX audio: RX audio is Opus-encoded onto the outbound WebRTC audio
    // track instead of raw PCM over the unreliable "frames" data channel — the
    // browser's native adaptive jitter buffer + packet-loss concealment make it
    // robust over an internet hop (the PCM path has neither: maxRetransmits:0,
    // no reorder/jitter handling, so WAN loss = choppy/silent audio) at ~50×
    // less bandwidth. Default ON; set ZEUS_REMOTE_OPUS_RX=0 to fall back to the
    // legacy PCM-over-datachannel path if the Opus track ever misbehaves.
    internal static readonly bool OpusRxEnabled =
        (Environment.GetEnvironmentVariable("ZEUS_REMOTE_OPUS_RX") ?? "")
            .Trim() is not ("0" or "false" or "FALSE" or "False" or "off" or "OFF");
    private RemoteRxAudioPipeline? _rxAudio;

    // Post-unlock binary stream-request control frames (RX monitoring, Phase A):
    //   first byte 0x22 = display request, 0x21 = audio request; byte[1] 1/0 = enable/disable.
    // We track the session's current wanted-state so a duplicate enable can't
    // double-count the hub's global gate and a disconnect always unwinds it.
    private const byte MsgTypeAudioStreamRequest = 0x21;
    private const byte MsgTypeDisplayStreamRequest = 0x22;
    private bool _wantsDisplay;
    private bool _wantsAudio;

    // Named HttpClient used for the loopback REST tunnel (see ZeusHost.cs).
    internal const string LoopbackHttpClientName = "RemoteApiLoopback";

    // Cap on a tunnelled request/response body (1 MiB). Larger replies are
    // refused with 502 and larger requests with 413 — the chrome/control
    // endpoints are all small JSON; a giant body would be an export/dump or
    // bulk-import path we don't want to relay either direction.
    // FIELD LESSON (the zombie-session bug): a data-channel message larger
    // than the peer's advertised maxMessageSize (~256 KB in every major
    // browser) doesn't fail the request — the SPEC'S penalty is CLOSING THE
    // CHANNEL. A big /api reply (the v2 diagnostics JSON) was sent as one
    // message, the browser killed the api channel, and from then on every
    // REST control (MON, mixer, band, mode) silently died while MOX and the
    // media kept flowing — a session with live audio and dead controls.
    // Replies now clamp safely below that ceiling; oversized ones 502.
    // Chunked replies for genuinely large payloads = future work.
    private const int MaxResponseBytes = 192 * 1024;
    private const int MaxRequestBytes = 1 * 1024 * 1024;

    // The LAN Browser proxy returns whole device pages with inlined CSS/images
    // (data URIs), which legitimately exceed the 1 MiB chrome cap. It's the one
    // endpoint whose large reply is expected, so it gets its own ceiling.
    // Clamped to the same channel-safe bound: a larger single message kills
    // the channel regardless of endpoint (see MaxResponseBytes). Restoring a
    // big-page LAN proxy over the tunnel requires chunking, not a bigger cap.
    private const int LanProxyMaxResponseBytes = 192 * 1024;
    private const string LanProxyPath = "/api/lan/proxy";

    /// <summary>
    /// Always-denied endpoints (BOTH read and write). A request to one of these
    /// is refused (403, no loopback) so a remote operator cannot exfiltrate or
    /// overwrite credentials, secrets, or identity. Matching is case-insensitive
    /// prefix on the canonical URL path (query string ignored).
    ///
    /// Derived by scanning ZeusEndpoints.cs for endpoints that touch
    /// credentials/identity/secrets or that export the prefs DB:
    ///   /api/remote/password   — remote-access session password store/status
    ///                            (a write here would let a remote change the
    ///                            very password gating it)
    ///   /api/prefs/databases/export — downloads the entire prefs LiteDB
    ///                            (contains the QRZ password + remote verifier)
    ///   /api/log/export        — full logbook export (PII / ADIF dump)
    ///   /api/system/windows-firewall — host OS firewall mutation; local-only
    ///                            because remote tunnel traffic is loopback-proxied
    ///
    /// NOTE: /api/qrz and /api/chat were previously fully denied here, but the
    /// remote operator IS the authenticated station owner (SPAKE2+ session
    /// password proven) — they should reach QRZ lookup and chat exactly as they
    /// do at the desk (1:1 remote). Both are now allowed; the residual exposure
    /// (a remote session can change the QRZ login/API key, and chat posts under
    /// the station callsign) is accepted as operator-equivalent access. The
    /// truly destructive surfaces above stay denied.
    /// Conservative by design: when unsure, deny.
    /// </summary>
    private static readonly string[] DeniedPathPrefixes =
    {
        "/api/remote/password",
        "/api/prefs/databases/export",
        "/api/log/export",
        "/api/system/windows-firewall",
        // /api/support — the maintainer-support Allow/Deny surface. It is the
        // OPERATOR's local decision point; a remote peer (even the authenticated
        // station owner) must never approve a read-only support session for itself
        // or another admin through the tunnel (remote-diag P3).
        "/api/support",
    };

    /// <summary>
    /// Write-denied endpoints (mutating methods only — GET still allowed). Reads
    /// of these are safe chrome, but a remote MUST NOT change them:
    ///   /api/tx/ps             — PureSignal. KB2UKA hard-stop: no remote arm,
    ///                            disarm, calibration, or persistence change. An
    ///                            inadvertently armed PS on an external-tap
    ///                            feedback chain can saturate the feedback ADC.
    ///   /api/prefs/databases   — switching/importing/deleting the active prefs
    ///                            LiteDB out from under the running radio.
    ///   /api/app               — process-control endpoints such as restart,
    ///                            quit, and uninstall. These are local-only;
    ///                            tunneled requests reach Kestrel as loopback.
    /// </summary>
    private static readonly string[] WriteDeniedPrefixes =
    {
        "/api/app",
        "/api/tx/ps",
        "/api/tx/swr-protection",   // PA guard off is a desk decision, never a remote one
        "/api/prefs/databases",
    };

    public RemoteWebRtcSession(
        RemoteVerifierMaterial verifier, ILogger log,
        IReadOnlyList<RTCIceServer>? iceServers = null, Zeus.Server.StreamingHub? hub = null,
        IHttpClientFactory? httpFactory = null, string? loopbackBaseUrl = null,
        RemoteWebRtcService? owner = null)
    {
        _owner = owner;
        _verifier = verifier;
        _log = log;
        _hub = hub;
        _httpFactory = httpFactory;
        _loopbackBaseUrl = loopbackBaseUrl?.TrimEnd('/');

        var gate = new Spake2PlusAuthGate(
            RemoteAuthConstants.Context, RemoteAuthConstants.IdProver, RemoteAuthConstants.IdVerifier,
            verifier.W0, verifier.L);
        _session = new RemoteSession(gate);

        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = iceServers?.ToList() ?? new List<RTCIceServer>(),
        });

        // ---- ICE diagnostics (2026-09-10 field round 2) --------------------
        // Gathering went nondeterministic in the field: the same broker server
        // list produced srflx+relay on one attempt and nothing a minute later,
        // and a fully-loaded answer still failed connectivity checks. The
        // library was a black box throughout. The three lines below are always
        // on (a handful per session); SIPSorcery's own internal log is a
        // firehose, so it only flows when ZEUS_RTC_DIAG=1 is set on the
        // service — flip it for a diagnosis session, not for daily use.
        if (Environment.GetEnvironmentVariable("ZEUS_RTC_DIAG") == "1"
            && Interlocked.Exchange(ref s_sipsorceryLogWired, 1) == 0)
        {
            SIPSorcery.LogFactory.Set(new SipsorceryLogBridge(log));
            log.LogInformation("rtc.remote SIPSorcery internal logging wired (ZEUS_RTC_DIAG=1)");
        }

        _pc.onicecandidate += c =>
        {
            if (c is not null)
                _log.LogInformation(
                    "rtc.remote gathered {Type} {Addr}:{Port}", c.type, c.address, c.port);
        };
        _pc.onicegatheringstatechange += s =>
            _log.LogInformation("rtc.remote gathering → {State}", s);
        _pc.oniceconnectionstatechange += s =>
            _log.LogInformation("rtc.remote ice → {State}", s);

        _pc.ondatachannel += OnDataChannel;
        _pc.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.closed
                or RTCPeerConnectionState.failed
                or RTCPeerConnectionState.disconnected)
                Close();
        };
    }

    /// <summary>True once the password handshake has succeeded.</summary>
    public bool IsUnlocked => _session.IsUnlocked;

    /// <summary>Raised once, when the session transitions to UNLOCKED (attach the frame bridge here).</summary>
    public event Action? Unlocked;

    /// <summary>Raised once, when the session is torn down (for owner cleanup).</summary>
    public event Action? Closed;

    private int _closed;

    /// <summary>Answer the browser's offer with a self-contained (vanilla-ICE) SDP.</summary>
    public async Task<string> CreateAnswerAsync(string offerSdp, CancellationToken ct = default)
    {
        // If the browser offered a voice-mic audio track, attach our recvonly
        // Opus receiver BEFORE setRemoteDescription so the answer negotiates it.
        // Gated on the offer actually containing audio so an RX-only client
        // (whose offer has no m=audio) is answered exactly as before.
        MaybeWireRemoteVoice(offerSdp);

        // The offer's own census: what the client brought to the table. When a
        // session fails, this line beside the answer's census shows which side
        // of the pairing was starved.
        var (oHost, oSrflx, oRelay) = CandidateCensus(offerSdp);
        _log.LogInformation(
            "rtc.remote offer candidates: {Host} host, {Srflx} srflx, {Relay} relay",
            oHost, oSrflx, oRelay);

        var setResult = _pc.setRemoteDescription(
            new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
        if (setResult != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"setRemoteDescription failed: {setResult}");

        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);

        // Vanilla ICE: this answer is the ONLY chance to tell the client our
        // candidates — the signal channel has no trickle leg, so anything
        // gathered after this method returns is lost forever.
        //
        // We must NOT trust iceGatheringState here. Field capture 2026-09-10
        // (SIPSorcery 10.0.14) showed the state reaching 'complete' the moment
        // the host candidates were enumerated — ~30 ms in — and only THEN, a
        // further ~1 s later, did the STUN srflx and TURN relay candidates
        // arrive and re-open gathering. A wait that latches the first
        // 'complete' therefore shipped a host-only answer while the public
        // candidates were still in flight; the one attempt that worked was the
        // one where srflx happened to beat the answer. That is the whole bug.
        //
        // So wait on the ANSWER'S CONTENT, not on a state enum: poll the local
        // SDP until it actually carries a srflx or relay line, capped at 6 s.
        // On the LAN (no public candidate is coming) the cap is spent in full
        // once, then the honest LAN-only answer ships — acceptable, since a LAN
        // client pairs on host candidates anyway.
        await WaitForPublicCandidateAsync(_pc, TimeSpan.FromSeconds(6), ct);

        var sdp = _pc.localDescription.sdp.ToString();
        var (host, srflx, relay) = CandidateCensus(sdp);
        if (srflx + relay == 0)
            _log.LogWarning(
                "rtc.remote answer is LAN-only ({Host} host, 0 srflx, 0 relay) — "
                + "STUN/TURN gathering produced no public candidate; off-network "
                + "clients cannot reach this answer",
                host);
        else
            _log.LogInformation(
                "rtc.remote answer candidates: {Host} host, {Srflx} srflx, {Relay} relay",
                host, srflx, relay);
        return sdp;
    }

    /// <summary>
    /// Wait until the peer connection's local description actually contains a
    /// server-reflexive or relay candidate, or the timeout elapses. This is
    /// deliberately content-based rather than state-based: SIPSorcery's
    /// iceGatheringState can report 'complete' before the STUN/TURN candidates
    /// arrive, so the state is not a safe signal that the answer is ready.
    /// </summary>
    private static async Task WaitForPublicCandidateAsync(
        RTCPeerConnection pc, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var sdp = pc.localDescription?.sdp?.ToString();
            if (sdp is not null
                && (sdp.Contains(" typ srflx", StringComparison.Ordinal)
                    || sdp.Contains(" typ relay", StringComparison.Ordinal)))
                return;

            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Count the candidate types in an SDP — the answer's own truth about
    /// reachability. srflx+relay == 0 means no off-LAN client can connect.
    /// </summary>
    internal static (int Host, int Srflx, int Relay) CandidateCensus(string sdp)
    {
        static int Count(string s, string needle)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                n++;
                i += needle.Length;
            }
            return n;
        }

        return (Count(sdp, " typ host"), Count(sdp, " typ srflx"), Count(sdp, " typ relay"));
    }

    // ---- SIPSorcery internal-log bridge (ZEUS_RTC_DIAG=1) ------------------
    private static int s_sipsorceryLogWired;

    /// <summary>
    /// Adapts the session's ILogger into the ILoggerFactory SIPSorcery wants,
    /// forwarding everything at Information so it clears the INFO+ file sink.
    /// Diagnosis-session volume — never leave the env var set in daily use.
    /// </summary>
    private sealed class SipsorceryLogBridge(ILogger sink) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new Redirect(sink, categoryName);
        public void AddProvider(ILoggerProvider provider) { /* single fixed sink */ }
        public void Dispose() { /* nothing owned */ }

        private sealed class Redirect(ILogger sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                sink.LogInformation(exception,
                    "sipsorcery {Category}: {Message}", category, formatter(state, exception));
            }
        }
    }

    /// <summary>
    /// Egress a radio frame on the unreliable "frames" channel — refused (returns
    /// false, never throws) until the session is UNLOCKED.
    /// </summary>
    public bool TrySendFrame(byte[] frame)
    {
        if (!_session.TryEgress())
            return false;

        // Opus-RX mode: divert RX audio frames (0x02) onto the WebRTC audio track
        // and DON'T also ship the raw PCM over the data channel. Every other frame
        // type (spectrum/meters/MOX/…) still rides the data channel as before.
        if (_rxAudio is not null
            && frame.Length >= Zeus.Contracts.WireFormat.HeaderSize
            && frame[0] == (byte)Zeus.Contracts.MsgType.AudioPcm)
        {
            try { _rxAudio.Encode(Zeus.Contracts.AudioFrame.Deserialize(frame)); }
            catch { /* malformed frame — drop, never fatal */ }
            return true;
        }

        // Radio keyed truth for the TX lease watchdog: the hub broadcasts a
        // MoxState edge on every key/unkey regardless of who keyed. Observe it
        // on the way past — cheaper and more honest than re-deriving it from
        // what this session asked for.
        if (frame.Length >= 3 && frame[0] == (byte)Zeus.Contracts.MsgType.MoxState)
            _radioKeyed = frame[1] != 0 || frame[2] != 0;

        if (_frames is null)
            return false;

        // Display pacing + backpressure (field rounds 1-2: session hung, then
        // 'stuck / restarts with hiccups' on Wi-Fi). A remote session never
        // needs the full local frame rate: pace display frames to ~20 fps in
        // the clear, stretching toward ~7 fps as the channel's SCTP buffer
        // fills, and only past the hard ceiling drop at the door outright.
        // Graceful degradation instead of freeze-then-catch-up oscillation.
        // Meters/status are tiny and pass unpaced; audio rides the media
        // track untouched.
        if (frame.Length >= 1 && frame[0] == (byte)Zeus.Contracts.MsgType.DisplayFrame)
        {
            ulong buffered = _frames.bufferedAmount;
            long now = Environment.TickCount64;
            if (buffered > MaxBufferedFrameBytes)
            {
                // Hard ceiling — drop at the door, and treat it as congestion:
                // multiplicative back-off of the pace (piece 3 bandwidth
                // adaptation, AIMD). ×1.5 per congested drop, floor 4 fps.
                long grown = Math.Min(MaxDisplayGapMs, (long)(Volatile.Read(ref _displayGapMs) * 3 / 2));
                if (grown != Volatile.Read(ref _displayGapMs))
                {
                    Volatile.Write(ref _displayGapMs, grown);
                    _log.LogDebug("rtc.remote pace back-off → {Gap} ms/frame", grown);
                }
                Volatile.Write(ref _lastCongestionMs, now);
                return true;
            }
            // Additive recovery: after RecoveryQuietMs without touching the
            // ceiling and with the buffer under a quarter of it, walk the gap
            // back 10 ms per sent frame toward the 20 fps base.
            long gap = Volatile.Read(ref _displayGapMs);
            if (gap > NormalDisplayGapMs
                && buffered < MaxBufferedFrameBytes / 4
                && now - Volatile.Read(ref _lastCongestionMs) > RecoveryQuietMs)
            {
                gap = Math.Max(NormalDisplayGapMs, gap - 10);
                Volatile.Write(ref _displayGapMs, gap);
            }
            // Half-full buffer still stretches the pace within the current
            // envelope (the pre-adaptation behaviour, kept as the fast path).
            long effectiveGap = buffered > MaxBufferedFrameBytes / 2 ? Math.Max(gap, SlowDisplayGapMs) : gap;
            // Pace PER RECEIVER. Field find (remote RX2 blank, audio fine):
            // one shared timestamp meant RX1's frame took the slot and RX2's,
            // arriving a millisecond later inside the gap, was paced out as
            // "a fresher frame is right behind" — but the fresher frame was
            // RX1's next one. RX2 never won a slot. Each RxId now keeps its
            // own clock; the congestion envelope (gap/AIMD) stays shared
            // because it describes the channel, not a receiver.
            int rx = frame.Length > Zeus.Contracts.WireFormat.HeaderSize
                ? frame[Zeus.Contracts.WireFormat.HeaderSize]
                : 0;
            if (rx >= _lastDisplaySendMs.Length) rx = _lastDisplaySendMs.Length - 1;
            if (now - _lastDisplaySendMs[rx] < effectiveGap)
                return true; // paced out — a fresher frame for this receiver is right behind
            _lastDisplaySendMs[rx] = now;
        }
        else if (_frames.bufferedAmount > MaxBufferedFrameBytes)
        {
            return true; // non-display under a choked channel: drop too
        }

        _frames.send(frame);
        return true;
    }

    /// <summary>SCTP buffered ceiling before frames drop at the door
    /// (~a quarter second of full-rate spectrum; audio rides the media track
    /// and is unaffected).</summary>
    private const ulong MaxBufferedFrameBytes = 256 * 1024;

    /// <summary>Display pacing: minimum inter-frame gap in the clear (~20 fps —
    /// plenty for a remote panadapter) and under buffer pressure (~7 fps).</summary>
    private const long NormalDisplayGapMs = 50;
    private const long SlowDisplayGapMs = 150;
    /// <summary>Adaptive pace ceiling (4 fps) and the quiet time required
    /// before the gap walks back toward 20 fps (piece 3 AIMD).</summary>
    private const long MaxDisplayGapMs = 250;
    private const long RecoveryQuietMs = 3000;
    private long _displayGapMs = NormalDisplayGapMs;
    private long _lastCongestionMs;
    // Last display send per RxId (index = first body byte); slot 7 pools any
    // higher ids so an unexpected value can never index out of range.
    private readonly long[] _lastDisplaySendMs = new long[8];

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        _leaseTimer?.Dispose();
        _leaseTimer = null;

        // Dead-man TX safety: if this session left the radio keyed, drop the
        // carrier before tearing anything else down. A remote link that fails
        // mid-transmit must never strand the station on-air.
        if (_remoteTxArmed)
        {
            _remoteTxArmed = false;
            _ = BestEffortUnkeyAsync();
        }

        _session.Close();
        if (_hub is not null)
        {
            // Release any RX display/audio request still held so a remote
            // disconnect can't pin the global gates on (mirrors the /ws
            // ClientSession cleanup in StreamingHub.AttachClientAsync).
            if (_wantsDisplay) { _wantsDisplay = false; _hub.AdjustDisplayRequests(-1); }
            if (_wantsAudio) { _wantsAudio = false; _hub.AdjustAudioRequests(-1); }
            _hub.DetachSink(_sinkId);
        }
        _sink?.Dispose();
        try { _pc.close(); } catch { /* already torn down */ }
        Closed?.Invoke();
    }

    private void OnDataChannel(RTCDataChannel dc)
    {
        switch (dc.label)
        {
            case "control":
                _control = dc;
                // Client-initiated: it sends {t:"hello"} once its channel opens
                // and we reply with auth-params. Avoids depending on the
                // answerer-side onopen firing.
                dc.onmessage += (_, _, data) => OnControlMessage(data);
                break;
            case "frames":
                _frames = dc;
                break;
            case "api":
                // Read-write REST tunnel. Deny-by-default: each message is
                // ignored until the session is UNLOCKED (checked in the handler);
                // only non-denylisted paths reach loopback, and writes are
                // additionally barred from the burn-zone (PureSignal) + prefs DB.
                _api = dc;
                dc.onmessage += (_, _, data) => OnApiMessage(data);
                break;
            default:
                _log.LogWarning("rtc.remote unexpected data channel '{Label}'", dc.label);
                break;
        }
    }

    private void SendAuthParams()
    {
        _control!.send(JsonSerializer.Serialize(new
        {
            t = "auth-params",
            salt = Convert.ToBase64String(_verifier.Salt),
            iterations = _verifier.Iterations,
            memoryKib = _verifier.MemoryKib,
            parallelism = _verifier.Parallelism,
        }));
    }

    /// <summary>
    /// Split control-channel input: pre-unlock and JSON auth messages go to the
    /// SPAKE2+ gate; post-unlock binary stream-request frames (0x21/0x22) drive
    /// the RX display/audio gates. The two are unambiguous on the wire — auth is
    /// always JSON (first byte '{' = 0x7B), stream-requests lead with 0x21/0x22,
    /// so a 0x21/0x22 first byte is treated as binary control, never JSON.
    /// Deny-by-default: binary control is honoured ONLY after unlock; anything
    /// non-auth while LOCKED still fails the session closed via HandleControlAsync.
    /// </summary>
    private void OnControlMessage(byte[] data)
    {
        if (_session.IsUnlocked && data.Length >= 1)
        {
            if (data[0] == MsgTypeTxLeaseKeepalive)
            {
                Volatile.Write(ref _lastKeepaliveMs, Environment.TickCount64);
                return;
            }
            if (data[0] is MsgTypeDisplayStreamRequest or MsgTypeAudioStreamRequest)
            {
                HandleStreamRequest(data);
                return;
            }
        }

        _ = HandleControlAsync(data);
    }

    /// <summary>
    /// Apply a post-unlock binary stream-request frame: [type][enable:u8].
    /// 0x22 toggles the display gate, 0x21 the audio gate. Tracks the session's
    /// current wanted-state so the hub's global counter moves at most once per
    /// transition (and Close can unwind it exactly).
    /// </summary>
    private void HandleStreamRequest(byte[] data)
    {
        if (_hub is null) return;
        bool enable = data.Length > 1 && data[1] != 0;
        switch (data[0])
        {
            case MsgTypeDisplayStreamRequest:
                if (enable == _wantsDisplay) return;
                _wantsDisplay = enable;
                _hub.AdjustDisplayRequests(enable ? 1 : -1);
                break;
            case MsgTypeAudioStreamRequest:
                if (enable == _wantsAudio) return;
                _wantsAudio = enable;
                _hub.AdjustAudioRequests(enable ? 1 : -1);
                break;
        }
    }

    // -- Remote voice TX (inbound Opus audio track) --------------------------

    /// <summary>
    /// Attach the recvonly Opus audio receiver iff the browser offered a mic
    /// track. No-op when there is no hub (transport tests), no audio in the offer
    /// (RX-only client), or it is already wired. Decoded voice is injected into
    /// the SAME TX-mic ingest local audio uses; MOX + source arbitration
    /// downstream decide whether it actually reaches the air.
    /// </summary>
    private void MaybeWireRemoteVoice(string offerSdp)
    {
        if (_audioWired || _hub is null) return;
        if (string.IsNullOrEmpty(offerSdp)
            || offerSdp.IndexOf("m=audio", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        _micPipeline = new RemoteMicAudioPipeline(OnRemoteMicBlock);
        // With Opus-RX on, the same audio m-line is bidirectional: we RECEIVE the
        // operator's mic AND SEND the radio's RX audio over it. Off (default) it
        // stays recvonly (mic only) exactly as before.
        var direction = OpusRxEnabled ? MediaStreamStatusEnum.SendRecv : MediaStreamStatusEnum.RecvOnly;
        var audioTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.audio, false,
            new List<SDPAudioVideoMediaFormat> { new(SDPMediaTypesEnum.audio, 111, "OPUS", 48000, 2) },
            direction);
        _pc.addTrack(audioTrack);
        _pc.OnRtpPacketReceived += OnAudioRtpReceived;
        if (OpusRxEnabled) _rxAudio = new RemoteRxAudioPipeline(OnEncodedRxOpus);
        _audioWired = true;
        _log.LogInformation(
            "rtc.remote voice-mic track negotiated (Opus 48k {Dir})",
            OpusRxEnabled ? "sendrecv +RX-audio" : "recvonly");
    }

    private void OnAudioRtpReceived(
        System.Net.IPEndPoint remoteEp, SDPMediaTypesEnum media, RTPPacket pkt)
    {
        if (media != SDPMediaTypesEnum.audio || _micPipeline is null) return;
        if (!_session.IsUnlocked) return; // deny-by-default: no voice before unlock
        try
        {
            int seq = pkt.Header.SequenceNumber;
            if (_lastAudioSeq >= 0)
            {
                // Conceal a small gap with Opus PLC; cap it so a long dropout
                // can't spin the decoder. A reorder/duplicate (huge wrapped gap)
                // is simply decoded in place.
                int gap = (ushort)(seq - _lastAudioSeq - 1);
                for (int i = 0; i < gap && i < 5; i++) _micPipeline.DecodeLost();
            }
            _lastAudioSeq = seq;
            _micPipeline.Decode(pkt.Payload);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "rtc.remote audio rtp decode failed");
        }
    }

    /// <summary>Inject a decoded 960-sample f32le voice block into the TX-mic ingest.</summary>
    private void OnRemoteMicBlock(ReadOnlyMemory<byte> f32leBlock) => _hub?.InjectMicPcm(f32leBlock);

    /// <summary>Send one 20 ms Opus RX packet on the outbound audio track. Called
    /// synchronously from the frame-drain task (Opus-RX mode only). Never throws.</summary>
    private void OnEncodedRxOpus(ReadOnlyMemory<byte> opus)
    {
        try { _pc.SendAudio(RemoteRxAudioPipeline.BlockRtpUnits, opus.ToArray()); }
        catch { /* peer gone / track not ready — drop, never fatal */ }
    }

    // -- Read-write REST tunnel ---------------------------------------------
    //
    // Post-unlock only. Each "api" message is {id, method, path, body?,
    // contentType?}. The radio loopback-proxies the request to its own local
    // Kestrel and replies with {id, status, contentType, body}. Safety gates,
    // in order:
    //   1. LOCKED               → ignore entirely (deny-by-default, fail-closed).
    //   2. unsupported method   → 405 (only GET/HEAD/POST/PUT/DELETE/PATCH).
    //   3. request body >1 MiB  → 413 (no bulk import through the tunnel).
    //   4. path traversal       → 403 (can't collapse "../" onto a denied path).
    //   5. always-denied path   → 403 (secrets/identity/exports, any method).
    //   6. write to write-denied→ 403 (PureSignal burn-zone, prefs DB).
    //   7. otherwise            → loopback proxy; 502 on >1 MiB reply or error.
    // Fire-and-forget like the control handling — never block the data-channel
    // callback thread, and never throw out of the handler.

    private void OnApiMessage(byte[] data)
    {
        if (!_session.IsUnlocked) return; // pre-unlock API input is ignored
        _ = HandleApiRequestAsync(data);
    }

    private async Task HandleApiRequestAsync(byte[] data)
    {
        int id = 0;
        try
        {
            string method;
            string path;
            string? body;
            string? contentType;
            using (var doc = JsonDocument.Parse(data))
            {
                var root = doc.RootElement;
                id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
                method = (root.TryGetProperty("method", out var mEl) ? mEl.GetString() : null)
                    ?? "GET";
                path = (root.TryGetProperty("path", out var pEl) ? pEl.GetString() : null) ?? "";
                body = root.TryGetProperty("body", out var bEl) ? bEl.GetString() : null;
                contentType = root.TryGetProperty("contentType", out var cEl) ? cEl.GetString() : null;
            }

            if (!IsAllowedMethod(method))
            {
                SendApiReply(id, 405);
                return;
            }

            bool mutating = IsMutatingMethod(method);

            // Cap the request body so the tunnel can't be used as a bulk-import
            // channel. UTF-16 length is a safe over-estimate of UTF-8 bytes.
            if (body is not null && body.Length > MaxRequestBytes)
            {
                SendApiReply(id, 413);
                return;
            }

            if (_httpFactory is null || string.IsNullOrEmpty(_loopbackBaseUrl) || !path.StartsWith('/'))
            {
                SendApiReply(id, 502);
                return;
            }

            // Reject path traversal outright. Without this, a path like
            // "/api/state/../prefs/databases/export" slips past the denylist
            // (it prefix-matches nothing) yet Uri canonicalisation collapses the
            // "../" so the loopback request lands on a denied endpoint — a bypass
            // that would exfiltrate the prefs DB. Legit SPA /api paths never
            // contain dot-segments or percent-encoded ones.
            if (path.Contains("..", StringComparison.Ordinal)
                || path.Contains("%2e", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning("rtc.remote api DENY (traversal) {Path}", path);
                SendApiReply(id, 403);
                return;
            }

            // Build the loopback target once and denylist-check the CANONICAL
            // path that will actually be requested — never the raw input. Verify
            // it stays on loopback so a crafted authority can't redirect the
            // request off-box (SSRF).
            if (!Uri.TryCreate(_loopbackBaseUrl + path, UriKind.Absolute, out var target)
                || !target.IsLoopback)
            {
                SendApiReply(id, 502);
                return;
            }

            // Sensitive-endpoint denylist — refuse before any loopback call.
            if (IsDenied(target.AbsolutePath, mutating))
            {
                _log.LogWarning("rtc.remote api DENY {Method} {Path}", method, target.AbsolutePath);
                SendApiReply(id, 403);
                return;
            }

            // HEAD is proxied as GET and the body discarded; all other methods
            // pass through verbatim with their request body (when present).
            var httpMethod = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
                ? HttpMethod.Get
                : new HttpMethod(method.ToUpperInvariant());

            var client = _httpFactory.CreateClient(LoopbackHttpClientName);
            using var req = new HttpRequestMessage(httpMethod, target);
            if (mutating && !string.IsNullOrEmpty(body))
            {
                req.Content = new StringContent(body, Encoding.UTF8);
                var ct = string.IsNullOrEmpty(contentType) ? "application/json" : contentType;
                if (MediaTypeHeaderValue.TryParse(ct, out var parsed))
                    req.Content.Headers.ContentType = parsed;
            }

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            // Track TX keying so a dropped link un-keys the radio (see Close()).
            if (mutating && (int)resp.StatusCode is >= 200 and < 300)
                TrackTxKeying(target.AbsolutePath, body);

            // Cap the body so the tunnel can't be used as a bulk-export channel.
            // The LAN Browser proxy is the one endpoint with an expected-large
            // reply (a whole device page with inlined assets), so it gets a
            // higher ceiling than the small chrome/control endpoints.
            int maxResp = path.StartsWith(LanProxyPath, StringComparison.OrdinalIgnoreCase)
                ? LanProxyMaxResponseBytes
                : MaxResponseBytes;
            if (resp.Content.Headers.ContentLength is long len && len > maxResp)
            {
                SendApiReply(id, 502);
                return;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length > maxResp)
            {
                SendApiReply(id, 502);
                return;
            }

            var respContentType = resp.Content.Headers.ContentType?.ToString();
            var respBody = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
                ? ""
                : Encoding.UTF8.GetString(bytes);
            SendApiReply(id, (int)resp.StatusCode, respContentType, respBody);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "rtc.remote api request failed — replying 502");
            try { SendApiReply(id, 502); } catch { /* channel gone */ }
        }
    }

    private static bool IsAllowedMethod(string method)
        => method.ToUpperInvariant() is "GET" or "HEAD" or "POST" or "PUT" or "DELETE" or "PATCH";

    private static bool IsMutatingMethod(string method)
        => method.ToUpperInvariant() is "POST" or "PUT" or "DELETE" or "PATCH";

    private static bool IsDenied(string path, bool mutating)
    {
        // Compare on the path only (strip any query string) so a denied prefix
        // can't be smuggled past with a "?..." suffix.
        int q = path.IndexOf('?');
        var p = q >= 0 ? path[..q] : path;
        foreach (var prefix in DeniedPathPrefixes)
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        if (mutating)
            foreach (var prefix in WriteDeniedPrefixes)
                if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    /// <summary>
    /// Note a successful MOX/TUN keying request so the dead-man in Close() can
    /// un-key a dropped session. Arms on {"on":true} to /api/tx/{mox,tun},
    /// disarms on {"on":false}.
    /// </summary>
    private void TrackTxKeying(string path, string? body)
    {
        // CWX keys MOX server-side for the length of the message; a remote
        // that sends text then vanishes is exactly the lease's case. Arm on
        // send, disarm on abort; the MoxState off-edge clears the keyed flag.
        if (path.Equals("/api/cw/send", StringComparison.OrdinalIgnoreCase)) { _remoteTxArmed = true; return; }
        if (path.Equals("/api/cw/abort", StringComparison.OrdinalIgnoreCase)) { _remoteTxArmed = false; return; }
        if (!path.Equals("/api/tx/mox", StringComparison.OrdinalIgnoreCase)
            && !path.Equals("/api/tx/tun", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            using var doc = JsonDocument.Parse(body ?? "");
            if (doc.RootElement.TryGetProperty("on", out var onEl)
                && onEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _remoteTxArmed = onEl.GetBoolean();
        }
        catch { /* unparseable body — leave the arm state unchanged */ }
    }

    /// <summary>
    /// TX lease watchdog tick (250 ms). Two deadlines on the same clock:
    /// keyed + no pulse for 1.5 s → un-key NOW (the lease has lapsed);
    /// no pulse for 30 s at all → the session is a zombie, tear it down.
    /// Neither fires until the client's first pulse has arrived.
    /// </summary>
    private void LeaseTick()
    {
        if (_closed != 0) return;
        long last = Volatile.Read(ref _lastKeepaliveMs);
        if (last == 0) return; // lease not in force — old client, or not yet pulsing
        long silent = Environment.TickCount64 - last;

        if (silent >= (long)TxLeaseDeadline.TotalMilliseconds
            && _remoteTxArmed && _radioKeyed
            && Interlocked.Exchange(ref _leaseTripped, 1) == 0)
        {
            _remoteTxArmed = false;
            _log.LogWarning("rtc.remote TX lease lapsed ({Silent} ms without keepalive while keyed) — un-keying", silent);
            _ = BestEffortUnkeyAsync();
        }
        else if (silent < (long)TxLeaseDeadline.TotalMilliseconds)
        {
            _leaseTripped = 0; // pulses back: a fresh key-down earns a fresh lease
        }

        if (silent >= (long)SessionLeaseDeadline.TotalMilliseconds)
        {
            _log.LogWarning("rtc.remote no keepalive for {Silent} ms — closing zombie session", silent);
            Close();
        }
    }

    /// <summary>
    /// Best-effort un-key (MOX off + TUN off) over loopback when a keyed session
    /// drops. Fire-and-forget; never throws. Short per-request timeout so a
    /// wedged Kestrel can't hang teardown.
    /// </summary>
    private async Task BestEffortUnkeyAsync()
    {
        if (_httpFactory is null || string.IsNullOrEmpty(_loopbackBaseUrl)) return;
        try
        {
            var client = _httpFactory.CreateClient(LoopbackHttpClientName);
            foreach (var p in new[] { "/api/cw/abort", "/api/tx/mox", "/api/tx/tun" })
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _loopbackBaseUrl + p)
                {
                    Content = new StringContent("{\"on\":false}", Encoding.UTF8, "application/json"),
                };
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var _ = await client.SendAsync(req, cts.Token).ConfigureAwait(false);
            }
            _log.LogWarning("rtc.remote session dropped while keyed — sent dead-man un-key (MOX/TUN off)");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "rtc.remote dead-man un-key failed");
        }
    }

    private void SendApiReply(int id, int status, string? contentType = null, string? body = null)
    {
        if (_api is null) return;
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["status"] = status,
        };
        if (contentType is not null) payload["contentType"] = contentType;
        if (body is not null) payload["body"] = body;
        if (contentType is not null) payload["headers"] = new Dictionary<string, string> { ["content-type"] = contentType };
        try { _api.send(JsonSerializer.Serialize(payload)); } catch { /* channel closed */ }
    }

    private async Task HandleControlAsync(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var t = doc.RootElement.GetProperty("t").GetString();

            if (_session.IsUnlocked)
                return; // post-unlock control plane (inbound VFO etc.) attaches with the frame bridge

            switch (t)
            {
                case "hello":
                    SendAuthParams();
                    break;
                case "auth-share":
                {
                    // Piece 3 host auth throttle: refuse the attempt outright
                    // while locked out — before any SPAKE2 math runs.
                    long retryMs = _owner?.AuthRetryAfterMs() ?? 0;
                    if (retryMs > 0)
                    {
                        _log.LogWarning("rtc.remote auth attempt during lockout ({Retry} ms remaining)", retryMs);
                        try { _control!.send(JsonSerializer.Serialize(new { t = "auth-throttled", retryMs })); } catch { }
                        Close();
                        break;
                    }
                    var share = Convert.FromBase64String(doc.RootElement.GetProperty("share").GetString()!);
                    var outcome = await _session.SubmitAuthAsync(share);
                    if (outcome.Action == RemoteSessionAction.Reply)
                        _control!.send(Json("auth-share", "share", outcome.Reply.ToArray()));
                    else
                        FailAndClose();
                    break;
                }
                case "auth-confirm":
                {
                    var confirm = Convert.FromBase64String(doc.RootElement.GetProperty("confirm").GetString()!);
                    var outcome = await _session.SubmitAuthAsync(confirm);
                    if (outcome.Action == RemoteSessionAction.Unlock)
                    {
                        _owner?.RecordAuthSuccess();
                        // Piece 3 single-operator policy: the password was
                        // right, but the radio already has a remote operator.
                        // Refuse the slot, tell the client why, close. The
                        // desk UI is never part of this — it needs no slot.
                        if (_owner is not null && !_owner.TryAcquireOperatorSlot(this))
                        {
                            _log.LogWarning("rtc.remote unlock refused — another operator holds the session");
                            try { _control!.send(JsonSerializer.Serialize(new { t = "auth-busy" })); } catch { }
                            Close();
                            break;
                        }
                        _control!.send(Json("auth-ok", "confirm", outcome.Reply.ToArray()));
                        _log.LogInformation("rtc.remote session UNLOCKED");

                        // Arm the radio data path: register a sink so the hub's
                        // broadcast fan-out reaches this session's frames channel
                        // (gated again by TrySendFrame). Only happens post-unlock.
                        if (_hub is not null)
                        {
                            _sink = new RemoteFrameSink(TrySendFrame);
                            _hub.AttachSink(_sinkId, _sink);
                        }

                        // TX lease watchdog runs for the life of the unlocked
                        // session. Armed only once the first keepalive lands,
                        // so an old client that never pulses is unaffected
                        // (it just keeps the pre-lease Close() dead-man).
                        _leaseTimer = new Timer(_ => LeaseTick(), null, LeaseTickPeriod, LeaseTickPeriod);

                        Unlocked?.Invoke();
                    }
                    else
                    {
                        _owner?.RecordAuthFailure();
                        FailAndClose();
                    }
                    break;
                }
                default:
                    FailAndClose(); // anything else while LOCKED is a protocol violation
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "rtc.remote auth error — failing closed");
            FailAndClose();
        }
    }

    private void FailAndClose()
    {
        try { _control?.send("{\"t\":\"auth-fail\"}"); } catch { /* best effort */ }
        Close();
    }

    private static string Json(string t, string field, byte[] value)
        => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["t"] = t,
            [field] = Convert.ToBase64String(value),
        });
}
