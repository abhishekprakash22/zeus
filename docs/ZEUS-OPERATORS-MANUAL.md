# Zeus Operator's Manual

**For the ANAN G2 / G2 Ultra and Zeus on the desktop · current through v0.15.143**

Zeus is the radio's own face: an OpenHPSDR Protocol-1/2/3 client that runs on
the radio itself (the internal Pi of a G2, or an attached Pi), on the 8-inch
touch panel, and identically on any Windows / Linux / macOS PC pointed at the
same radio. One Zeus, everywhere.

This is the operator's manual. It tells you how to use the radio. The
engineering record (why things are the way they are) lives separately.

---

## 1. Quick start — the appliance flow

1. **Power on.** The radio boots into Zeus full screen.
2. **Wait ~10 seconds.** The Discover tab fills in by itself:
   - a **Saturn** row with a **Connect** button — this is your radio's data
     plane (p2app), started and watched by Zeus automatically. With an
     Ethernet cable in, the row shows the radio's LAN address; with no cable
     at all, it shows `127.0.0.1` — both are the same radio, both work.
   - a **PCIe · DETECTED** badge row — proof Zeus can see the radio on its
     own internal bus. Informational; you don't connect through it.
3. **Tap Connect.** You're on the air. No terminal, no setup, no cables
   required.

To finish a session: **Exit** (closes Zeus and everything it manages) or the
**⏻ SHUT DOWN** button (powers the radio down — tap it, then confirm **SURE?**
within ten seconds).

---

## 2. The Discover tab, explained

| Row | What it is | What to do |
|---|---|---|
| Saturn @ LAN address | Your radio via its network personality (p2app), cable in | Connect — full RX + TX |
| Saturn @ 127.0.0.1 | The same radio, internal path, no cable needed | Connect — full RX + TX |
| `PCIe · DETECTED` badge | The radio seen directly on the PCIe bus | Informational (native transport is an expert feature — §11) |
| Other Saturn/Hermes rows | Other radios on your network | Connect as usual |

Only one of the LAN / loopback rows appears at a time — with a cable the LAN
row wins; unplugged, the loopback row takes over. Discovery refreshes every
10 seconds.

**Manual tab:** connect by address (`ip:port`, port 1024 for Protocol 2) when
broadcast discovery can't reach a radio. Protocol 2 is Zeus's primary path:
full RX + TX, CW, and PureSignal.

---

## 3. p2app — managed for you

p2app is the small program that turns the Saturn board into a network radio.
**You never start or stop it yourself.** Zeus:

- **starts** it when the radio boots (and finds it whether it lives in your
  own Saturn checkout or a Zeus-managed one),
- **restarts** it within seconds if it ever crashes,
- **stops** it when you press Exit or shut down,
- **adopts** (leaves alone) a p2app you started yourself or run as a system
  service,
- **pauses** it automatically when an expert native-PCIe session needs the
  hardware, and brings it back after.

Status any time: Settings → Updates → **P2APP** section, or
`GET /api/p2app` for the curious (`Supervised`, `Adopted`, `Paused`, …).

### Updating p2app

Settings → Updates → **UPDATE P2APP**. Zeus pulls the latest source from
Laurence Barker's Saturn repository and rebuilds it on the radio (the same
steps as the official update script). During the build the Saturn row
disappears for a couple of minutes; it returns on the new version by itself.

**If the build fails, nothing is lost:** Zeus restores the previous binary
automatically and tells you — *"previous p2app restored — radio still
working."* The prior version is also kept on disk as `p2app.zeus-previous`.

---

## 4. Receiving

Connect, then operate from the workspace:

- **Tuning:** drag the waterfall, tap a signal, spin the encoder, or type a
  frequency. Band buttons remember per-band settings.
- **Modes:** LSB / USB / CW / AM / FM / DIGI, with per-mode filter presets
  and adjustable edges.
- **AGC** (with AGC-T gain), **squelch**, **attenuation** (S-ATT can also run
  predictively), **NR** (classic, RNNoise NR3, spectral NR4), **NB**, notch.
- **Multi-RX:** additional receivers share the panadapter; each has its own
  audio and settings.
- **Diversity:** two-antenna phasing pad for null steering (hardware
  permitting).
- **Front-panel controls (G2 / G2 Ultra):** enable *Front Panel* in
  Settings → Radio and the physical knobs, buttons, and LEDs work — both
  when Zeus talks to the panel directly and when you're connected through
  the radio's own p2app row (the panel's events are relayed automatically;
  the settings card shows which path is live).

---

### The G2 touch drawer (8-inch front glass)

