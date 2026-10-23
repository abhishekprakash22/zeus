# Zeus Operator's Manual

**For the ANAN G2 / G2 Ultra and Zeus on the desktop · current through v0.15.193**

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
  hardware, and brings it back after,
- **restarts** it once, by itself, if the G2's front panel hasn't come
  alive a few seconds into a session — the connection blips for a moment
  and returns with the panel working. (If you run p2app yourself, Zeus
  won't touch it; the log tells you to restart it instead.)

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
- **Tuning loupe (touch):** press and hold a finger still on the panadapter
  or waterfall for about a third of a second and a 4× magnifier opens above
  your finger, so the signal you are aiming at is never hidden under it. The
  magnifier shows the trace and recent waterfall, where your passband sits
  now, and where it will land. Slide to aim — nothing changes while you
  aim — and lift to tune there, on your tuning-step grid (STEP). To back out,
  slide well off the display (off its sides, or about a fingertip above or
  below it) until the readout says *Release to cancel*, then lift. A second
  finger also cancels. Moving before the hold opens is an ordinary drag, as
  always; the loupe never opens with a mouse or while the VFO is locked.
  It is drawn in your own panadapter trace colour and waterfall palette,
  on the same dB ranges as the display under it.
- **Display effects (Settings → Display → EFFECTS):** Off / **Subtle**
  (default) / Full, remembered by the radio. Subtle: the trace and fill take
  the waterfall palette's colour per signal (noise keeps your trace colour),
  the waterfall dims outside the receive filter, and the VFO digits roll
  like an odometer. Full adds phosphor persistence on the panadapter —
  steady carriers burn in, bursts glow and fade within half a second, and a
  clear gap keeps the live trace crisp on top of its glow; the
  noise floor itself leaves no glow, so weak signals are never buried; it
  restarts whenever you tune, zoom or change the dB range. The filter
  displays follow the same setting. Reduced-motion devices never roll the
  digits.
- **Waterfall speed (SPD, G2 panes):** the SPD pill cycles a receiver's
  waterfall speed ×½ → ×1 → ×2 → ×4 and is remembered per receiver by the
  radio, so it survives restarts.
- **Modes:** LSB / USB / CW / AM / FM / DIGI, with per-mode filter presets
  and adjustable edges.
- **AGC** (with AGC-T gain), **squelch**, **attenuation** (S-ATT can also run
  predictively), **NR** (classic, RNNoise NR3, spectral NR4, neural NR5), **NB**, notch.
- **NR5 in CW (WDSP 2.11):** NR5's Standard / Premium choice carries into
  CW, where the CW-trained model of the same grade runs automatically
  (Settings → NR5 *In use* shows "CW Standard" / "CW Premium"). With a CW
  model running, NR/NR2/ANF/SNB, the RX EQ and pre-AGC NR3/NR4 are bypassed
  ahead of the demodulator by design; switch NR5 off to get them back.
- **Multi-RX:** additional receivers share the panadapter; each has its own
  audio and settings.
- **Diversity:** two-antenna phasing pad for null steering (hardware
  permitting).
- **Front-panel controls (G2 / G2 Ultra):** enable *Front Panel* in
  Settings → Radio and the physical knobs, buttons, and LEDs work — both
  when Zeus talks to the panel directly and when you're connected through
  the radio's own p2app row (the panel's events are relayed automatically;
  the settings card shows which path is live).
- **Remapping the panel:** the *Button & encoder mapping* grid on the same
  card lets you reassign any button or encoder. Press a control on the
  physical panel and its row flashes in the grid (press-to-identify) — this
  also surfaces buttons the shipped map doesn't know, which then appear with
  an *assign* picker. That is how you bind the panel's **PS** button: press
  it, watch the detected row appear, and assign **TogglePureSignal**.
  PureSignal is never armed by any default — only a mapping you create can
  reach it. **MOX** and **TUNE** are pinned and cannot be remapped (a
  transmitter key stays where the legend says it is), and the main VFO knob
  is always the VFO. *Reset all* returns the whole panel to the shipped
  defaults; changes take effect on the very next press, no restart.
