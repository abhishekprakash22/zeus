## Remote operation (ananremote.com)

ANAN Core Remote lets you operate the radio from a phone or PC anywhere with internet. Both ends dial **out** — no port forwarding — and it works behind carrier-grade NAT (a TURN relay is used automatically when no direct path exists).

**Set up (once, at the radio):** Settings → **SERVER** — save your **callsign** and set a **session password**. The password *is* the enable switch: with no password set, remote is off. Restart (or drop the broker socket) after changing the callsign. Then, from anywhere: open `https://ananremote.com/go/<CALLSIGN>` (a QR code for the address is shown in the SERVER tab) and enter the password.

- **Your callsign is yours.** On first connect the radio claims its callsign at the broker with a private key it generates and keeps; another radio trying the same callsign is refused. The callsign card shows the claim state after every save — available / claimed by this radio / already taken (save a variant like CALL/2 and share that). A lost key's claim lapses after ~60 days offline.
- **One operator at a time.** A second correct-password connection is told "Another operator is connected." The slot frees the moment the first session closes (worst case ~30 s). The radio's own screen is never part of this — the desk always works.
- **Password lockout:** five failed attempts within ten minutes lock unlocking for five minutes; the client shows the wait.
- **Remember on this device:** opt-in, per callsign, 7 days, cleared by a wrong password. Weigh it: whoever holds the device can key your transmitter.
- **TX safety on a broken link:** a remote key-down is a **lease**, not a latch. The browser pulses the radio four times a second; if pulses stop while the radio is remotely keyed (MOX, TUN, or a remote CW message), the radio un-keys within ~1.5 s. A fully silent session tears down after 30 s. The ordinary TX timeout still applies on top. Reconnecting never re-asserts a stale key-down.
- **Link chip:** top-right — signal bars, RTT, RX-audio loss, arriving spectrum fps, and RELAY when riding TURN; hover for jitter and path detail. Good ≈ under 150 ms lossless; fair ≈ up to 400 ms; on a struggling link the spectrum backs off (to ~4 fps, recovering to ~20) — audio always wins.
- **TX source hint:** keying MOX remotely while the radio's TX audio input is armed to a radio jack shows an advisory chip — your remote voice is deliberately NOT on the air, because TX ingest is single-select. Switch the TX audio source to **Host** (RADIO tab) to transmit the browser mic.
- **MON over remote** works: monitor your processed TX audio on the remote end.
- **Connect progress:** the flow reports its stage live (broker → radio → media path → authenticating) and gives up with a named reason after 45 s. Password validation happens on the secured channel *after* a media path exists — a stall at "negotiating a media path" is the network, not the password.
- **Remote shutdown guard:** ⏻ SHUT DOWN from a remote session demands the session password re-typed under an explicit warning — powering off remotely means nobody without physical access can power back on.
