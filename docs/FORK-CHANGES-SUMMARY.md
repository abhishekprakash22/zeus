# ANAN Core — what this fork adds to OpenHPSDR Zeus 0.10.9

Baseline: Zeus **0.10.9** (`develop`, 2026-07-05), the last publicly reachable
upstream tree. Licence unchanged — GPL-2.0-or-later, upstream copyright headers
retained.

This is the operator- and engineer-visible summary. `CHANGELOG.md` carries the
release-by-release detail; `STANDALONE-CHANGES.md` covers the infrastructure
severance specifically.

Note on upstream: the Zeus authors have since split the project — a GPL headless
station engine, with the UI going proprietary. Upstream UI merges are therefore
permanently over; the engine repo remains a cherry-pick source for protocol and
DSP fixes.

---

## 1. Independence from upstream infrastructure

- Account gate, billing, and user-directory telemetry no longer called.
- Every `*.openhpsdrzeus.com` dependency either removed or made configurable
  (`ZEUS_REMOTE_BROKER_URL`, `ZEUS_UPDATE_MANIFEST_URL`, `ZEUS_REMOTE_ORIGIN`,
  `ZEUS_REMOTE_CALLSIGN`).
- Own remote broker in-tree (`cloud/zeus-remote-broker/`), own update manifest,
  own download path. No capability was removed to achieve this.
- Force-update wall disabled for standalone operation.

## 2. Rebrand — ANAN Core

- Product renamed throughout the operator-visible surface: splash, About,
  desktop launcher, discover dialog, PWA manifest, page title, mobile and
  desktop headers, updater release names.
- Mark kept (twin square-wave pulse), wordmark ZEUS → CORE, OPENHPSDR eyebrow
  retained. Icons regenerated at 192/512 plus maskable.
- Asset and process filenames deliberately unchanged — the AppImage name is a
  self-updater path contract.

## 3. WDSP 2.10 port (from WDSP 1.29 + PureSignal 2.0)

- Full vendored upgrade to Warren's WDSP 2.10 drop, in phases, with a runbook
  and migration plan in `docs/designs/`.
- **NR5 / NNR** neural noise reduction added as an RX mode, availability-gated
  on the engine's own exports.
- NR3/NR4 re-spliced onto the new tree; psccF compatibility shim.
- Three upstream 2.x files (`extrapolate`, `nurbs_fit`, `nurbs_spline`) call
  `_aligned_malloc` with no prototype — on Linux this truncated the returned
  pointer to 32 bits and killed the first TXA open. Fixed with real symbols in
  `linux_port.c`, plus `-Wl,--no-undefined` and explicit `-lm` so the class of
  bug can't recur silently.
- VST3 build jobs removed (gitlink lost from the tree upstream).

## 4. PureSignal

- **PS MOX fix** — `SetPsMox` had no caller, so the calibrator discarded every
  feedback frame. PS never converged before this.
- **Auto-attenuate service** — escapes the hot-side deadlock automatically,
  stepping feedback attenuation while keyed, with a cause-split banner
  (tap clipping vs hardware peak).
- **Live IMD3 / IMD5 readout**, measured from the actual tone bins so carrier
  error cancels; ±400 Hz tone search, five-frame median, source pinning, and a
  floor guard that reports "—" rather than a meaningless number.
- **Over-drive verdict** — attempted-fits/refusal counters surfaced as a banner
  that outranks the stall variants.
- **AmpView** card (`GetPSDisp`) and **phase-rotation auto-cal**, which turned
  out to be UI-unreachable upstream because the web DTO never carried
  `autoMode`.

## 5. PA calibration and antenna measurement (new subsystem)

- **PA gain calibration wizard** — per-band measure against a dummy load: key
  TUN, settle, capture, compute error in dB, adjust gain, converge to ±3 %.
  Hard unkeyed aborts on SWR, no-response, or meter gaps.
- **Factory mode** — walks 160 m to 6 m unattended (~2 min, ~26 keyed
  seconds), verifying after each retune that the band actually matched before
  measuring.
- **SWR analyzer** — one keyed carrier per band sweep, 20–150 points, median
  SWR with a validity floor, live SVG curve with previous-sweep compare,
  minimum marker and 2:1 span.
- **G2-1K coupler transfer** (3.3 V @ 1 kW square-law) and the ANAN-G2-1K
  variant selector.
- **Ganymede amplifier protection** integration with a plain-language trip
  banner.

## 6. Remote access (substantially rebuilt)

- SPAKE2+ password as the sole authentication; broker untrusted; deny-by-default
  API surface; the password *is* the enable switch.
- Anti-squat host-key claim with 60-day TTL, five-strike auth throttle,
  single-operator slot with zombie-lease release.
