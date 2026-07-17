# Standalone fork — modifications to OpenHPSDR Zeus 0.10.9

This tree is Zeus **0.10.9** (`develop`, CHANGELOG top entry `[0.10.9] — 2026-07-05`),
obtained from the last publicly reachable repository
(`github.com/brianbruff/openhpsdr-zeus`, branch `develop`), with local
modifications applied.

Licence unchanged: **GNU GPL v2 or later**. Upstream copyright headers and the
`SPDX-License-Identifier: GPL-2.0-or-later` lines are retained throughout. These
changes are made under GPL §2 and are documented here per §2(a).

---

## Design principle

**Remove the dependency on project infrastructure — not the features.**

Every capability Zeus has remains available. What changes is that nothing is
*blocked by*, or *pointed only at*, hosts under `*.openhpsdrzeus.com`. Where a
feature needs a server, that server is now configurable and its source is in this
tree (`cloud/zeus-remote-broker/`).

---

## Upstream host inventory

| Host / path | Purpose | Status here |
|---|---|---|
| `remote.openhpsdrzeus.com/users/session`, `/billing/checkout` | account gate + billing | not called (standalone) |
| `remote.openhpsdrzeus.com/users/heartbeat` | user-directory telemetry | not registered |
| `wss://remote.openhpsdrzeus.com/signal` | remote-access broker | **still used — override with `ZEUS_REMOTE_BROKER_URL`** |
| `downloads.openhpsdrzeus.com/latest.json` | update manifest | on-demand only; override with `ZEUS_UPDATE_MANIFEST_URL` |
| `openhpsdrzeus.com/download` | force-update wall target | wall disabled (standalone) |
| `openhpsdrzeus.com/go/<callsign>` | remote QR address | override with `ZEUS_REMOTE_ORIGIN` |

Third-party services (QRZ, ClubLog, LoTW, POTA/SOTA, DXSummit, DX cluster) are
independent of Zeus infrastructure, still work, and are **untouched**.

---

## What changed (6 files)

### 1. `Zeus.Server.Hosting/ZeusStandalone.cs` — NEW
Single switch, `ZEUS_STANDALONE`, **defaulting to enabled**. Set
`ZEUS_STANDALONE=0` / `off` / `false` to restore upstream behaviour.

### 2. `Zeus.Server.Hosting/UserManagementStore.cs` — MODIFIED
`GetSession()` returns `AccessAllowed: true` for an unauthenticated local
session (`DenialReason: null`).

Fixes: `App.tsx:1170` replacing the console with `<QrzAccessGate/>`, and
`ZeusEndpoints.cs:37` middleware returning 403 on protected routes while calling
`RevokeActiveRadioAccessAsync()` — which forces `TrySetMox(false)` /
`TrySetTun(false)`, i.e. disables the transmitter.

`IsAdmin` is **deliberately unchanged** — this grants operation, not privilege.
`/api/admin/*` still requires a real admin record.

### 3. `Zeus.Server.Hosting/RemoteUserAccessClient.cs` — MODIFIED
`Enabled` forced false when standalone: `/users/session` and `/billing/checkout`
are never called, so the local session stays authoritative.

### 4. `Zeus.Server.Hosting/ZeusHost.cs` — MODIFIED
`UserDirectoryReporter` (pure telemetry heartbeat) is not registered when
standalone.

`RemoteBrokerClient` is **left registered** — see "Remote operation" below.

### 5. `Zeus.Server.Hosting/RepoUpdateService.ReleaseGuards.cs` — MODIFIED
`ApplyStartupPolicy()` no longer produces a blocking force-update when standalone.

This one matters. `EvaluateVersionFloor()` stores a local version high-water mark;
running a build older than one previously run on the same machine sets
`ForceReason = "downgrade"`, and `App.tsx:1156` then renders `<ForceUpdateGate/>`
over the whole app — *before* the account gate — directing the operator to
`openhpsdrzeus.com/download` with no way past it. **Installing this 0.10.9 build on
a machine that has run 0.11.0 would trip exactly that**, and with the download host
gone the wall is unrecoverable.

Update *checking* is unaffected: status is still reported and the Updates panel
still works against a reachable manifest (`ZEUS_UPDATE_MANIFEST_URL`). Only the
blocking wall is declined.