- **VFO encoder divide:** on the same card, *Auto* derives the dial divisor
  from the selected tuning step and keeps the panel's acceleration curve, so
  a fast spin cruises across the band (the shipped behaviour). A fixed value
  works like piHPSDR's VFO encoder divisor — that many panel ticks per VFO
  step — and switches the dial to mechanically linear tuning: the
  acceleration curve is bypassed, so the rate never runs away with rotation
  speed and each step lands exactly where a detent-feel dial should. The
  on-screen VFO numerals follow the knob live (streamed, not polled), on
  every connected client at once — and so does every other panel-driven
  value: AF, AGC, drive, filter edges, attenuation, and RIT track their
  encoders as you turn them.
- **The MULTI encoder** works the way Thetis and piHPSDR taught your
  fingers: **press** it once and the knob *chooses* the function — the badge
  at the top-right of the main screen grows and goes solid accent, and each
  detent steps through the assignable list (RX1/RX2 AF, AGC, attenuation,
  filter edges, RIT, XIT, drive, diversity gain/phase — wrapping in both
  directions). **Press again** and the knob *operates* the chosen function.
  Idling five seconds in select mode drops back to operate on its own. The
  badge is always on the main screen, flashing whenever the assignment
  changes, so the knob is never a mystery.

---

### The G2 touch drawer (8-inch front glass)

Settings → Display → **G2 touch drawer** swaps the desktop transport bar for
a two-deck keyboard while connected. The slim top strip holds the page
tabs — **BAND**, **MODE**, **FILTER**, **NB·NR**, **RADIO**, **DISPLAY**,
(the DISPLAY performance sheet also carries the spectrum-resolution knob
described below),
**TX** — each opening its bottom sheet for the focused receiver (the active
tab wears an accent underline). The full-height row below is the key row:
**MOX** is fixed on the left and **REC** fixed past the divider on the
right; between them sit four operator-chosen keys. **NEXT** cycles three
sets of four, and the **✎** key opens a picker on each slot. One key lives
in exactly one slot across all three sets — picking a key that already
lives somewhere else swaps the two (the picker marks such keys with
`⇄ set-number`), so duplicates cannot be created and no slot is ever
blank. The stock arrangement covers every assignable key: set 1 **TUN /
MON / PS / CTUN**, set 2 **SPLIT / RIT / DIV / MUTE** (mute follows the
focused receiver), set 3 **LOCK** (VFO lock — the same lock as the front-panel LOCK button, so either one
locks the dial, the knob and the screen, and both light together), **2TON** (two-tone test
generator — this keys the transmitter, and its key lights TX red), **CW⌁**
(the CW decoder), and **FULL SCR**. PRE joins the pool only on the one
board where the preamp bit does anything. All keys carry the same
safeguards as their desktop originals, at finger size. On the touchscreen,
**MOX**, **TUNE** and **2TON** act the moment your finger lifts, as fast as
the front-panel buttons; sliding your finger off the key before lifting
cancels the press. A compact
FWD / SWR / ALC readout (label, bar, value) sits inline in the transport
row.

**Spectrum resolution (FFT).** Settings → DISPLAY (performance) selects the
RX display analyzer FFT size: 8k, 16k (default), 32k, or 64k points. Finer
bins sharpen zoomed-in spectra — at the Saturn's 1.536 MHz display rate 16k
is about 94 Hz per bin, 64k about 23 Hz — which resolves individual carriers
in a crowded CW pileup and FT8 sub-band structure. The trade is time
resolution: a larger FFT fills more slowly, so keying smears wider on the
waterfall. That is Fourier's bargain, not a defect. The setting is shared by
both receivers and by every connected client (the analyzer is one instrument),
persists on the radio, and applies live with at most a one-frame blink.