- Own TURN credential fetch (the CGNAT cure), AIMD display pacing, per-receiver
  pacing so RX2 is no longer starved blank.
- TX lease with keepalive; remote MON; mic uplink gated on TX source.
- `/go/<CALLSIGN>` printed-link path, fixed twice in the field (placeholder
  substitution, then rewrite-vs-redirect).
- Hosted client landing page with which-radio prompt and resume chip.
- Boot RPC lifecycle rewritten — requests no longer expire in the queue while
  SPAKE2+ completes.
- **FT8 decode bridge**: the plugin's SSE stream cannot ride the fetch-based API
  tunnel, so the host opens it over loopback and forwards frames verbatim.
- Update install and the force-update gate are now radio-only, so a remote
  operator can't be locked out by a modal they cannot satisfy.

## 7. Bandwidth and rendering

- **u8 display bins** — optional one-byte-per-bin quantisation against a fixed
  −160..−20 dB range, negotiated per client so stale bundles keep float32.
  Measured: a remote viewer went 2.1 Mbit/s → 816 kbit/s.
- **GPU render-stats overlay** (`?renderstats=1`) — fps, GL span per frame,
  long-task census, upload rate, draws.
- **Shift coalescer** — one full-surface rebase per frame instead of two or
  three.
- **Dial-frame priority lane** — the hub's single bounded per-client queue was
  evicting the tiny VFO frames during a fast spin, producing dead time and
  run-on. Dial and state frames now ride a latest-wins lane ahead of bulk.

## 8. G2 front panel and 8-inch touch layout

- Full Andromeda front-panel support over `/dev/ttyAMA1` and CAT-over-TCP, with
  a serial fallback that survives p2app's tty grab.
- Remappable buttons and encoders; reserved ids mappable; MOX/TUNE pinned.
- **Multifunction encoder** rebuilt to the Thetis/piHPSDR model — press to
  choose, press to operate, idle auto-exit — with an always-visible assignment
  badge on the main screen.
- Touch drawer with three assignable key decks, a 13-key registry, duplicate
  swapping and deck sanitising.
- CTRL popover (MODE, AF, AGC-T, MIC, DRV, MUTE) with dead-space dismiss.
- Per-receiver panes, divider persistence stored on the radio rather than in the
  kiosk's throwaway browser profile.
- Card geometry clamped so a card can no longer strand itself off-screen, plus
  a reset control.

## 9. DSP and radio features

- Per-receiver AGC-T with independent Auto-AGC-T servos (Thetis behaviour).
- FreeDV 700D/700E RX and TX in core, with auto-detect scanning, SNR squelch,
  text sidechannel and clean end-of-over tails.
- Native FT8/FT4 with GFSK synthesis and a dedicated keyer service.
- WSPR, DeepCW skimmer, CFC/EQ/WBFM.
- Filter displays made read-only after a field bug where touch drag-tune resized
  the passband; bandwidth writes now only via presets, CUSTOM inputs and nudge.
- Startup sanity clamps on filter state.
- GPIO paddle keyer with a Settings → CW page.

## 10. Platform, packaging, update

- Self-update with rollback, atomic AppImage swap, and a supervised systemd
  handoff; two-part release tags parse numerically.
- In-app FPGA flashwriter — writes the PRIMARY image only, so it cannot brick
  the board; golden image sniffed and protected.
- p2app supervisor that adopts rather than kills an external instance, with a
  freshness gate so "update" reports already-up-to-date instead of churning the
  pid.
- Factory install script, kiosk hardening, keyring workaround, Windows and macOS
  release workflows.
- Operator manual: 29 chapters, built to PDF in CI, served as a static asset and
  reachable from the UI.

## 11. Native Saturn / XDMA path (exploratory)

- Direct `/dev/xdma0_user` access layer with the register map documented:
  identity, DDC/DUC frequency, rates, FIFO reset, MOX/TX enable, attenuation,
  keyer ramp.
- Native RX proven, and first native TX on a bare board.
- Remaining gaps before amplified native TX: TX frequency follow and the Alex
  word. PureSignal on the PCIe path is not implemented.

---

## Known open items

- Post-update restart reliability — the updater exits cleanly, so the
  `Restart=on-failure` unit does not bring it back; currently worked around by a
  desktop autostart entry.
- Light-theme VFO digit visibility (under diagnosis).
- Shift-as-shader-offset redesign, R8+LUT textures, half-vsync idle — the
  remaining GPU work.
- Multi-user remote access with roles — designed, not built
  (`docs/designs/remote-access-roles.md`).
- Factory defaults and user profiles — designed, not built
  (`docs/designs/factory-defaults-and-profiles.md`).