### 6. `Zeus.Server.Hosting/Remote/RemoteQr.cs` — MODIFIED
`BrokerOrigin` was `const "https://openhpsdrzeus.com"`. Now reads
`ZEUS_REMOTE_ORIGIN`, falling back to `DefaultBrokerOrigin` (unchanged default, so
`RemoteQrTests` still passes).

---

## Remote operation — preserved, and self-hostable

Remote operation is **not disabled**. `RemoteBrokerClient` stays registered and
behaves exactly as upstream. It is already inert until you set a session password
(`if (!_passwords.HasPassword() && !_availability.IsAvailable) → delay`), so an
unreachable default broker costs nothing at idle.

To run remote access without upstream infrastructure, deploy your own broker —
the source is in this tree:

    cd cloud/zeus-remote-broker
    npm install
    npx wrangler deploy          # Cloudflare Worker + Durable Object

Then point Zeus at it:

    ZEUS_REMOTE_BROKER_URL="wss://your-broker.example.com/signal?role=host"
    ZEUS_REMOTE_ORIGIN="https://your-remote-client.example.com"

The broker only relays WebRTC signaling and mints TURN credentials — **media flows
peer-to-peer and never touches the Worker**, and the session password (SPAKE2+,
ADR-0008) is never seen by it. Note the broker's *host* side is QRZ-gated upstream
(proving the callsign belongs to you); since you now own the broker, that policy is
yours to keep or change in `cloud/zeus-remote-broker/src/`.

The browser-side defaults also live in `zeus-web/src/remote/connect.ts`
(`DEFAULT_BROKER`) and `zeus-web/src/components/ServerUrlPanel.tsx`
(`REMOTE_GO_ORIGIN`) — change those if you self-host the web client too.

---

## What was deliberately NOT changed

* **Third-party ham services** — QRZ, ClubLog, LoTW, POTA/SOTA, DXSummit, cluster.
  QRZ login remains available and optional (callsign lookup, logbook upload).
* **Hosted services** — operator chat relay, plugin checkout/billing are enforced
  server-side and are unaffected by client changes. A QRZ requirement for a
  licensed-operator chat relay is legitimate; sign in normally if you want chat.
* **Admin routes, plugin entitlement logic, DSP, protocol code** — untouched.

---

## Build

Prerequisites: **.NET 10 SDK**, **Node.js 20+** (the host `.csproj` runs
`npm ci && npm run build` for `zeus-web` automatically).

Cross-publish for a Raspberry Pi (arm64):

    dotnet publish OpenhpsdrZeus/OpenhpsdrZeus.csproj \
      -c Release -r linux-arm64 --self-contained true -o publish/pi

On the Pi (64-bit Raspberry Pi OS / Debian):

    sudo apt install -y libfftw3-double3     # libwdsp.so needs libfftw3.so.3
    cd ~/zeus && chmod +x OpenhpsdrZeus
    ZEUS_PORT=6060 ./OpenhpsdrZeus           # service mode; browse http://localhost:6060

* The prebuilt aarch64 `libwdsp.so` is vendored in
  `Zeus.Dsp/runtimes/linux-arm64/native/` — no C toolchain or CMake needed.
* Use service mode (not `--desktop`) on the Pi; audio then plays via the browser.
* LAN access: `ASPNETCORE_URLS=http://0.0.0.0:6060 ./OpenhpsdrZeus`

## Known test impact

`tests/Zeus.Server.Tests/UserAccessGateEndpointTests.cs` asserts the original
blocking behaviour and is expected to fail. That is the change working as
intended. `RemoteQrTests` still passes (default origin unchanged).

## Not verified here

**This tree has not been compiled** — it was patched in an environment without the
.NET 10 SDK. The edits are small and local, but the first build is unverified.

Also still unchecked: whether 0.10.9 carries the first-launch agreement gates
(Experimental Software Agreement / FT8 control-operator acknowledgement /
crash-log consent) seen around the `0.10.9-dryrun` builds. Those are separate
walls from the ones addressed here — grep before assuming you are clear.

## If you distribute binaries from this tree

GPL §3 applies to you as it did upstream: ship the corresponding source, or a
written offer for it. Keep the SPDX headers and existing copyright lines
(Keating EI6LF / Cerrato KB2UKA / Suarez N9WAR and contributors); add your own for
new work, and note your changes.