Settings → Display → **G2 touch drawer** swaps the desktop transport bar for
a two-deck keyboard while connected. The slim top strip holds the page
tabs — **BAND**, **MODE**, **FILTER**, **NB·NR**, **RADIO**, **DISPLAY**,
**TX** — each opening its bottom sheet for the focused receiver (the active
tab wears an accent underline). The full-height row below holds the
transmitter's own controls: **MOX**, **TUN**, **MON**, **PS**, **CTUN**,
and — past a divider — **REC** (the same transport buttons, with all their
safeguards, at finger size). A compact FWD / SWR / ALC readout (label,
bar, value) sits inline in the transport row. Band, mode, and filter taps
apply to the FOCUSED receiver — tap a pane first, then pick. **NB·NR** opens the noise-blanker /
noise-reduction panel and **RADIO** the radio (antenna) settings, both as
touch sheets, **DISPLAY** the display settings, and **TX** the
transmit panel (drive, mic, bandpass). A second side button, **AUDIO
PROC**, opens the TX audio processing panel (CFC, EQ, leveler) with the
live TX stage meters. SPLIT lights a red SPLIT▸B tag on RX1's flag. The CONTROLS panel also carries a
row with SPLIT, RIT, DIV, the CW decoder toggle, and NIGHT (dims the
display for the dark shack). BAND/MODE/FILTER sheets close themselves after
a pick (PIN keeps them open).a pick (PIN keeps them open). Each pane's flag carries the real VFO
readout — scroll the wheel over a digit to step that decade, or click the
digits to type a frequency (kHz) inline — plus the **CTRL** chip for that
receiver's AF / AGC-T / mute controls, the STEP chip to cycle the tune
step, and a DSP status row (NR / NB / ANF / SNB, lit when engaged; the
NB·NR tab is the editor). RX2's waterfall wears the same enhanced texture as RX1's, built from
its own band. The left rail holds
DISC (tap to arm SURE? in red, tap again within 3 s to disconnect — the
armed button notes unsaved TX-audio edits), FULL SCR to enter or leave
browser full screen, CONTROLS, and AUDIO PROC. While the G2 layout is on,
audio follows the active receiver — including while the Settings page is
open. Pinch on
either pane zooms that receiver alone. The desktop header's control
cluster is re-homed: the **CONTROLS** button on the left edge opens the
full set (STEP, FRONT-END, AGC, SQL, AF, ROGER, VIEW...) as a touch
panel, along with the Zeus brand and the Disconnect button — the header
row itself is gone and the receiver panes take its space. Card positions and hidden-card choices persist across restarts on
this device. The keys stretch to
fill the drawer edge to edge. Receivers split the glass 50/50 when RX2
is enabled; with RX2 off, RX1 takes the whole display. Drag the bar
inside each pane to set its spectrum/waterfall ratio. The analog S-meter
(which follows the active receiver) and the bandwidth filter display
float over the panes — drag by the title strip, resize by the corner
handle; each receiver gets its own bandwidth filter card. Each receiver's
VFO flag shows RX number, frequency, mode, filter width, and a live mini
S-meter with S-point markings and an S readout (RX1 from the calibrated
meter, RX2 estimated from its spectrum), highlighted on the active
receiver. Both panes carry the dB scales with their level drags — on the spectrum
and on the waterfall, each receiver's levels fully independent — plus ZOOM and a waterfall SPEED
multiplier docked bottom-right — each receiver zooms and scrolls
independently. The flag S-meters carry a peak-hold tick, and the S-meter
and filter cards can be closed with ✕ (restore pills appear top-right).
Audio
follows the active receiver — the inactive pane is muted until you tap
it (both unmute when you leave the layout). With the drawer on, the whole workspace
wears the graphite-and-amber G2 theme, and the workspace becomes two
stacked receiver panes — RX1 over RX2, each with its own spectrum,
waterfall, and band. Tap anywhere on the inactive pane to make that
receiver active (the first tap only selects — it never tunes); the
amber flag marks the active one, and the drawer's BAND/MODE/FILTER
follow it. Drag the bar between the panes to resize. Turning the option
off restores the desktop workspace and standard theme instantly. More of the touch layout (stacked
receiver panes, on-glass filter displays) arrives in the next releases.

## 5. Transmitting

- **MOX** keys the radio; **TUN** emits a low-drive carrier for tuning.
- **DRV** sets drive. **MON** (amber, between TUN and PS) lets you hear your
  own processed TX audio — with MOX *off* it's a safe preview that transmits
  nothing.
- **PureSignal (PS)** linearizes the PA; calibrate into a dummy load first.
  Zeus remembers whether you had PS engaged and restores it next session.
- **Mic sources:** the G2's **front-panel mic** is first-class — no PC audio
  involved. A browser mic (headset on a remote PC) also works. The "mic"
  status chip reports whichever source is actually in use.
- **Mic PTT wiring:** Settings → audio — *Mic PTT* enables/disables the mic
  switch keying the radio; *PTT on Tip* swaps tip/ring for non-Apache-wired
  mics. Both persist across restarts.

**Antenna discipline is yours:** always have an antenna or dummy load on the
active port before keying.

---

## 6. CW

- **Paddle / straight key** in the G2's key jack: element timing is done by
  the radio's FPGA keyer — zero latency. Settings → CW: mode (Straight /
  Iambic A / B), WPM, Farnsworth, sidetone pitch and level.