DISPLAY also offers five G2
themes (Zeus Blue, Amber, Night Red, Phosphor, Ice) that recolor the
layout's chrome — TX red never changes, and the spectrum/waterfall keep
their own colors; NIGHT dimming composes with any theme. Band, mode, and filter taps
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
receiver's AF / AGC-T / AUTO AGC / mute controls, the STEP chip to cycle the tune
step, and a DSP status row (NR / NB / ANF / SNB, lit when engaged; the
NB·NR tab is the editor). RX2's waterfall wears the same enhanced texture as RX1's, built from
its own band. AGC-T is per receiver: RX1 and RX2 each have their own AGC-T
baseline and their own Auto-AGC-T, which tracks that receiver's own band
noise floor (Auto on RX2 keys off RX2's panadapter). The top-bar AGC slider
follows the focused receiver, like MODE and FILTER; the G2 front panel's
RX2 AGC encoder drives RX2's AGC-T only, and its value flashes on the RX2
pane whichever receiver is active. Grabbing a receiver's AGC-T slider
takes manual control of that receiver (its Auto turns off); the other
receiver is unaffected. The outer edges of every span (the receiver's filter
transition band, where out-of-span signals fold back attenuated) render
raw rather than enhanced, so the shoulders no longer bloom into bright
columns the spectrum trace doesn't show. The left rail holds
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
handle; each receiver gets its own bandwidth filter card. The card is a
read-only display of the passband: it never changes the filter, so
touch-tuning across it (or across the passband on the main panadapter)
cannot resize your bandwidth by accident. Set bandwidth deliberately with
the filter preset chips or the CUSTOM low/high Hz fields. Each receiver's
VFO flag shows RX number, frequency, mode, filter width, and a live mini
S-meter with S-point markings and an S readout (RX1 from the calibrated
meter, RX2 estimated from its spectrum), highlighted on the active
receiver. Both panes carry the dB scales with their level drags — on the spectrum
and on the waterfall, each receiver's levels fully independent — plus ZOOM and a waterfall SPEED
multiplier docked bottom-right — each receiver zooms and scrolls
independently. The flag S-meters carry a peak-hold tick, and the S-meter
and filter cards can be closed with ✕ (restore pills appear top-right).
Drag the small wedge at a flag's bottom-right corner to enlarge it, up to
twice its size; everything on the flag grows together, both flags follow,
and the radio remembers the size. The S-METER card's corner handle resizes
the meter, and its S / PO / SWR readouts grow with the card.
The S-meters ignore the receiver while you transmit (MOX, TUNE or 2TON)
and for half a second after you unkey, so your own carrier never swings
the needle or the peak-hold tick on the way back to receive. The analog
meter's needle jumps straight to the new reading when it changes over
between S (receive) and power (transmit), instead of gliding from where
the other scale left it.
On a keyboard, hold **Space** to transmit; release to return to receive.
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
  The calibrator (PureSignal 3, WDSP 2.1) reports its own verdicts: the PS
  popover shows accepted vs attempted fits, and if the chain is driven into
  severe compression PS **refuses to calibrate** and the PURESIGNAL settings
  tab shows an over-drive banner — that is the calibrator protecting you from
  a meaningless fit, not a fault. Lower drive (or raise Feedback atten) until
  fits complete. Two-tone IMD reading a few dB shy of the old PS2 close-in,
  with equal-or-better real-speech splatter, is documented PS3 behaviour.
  The **Amp view** card at the bottom of the PURESIGNAL tab draws the
  amplifier as the calibrator measured it — gain and phase against input
  amplitude — with the correction curves it is applying; a flat gain trace is
  a linear amp, sag at the right edge is the compression PS is straightening.
- **Phase rotator auto-cal (WDSP 2.1):** in the TX fidelity panel, the
  rotator's **Auto** box lets WDSP find the corner itself — it measures the
  voice waveform's positive/negative asymmetry after the rotator and walks
  the corner until it is minimized. A status line shows SEARCHING (with the
  current step) or CONVERGED, the live corner, and IN → OUT asymmetry;
  **Restart search** re-arms it after a mic or processing change. With Auto
  on, the Corner box is read-only and tracks the search.
- **Mic sources:** the G2's **front-panel mic** is first-class — no PC audio
  involved. A browser mic (headset on a remote PC) also works. The "mic"
  status chip reports whichever source is actually in use.
- **Mic PTT wiring:** Settings → audio — *Mic PTT* enables/disables the mic
  switch keying the radio; *PTT on Tip* swaps tip/ring for non-Apache-wired
  mics. Both persist across restarts.

- **PA guards:** Zeus drops TX automatically when SWR reads above 2.5:1 on
  MOX (6:1 on TUN) for half a second, and when a single transmission runs
  past the **TX Timeout** (default 120 s). Both live in Settings → TX.
  **SWR Protection** can be switched off for a bridge reading you distrust
  or a rough load you are matching through an external tuner — with it off
  nothing but the timeout protects the PA; a red "PA unguarded" tag reminds
  you. It is a desk-only control: a remote session cannot change it.

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
- **FreeDV 700D/E** is built into the core — no external apps. It decodes
  on one receiver at a time — RX1 or RX2, whichever is in FreeDV (with both,
  the first one keeps it) — and the other receiver keeps playing normally.
  FreeDV transmit follows RX1.
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

The radio runs **one install at a time**, no matter how many screens are
open. If an install is already running — started from the front panel, a
LAN PC, anywhere — every other screen's update prompt switches to showing
that install's live progress instead of offering a second button, and every
screen reloads itself when the radio comes back. Pressing an update button
whose offer has gone stale (the update was already installed from another
screen) reports "Already up to date" and does nothing.

The blue **reload toast** ("A new version of ANAN Core is ready") only
reloads the page — it never installs anything. If the radio is mid-restart
when it's pressed, the toast waits for the radio to come back and then
reloads on its own; it no longer sits on UPDATING forever.

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

## 12. Remote operation

Zeus Remote lets you operate the radio from a phone or PC anywhere with
internet. Set a session password under Settings → SERVER and save your
callsign; then open `https://ananremote.com/go/<CALLSIGN>` and enter the
password. Both the radio and your browser dial *out* — no port forwarding,
and it works behind carrier-grade NAT (a relay is used automatically when a
direct path can't be found).

**Link quality chip.** Once connected, a small chip sits in the top-right
corner: signal bars (green good / amber fair / red poor), round-trip time in
ms, RX-audio packet loss over the last second, and the spectrum frame rate
actually arriving. `RELAY` lights when the session is riding the TURN relay.
Hover it for jitter and path details. Expect roughly: good = under 150 ms
and lossless; fair = up to 400 ms or occasional loss; poor = anything worse,
at which point the display will drop frames before the audio does.

**TX safety on a broken link.** A remote key-down is a *lease*, not a
latch. Your browser pulses the radio four times a second; if the pulses stop
while the radio is keyed by the remote session (MOX, TUN, or a CW message
sent from the remote), the radio un-keys itself within about 1.5 seconds —
it does not wait for the network stack to decide the link is dead. A session
that goes completely silent for 30 seconds is torn down. On top of this the
ordinary TX timeout (Settings → TX, default 120 s) still applies to every
transmission, remote or local; leave it enabled when operating remotely.
If the link drops mid-over, your client returns to the password prompt with
MOX cleared — reconnecting never re-asserts a stale key-down.

**One operator at a time.** Only one remote session can be unlocked; a
second correct-password connection is told "Another operator is connected"
and refused. The slot frees the moment the first session closes (worst
case ~30 s if it vanished without disconnecting). The radio's own screen is
never part of this — the desk always works.

**Password lockout.** Five failed password attempts within ten minutes
lock remote unlocking for five minutes. The client shows how long to wait.

**Your callsign is yours.** The first time this radio connects to the
broker it claims its callsign with a private key it generates and keeps;
another radio trying to register the same callsign is refused. If you
rebuild the radio and the key is lost, the old claim lapses after about
sixty days offline and the fresh radio claims it again. The callsign card
in Settings → SERVER shows the claim state after every save: available,
claimed by this radio, or already taken — in which case save a variant
(e.g. CALL/2) and share that address instead.

**TX source hint.** Keying MOX remotely while the radio's TX audio input is
armed to a radio jack (front-panel mic, line-in, or XLR) shows a warning
chip: your remote voice is deliberately NOT reaching the air, because the TX
audio ingest is single-select — one source only, never a mix. Switch the TX
audio input to **Host** in the radio's audio settings to transmit the
browser mic. The chip is advisory and dismissable; it never changes the
source for you.

**Connect progress and timeouts.** The connect flow reports its stage live
(contacting the broker, reaching the radio, negotiating a media path,
authenticating) and gives up with a named reason after 45 seconds instead of
sitting on "Connecting" forever. Password validation happens on the secured
channel AFTER a media path exists — so a stall at "negotiating a media path"
is the network, not the password.

**Remember on this device.** The connect dialog can remember the session
password on that device for 7 days (opt-in, per callsign, cleared by a wrong
password). The tradeoff is yours to weigh: whoever holds that device can key
your transmitter.

**Remote shutdown guard.** ⏻ SHUT DOWN from a remote session demands the
session password re-typed under an explicit warning — powering off remotely
means nobody without physical access can switch the radio back on.

**Earlier versions.** Settings → UPDATES → EARLIER VERSIONS (at the
radio): ROLL BACK TO the version the last update replaced (kept on the
radio — instant, offline), or SHOW EARLIER VERSIONS and INSTALL any recent
release. Confirmed first; same verified install and watchdog as an update
(a build that doesn't come up returns you to the previous one); no
"newer version" lock-out afterwards.

**Filter display views.** Each filter display can show Passband (classic),
Audio spectrum, Scope, Audio waterfall, CW pitch (CW only — zero-beat aid)
or Levels. Pick with the chip in its corner, swipe an audio view, wheel over
the page dots, or the FLT VIEW deck key (press = next, hold = Passband). Per
receiver, remembered on the radio; while keyed the audio views show TX audio
(chip says TX). Computed on the radio only while shown, so they work on the
G2, a PC and remote alike.

**Light theme (paper) and display backgrounds.** Settings → Theme → Light
turns the chassis and cards to one grey you pick (Silver to Graphite, a
Lightness slider, cool / neutral / warm Tint; text darkens automatically
to stay readable). The spectrum displays — panadapter, waterfall and the
filter displays — have their own background: Blue (default Navy; Midnight
to Steel with a Depth slider, your palette's floor lifted to match) or
Paper. In Dark, the same Depth slider runs from Black (classic, default) to
Ocean. All stored on the radio.

**Speech processor (compressor + CESSB).** Settings → TX Leveling: turn
the compressor on (about 6–10 dB) for more SSB talk power; CESSB (on by
default) runs with it, trimming the peaks the filters push back up, so the
average rises without extra splatter. CESSB only runs while the compressor
runs, stays active with PureSignal, follows the TX filter width, and is
bypassed with the compressor in digital modes. (Earlier versions ran CESSB
alone and dropped it under PureSignal — turn the compressor on to get that
talk power back.)

**Check my settings.** Settings → TROUBLESHOOT (or the ASK drawer's
Check my settings link): pick the problem — no RX audio, weak signals, low
TX power, TX audio, PureSignal, won't connect, remote can't connect or is
choppy, FT8/digital — and the radio lists the settings and conditions that
commonly cause it, each with its value, a LIKELY CAUSE / CHECK / OK verdict
and the fix. Runs on the radio with no AI; also included in every Report a
problem and used first by the assistant.

**Ask ANAN Core (built-in assistant).** Off by default; turn it on at the
radio in Settings → Assistant with your own Anthropic API key or a model
server on your network. Open it with the ASK deck key (add it with ✎) or
Settings → Assistant → Open assistant. It answers from the manual and the
radio's live state, shows the lookups it made and the manual sections it
used, and can only read — it cannot change settings or transmit. Sent: your
question, a state snapshot and redacted log lines; never audio, IQ,
passwords or your remote key. Its settings (and key) can be changed only at
the radio; asking works over remote too.

**Maintenance is done at the radio.** A remote session can see versions,
progress and "update available", but it cannot install an ANAN Core update,
update, stop or start p2app, flash or compare FPGA gateware (Saturn or HL2),
or use the native PCIe (XDMA) bench controls. Each of these can take away the
link you would need to recover — an update restarts the program carrying your
session, a failed p2app build leaves the radio deaf, and gateware recovery
needs a power cycle at the radio. Those screens say *Done at the radio* in
place of the buttons, and the radio refuses the requests over remote even if
they are sent some other way.

## G2-1K amplifier protection (Ganymede)

On a G2-1K, the amplifier's protection controller trips in hardware —
bias and PTT are dropped within microseconds of excessive reverse power,
drain current, PSU voltage, heatsink temperature, or forward power — and
reports the event to Zeus over its CAT link. Zeus removes drive
immediately (same path as the SWR trip) and shows an amber banner
explaining the fault in plain language — what happened and what to do
(e.g. "High reflected power — check the antenna and feedline") — on the
radio and on every connected client including remote. Technical detail
(the raw trip flags) stays in the log and `/api/amp/status`.
When the fault is cleared, press **RESET AMP** on the banner (or the
controller's own front-panel reset). If the fault is still present the
controller trips again immediately — that is the protection working.

The controller is found automatically on the radio's USB serial ports
(it identifies itself; nothing to configure). To pin a specific port,
set `ZEUS_GANYMEDE_PORT=/dev/ttyACM1` in the service environment.

**Bandwidth adaptation.** On a struggling link the spectrum frame rate
backs off automatically (down to ~4 fps) and climbs back to ~20 fps once
the link clears; audio always takes priority and is unaffected.

## 13. Shutting down and exiting

- **⏻ SHUT DOWN** (header): tap, then confirm **SURE?** within ten seconds.
  Refused while transmitting. Powers the Pi down cleanly.
- **Exit** (header): closes Zeus and stops everything it manages, including
  p2app. Any active connection drops.

---

## 14. Troubleshooting

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

## 15. Version and provenance

Zeus is a GPL fork in the OpenHPSDR lineage. The FPGA keyer, Saturn register
maps, p2app, and gateware come from the OpenHPSDR/Saturn community — see
ATTRIBUTIONS.md. This manual is maintained in-repo (`docs/`) and is updated
alongside the features it describes; the version line at the top states how
far it is current.

## 16. Automated PA gain calibration

> **Temporarily unavailable in this build.** The calibration wizard and
> factory run are shelved for now and will return in a future release. Any
> calibration already stored on your radio continues to apply to TX exactly
> as before.

The PA panel's **Calibrate — current band** card sets each band's PA gain
automatically so that 100% drive delivers your radio's rated output (100 W
on a G2, 1000 W on a G2-1K — the target comes from the selected board
profile's Rated PA Output).

**Before you start:** connect a dummy load rated for the radio's full
output, tune into the band you want to calibrate, and set **TUN drive
low** (20–30%). The calibration measures at your reduced drive and
normalizes mathematically to 100%, so the load only ever sees a fraction
of rated power.

**To calibrate:** tick the dummy-load confirmation, press **MEASURE THIS
BAND**, and listen for up to three short TUN bursts about a second apart.
The table shows the band's previous gain, the measured power (normalized
to 100% drive), the new gain, and the result — typically "calibrated in
2 passes — within ±3%". Tune to the next band and repeat.

**Factory mode — Measure all bands:** with the load confirmed, this
button calibrates every band in one run: the radio retunes to each
band's center in turn (and verifies it landed there before any burst
keys), measures, applies, and moves on with cool-down gaps between
bands. Any fault — high SWR, a non-responding band, a failed retune —
stops the whole run with the table showing exactly where. About two
minutes for 160m–6m at reduced drive. The single-band button (which
names the band the radio is currently on) remains for spot checks.

**Safety:** the burst unkeys immediately if SWR exceeds 1.5 (check the
load), if forward power does not respond (check PA enable and the TX
path), or when you press **Abort**. **Revert session** restores every
gain the session changed to its previous value. The calibration never
changes bands or moves your drive sliders.

## 17. SWR analyzer

> **Temporarily unavailable in this build.** The sweep tool is shelved for
> now and will return in a future release. The live SWR reading on the TX
> meter and SWR protection are unaffected.

The PA panel's **SWR analyzer** card sweeps a low-power carrier across one
amateur band and plots SWR against frequency from the radio's own
directional bridge — resonance dip, minimum SWR, and the 2:1 bandwidth,
like a standalone antenna analyzer, using the transmitter you already own.

**Setup:** connect the antenna (or load) under test, and set **TUN drive
for roughly 2–5 watts** of forward power — the bridge suppresses SWR
readings below about 2 W, and more power buys nothing. SWR protection can
stay on; it may fold drive back into a bad mismatch, which changes forward
power but not the SWR reading.

**To sweep:** tick the antenna-connected confirmation, pick a band (or
leave "Current band"), and press **Run sweep**. The carrier keys once and
the VFO steps across the band — 15–25 seconds — drawing the curve live.
The marker shows the minimum SWR and its frequency; dashed verticals and
the caption show the 2:1 span. When you sweep the same band again, the
previous curve overlays as a dashed trace — trim the antenna, re-sweep,
and watch the dip move. **Abort** drops the carrier instantly, and the VFO
always returns to where you left it.

**Boundaries:** sweeps are in-band only — a licensed transmitter cannot
sweep between allocations the way a milliwatt hardware analyzer does. The
reading is scalar SWR from a directional coupler; it does not measure
complex impedance.

