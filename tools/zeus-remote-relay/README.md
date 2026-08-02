# zeus-remote-relay — self-hosted remote operation, no cloud, no QRZ

Run Zeus remote operation entirely on infrastructure you own. This relay
replaces `remote.openhpsdrzeus.com` for signaling rendezvous; the actual
audio/spectrum/control traffic is **peer-to-peer WebRTC and never touches the
relay**. Security is unchanged from the public design (ADR-0007/0008): the
relay is untrusted plumbing, and the radio's SPAKE2+ session password is the
sole authenticator — deny-by-default until it proves out end-to-end.

## 1. Run the relay (any always-reachable box: VPS, home server, LAN edge)

    cd tools/zeus-remote-relay
    npm install            # single dependency: ws
    node server.mjs        # listens on :8787 (PORT env to change)

Options (env):
- `RELAY_TOKEN=secret` — require `&k=secret` on both ends (scanner hygiene;
  not security — SPAKE2+ is the security).
- `TURN_JSON='{"iceServers":[{"urls":"turn:...","username":"..","credential":".."}]}'`
  — serve TURN creds (your own coturn) for CGNAT cases. Omitted → clients
  fall back to public STUN, which suffices for most home NATs.
- `--ui /path/to/wwwroot` — also serve a zeus-web build, so
  `https://relay/` IS the remote client page.

Put it behind TLS (Caddy/nginx or a Cloudflare-tunnel-free reverse proxy of
your choice) — browsers require `wss://` for non-localhost WebSocket +
getUserMedia for the mic path.

## 2. Point the radio at it

On the Pi (systemd unit, ~/.profile, or the launch script):

    export ZEUS_REMOTE_BROKER_URL="wss://relay.example.com/signal?role=host"
    export ZEUS_REMOTE_CALLSIGN="VU2XYZ"     # QRZ-free identity for self-hosted relays

Then set a **remote session password** in Zeus (Settings → Remote) — the host
socket only comes online once a password exists, and nothing unlocks without
it. Log line to expect: `remote broker: host online for VU2XYZ`.

(`RELAY_TOKEN` in use? Append `&k=secret` to the URL.)

## 3. Connect from anywhere

Open the remote client with the broker override:

    https://relay.example.com/?broker=https://relay.example.com   (if --ui)
    — or any hosted zeus-web build with ?broker=https://relay.example.com

Enter callsign + the session password. Offer/answer flows through your relay
once; after that the session is direct P2P (or via your TURN, if configured).

## Protocol (for the curious / for reimplementers)

- Host: persistent WS `/signal?role=host` (callsign via `X-QRZ-Callsign`
  header or `&callsign=`). Receives `{t:"offer",sdp}`, replies
  `{t:"answer",sdp}`. One socket per callsign; a new one replaces the old.
- Client: WS `/signal?role=client&callsign=CALL`, send `{t:"offer",sdp}`,
  await `{t:"answer",sdp}` or `{t:"offline"}`. 25 s timeout per attempt.
- Offers are serialized per host (the radio answers sequentially); no
  correlation ids on the wire. `POST /turn` → `{iceServers:[...]}` or 404.

Verified by `relay-e2e.test.mjs` (fake host + fake client through the real
relay: answer routing + offline signaling).