- **CWX:** type-ahead keyboard CW from the CW panel, with macros.
- **Paddle at the Pi** (remote-head setups): a paddle wired to Pi GPIO is
  also supported, using the classic VK6PH/N1GP/DL1YCF iambic engine.
- **DeepCW:** the CW⌁ button decodes CW on-screen; **SKIM** lanes decode
  several signals across the band at once — tap a lane to tune it.

---

## 7. Digital modes

- **FT8/FT4** run natively on the radio — decode history, QSO logging with
  worked-before highlighting, WSJT-X-compatible UDP logging to N1MM+/JTAlert/
  GridTracker, TX macros.
- **FreeDV 700D/E** is built into the core — no external apps.
- **WSPR:** in-core decoding plus a transmit beacon slot.

---

## 8. Recorder, replay, voice keyer

- **Record** receive audio, your TX mic, or on-air TX to WAV files.
- **Instant replay:** the last 60 seconds are always buffered — REPLAY 10/30/60
  plays back what you just heard.
- **Voice keyer:** transmit a recorded WAV (CQ calls) with a deliberate
  two-press confirm; playback can also go through the radio's own speakers.

---

## 9. Updating Zeus

Settings → Updates shows the installed and latest production versions.
**INSTALL & RESTART** downloads, verifies, swaps atomically, and restarts —
with automatic rollback if the new version fails to come up. p2app is stopped
and restarted around the swap automatically.

If the screen looks odd right after an update, reload the page once
(Ctrl+Shift+R on a keyboard) — the browser can briefly hold the old
interface in its cache.

---

## 10. FPGA firmware (gateware)

Settings → Updates → **FPGA FIRMWARE** (appears only on the radio itself):

1. **LOAD AVAILABLE IMAGES** lists official gateware from the Saturn
   repository.
2. **CHECK AGAINST INSTALLED** compares the selected image byte-for-byte with
   what's in the radio's primary flash slot — know before you flash.
3. Type **FLASH** into the confirm field to enable the red button. Keep power
   on until it reports done, then power-cycle to run the new gateware.

**Safety by design:** Zeus writes only the *primary* slot. The factory
*golden* image is never touched — if a flash ever goes wrong, the radio
falls back to golden and boots anyway. The flasher also refuses to run while
transmitting or while the data plane is busy.

---

## 11. Expert: the native PCIe path (XDMA)

Zeus can drive the Saturn board directly over PCIe — no p2app, no network —
including RX *and* TX. This is working and field-proven, but it is an
**expert feature**, deliberately kept off the touch screen for now:

- Started via the API (`POST /api/xdma/rx/start`); Zeus pauses p2app itself
  and verifies the hardware is free before opening the register plane. When
  a native stream *is* running, the badge row shows its live rate and a STOP
  button.
- Native **TX is gated behind an arming ceremony**: transmitting requires an
  explicit `POST /api/xdma/tx/arm` with the confirm phrase
  `i-have-a-dummy-load` and a time-limited window. Unkey is always honored;
  the window disarms itself.
- **Do not put native TX on an antenna through an amplifier.** The native
  path does not yet drive the Alex low-pass filters — harmonic suppression is
  not in place. Dummy load only, until the manual says otherwise.
- Never run p2app and a native session at once. Zeus enforces this; don't
  fight it.

---

## 12. Shutting down and exiting

- **⏻ SHUT DOWN** (header): tap, then confirm **SURE?** within ten seconds.
  Refused while transmitting. Powers the Pi down cleanly.
- **Exit** (header): closes Zeus and stops everything it manages, including
  p2app. Any active connection drops.

---

## 13. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| No Saturn row on Discover | p2app not running yet | Wait one 10 s cycle; check Settings → Updates → P2APP status. `NoBinary` → run UPDATE P2APP once to install it |
| Row shows 127.0.0.1 instead of LAN address | No Ethernet cable | Normal — same radio, connect anyway |
| P2APP status says `Adopted` | A p2app you (or a system service) started is running | Fine for normal use; stop it yourself if you need native sessions or the updater |
| "UDP 1024 is owned by another process" when starting a native session | Externally-managed p2app | Stop it (`systemctl stop p2app` or kill it), retry |
| Mic PTT / PTT-on-Tip won't change | Running a version older than v0.15.102 | Update Zeus |
| UI looks stale after an update | Browser cached the old interface | Reload once (Ctrl+Shift+R) |
| p2app update failed | Build error upstream or missing tools | Read the log in the panel; the previous binary was restored automatically. Build tools needed on the radio: gcc, make, libi2c-dev, libgpiod |
| Radio keyed but hover text worried you | Fixed in v0.15.102 | P2 is full TX; update if you still see "experimental, RX only" |

---

## 14. Version and provenance

Zeus is a GPL fork in the OpenHPSDR lineage. The FPGA keyer, Saturn register
maps, p2app, and gateware come from the OpenHPSDR/Saturn community — see
ATTRIBUTIONS.md. This manual is maintained in-repo (`docs/`) and is updated
alongside the features it describes; the version line at the top states how
far it is current.
