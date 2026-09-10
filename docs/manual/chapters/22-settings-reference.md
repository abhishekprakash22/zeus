## Settings reference — every tab, every control

Settings is a full-screen workspace, not a pop-up: open it from the **gear** at the bottom of the left layout bar (or the DISPLAY drawer tab's link on the glass); any layout tab or **Esc** returns you to operating. Tabs run down the left side.

**One rule to remember:** **PA SETTINGS is the only staged tab** — it has an explicit **APPLY / CANCEL** footer, and edits reach the radio only on APPLY (CANCEL re-reads the radio and discards). Every other tab saves as you go.

### PA SETTINGS

- **Global:** **Rated PA output** (watts — 100 on a G2, 1000 on a G2-1K; comes from the board profile and feeds the power model and meters), **SWR Protection** on/off (the automatic SWR trip — see §11 for what turning it off means), **TX Timeout** (s, default 120), **TX Tail Delay** (ms, TX→RX hang), **Dis PA** (disable the PA stage entirely — exciter-level output only).
- **Per Band:** the **PA Gain** table (0–63 per band) that maps drive % to actual output per band, and the **per-band OC layer** (open-collector outputs following band changes, for amp/filter switching).
- *Calibrate — current band* and the *SWR analyzer* cards are **temporarily unavailable in this build** (shelved; returning in a future release). Any PA calibration already stored continues to apply to TX exactly as before, and the live SWR meter and protection are unaffected. Their operation, for builds that carry them: the calibration wizard sets each band's PA gain so 100 % drive delivers rated output — dummy load on, TUN drive low (20–30 %; the math normalizes to 100 %, so the load only sees a fraction), tick the confirmation, **MEASURE THIS BAND** (up to three short TUN bursts), repeat per band or run **Measure all bands** factory mode (the radio retunes each band's center, verifies it landed, measures, applies; any fault stops the run with the table showing where; ~2 minutes 160m–6m). Bursts unkey instantly on SWR > 1.5, non-responding forward power, or **Abort**; **Revert session** restores every changed gain. The SWR analyzer sweeps a 2–5 W carrier across one band and plots SWR from the radio's own bridge — minimum, 2:1 span, previous sweep overlaid for trim-and-compare; in-band only, scalar SWR.

### PURESIGNAL

Everything in §12: arm state, **Auto / Single / Run now / Reset PS state**, calibration timing (MOX/TX/amp/cal delays, iterations per pass), **Feedback** source (internal coupler / external bypass), **Feedback atten**, **Auto-attenuate**, *drop drive when feedback clips*, *relax phase tolerance*, *allow looser convergence on weak PAs*, the two-tone **Generator** frequencies, **Monitor PA output**, *Observed peak / HW peak* (with per-board default and an override for the coupler ADC ceiling), the signal-flow diagram, the live **IMD3/IMD5** readout with sparkline, and the **Amp view** card.

### AUDIO TOOLS

**Audio Devices** (input/output selection with refresh; *System default* honored; missing devices flagged), the **RX Audio** and **TX Audio** VST plugin chains, and the **Transmit tools** entry points (Audio Suite, profiles). Desktop hosts real VST3/AU; on the radio the chains list what the platform supports.

### DSP

Cards, each mapping to a Thetis-style Setup ▸ DSP family — operating controls, safe defaults loaded:

- **Bandwidth** — DDC sample rate, 48–1536 kHz.
- **WDSP Filter Architecture** — buffers, taps, window, cache (diagnostic-grade; leave alone unless you know why).
- **Filter Shape** — brick-wall vs softer skirts.
- **AGC** — mode, max-gain, custom knee shaping.
- **ADC Protection** — P2 overload / max-magnitude **auto-ATT** policy.
- **RX Squelch** — mode-aware SSB/AM/FM behaviour.
- **TX Leveling** — ALC / leveler / compressor targets.
- **Signal Intelligence** — signal **Pop**, click **Snap**, peak **Markers**.
- **Smart NR Automation** — panadapter-driven NR policy.

### CW

**KEYER** (Straight / Iambic A / B), **SPEED** (WPM), **FARNSWORTH**, **SIDETONE** (**PITCH**, **LEVEL**), *swap dot and dash contacts*, and the Pi-GPIO paddle assignments (**DOT GPIO**, **DASH GPIO**, **PADDLE ON GPIO**) for remote-head setups. The FPGA keyer handles a paddle in the radio's key jack regardless.

### BAND PLAN

The band plan editor — frequency/mode allocations per licence class, feeding the panadapter band overlay, the band-edge audio alert, and the **transmit band guard** (TX refused outside your plan).

### QRZ

QRZ.com sign-in (API key). Shows your callsign, home grid/coordinates, and whether the account has an active **XML subscription** — lookups need it. Feeds callsign lookup panels, the logbook, and the Beam Map background.

### ROTATOR

Hamlib `rotctld` client: Enabled, host, port (default 127.0.0.1), test. Drives the rotator panels and the Beam Map heading.

### MIDI

MIDI controller / Stream Deck mapping — bind hardware knobs and keys to radio controls.

### NETWORK

External-software integration: **TCI** (ExpertSDR3 Transceiver Control Interface) and **CAT** (Kenwood TS-2000 emulation over TCP, default port **19090**), each with bind address, port, and test; **CAT serial ports** for software that only speaks COM ports; the direct **DX-cluster** Telnet client; **WSJT-X UDP** (logging broadcasts to N1MM+/JTAlert/GridTracker); and **spotting** options. Both TCI and CAT are unauthenticated and CAT grants full transmit control — bind `0.0.0.0` only on a trusted network.

### DISPLAY

- **Theme** — Dark / Light with per-token color overrides (grouped by purpose, per-color Reset).
- **Panadapter Background** — Basic / **Beam Map** (needs QRZ) / **Image** (drop a photo; Fit/Fill/Stretch; auto-shrunk to 1920 px).
- **RX Trace Color**, **Spectrum Scale** (separate dB min/max for RX/TX panadapter and waterfalls), **Waterfall colormap** and scroll speed.
- **Wideband display** settings for the full-ADC spectrum.
- **Performance** — the RX display **FFT size** (8k/16k/32k/64k, §6) and display-rate options.
- **G2 touch layout** — the switch for §17, plus the five G2 themes.

### PLUGINS

Install and manage backend/UI/audio plugins from the registry or by URL — Recorder, Voyeur, RF2K-S, and more.

### HAMCLOCK

One-click local install of OpenHamClock (downloads and builds on demand; fetches a private Node.js if needed, ~30 MB), then add/remove it as a workspace panel and stop its server.

### KIWI SDR

Pick a public KiwiSDR from the map and use it as a receive source (§3).

### SPOTS

POTA / SOTA / DX-cluster spot feed: per-source enables, mode filters, hide-QRT / hide-worked, latest-per-activator, a callsign **watchlist** with audible alerts, and click-to-tune behaviour (set mode too? only when connected?).

### ZEUS DIGITAL

Install/update the native FT8/FT4 suite and its options (§14).

### SERVER

- **Server URL** for wrapped/phone clients (§20) and the **Mobile browser HTTPS** address list.
- **Remote access:** the **callsign** card (claim state: available / yours / taken), the **session password** (the remote enable switch), the **QR code** for `ananremote.com/go/<CALL>`, and the opt-in **Remote Diagnostics** switch (off by default) that lets the maintainer see anonymized diagnostics for support — *"Not visible to maintainers yet"* until you turn it on.

### RADIO

- **PTT-IN / Mic PTT** and **PTT on Tip** wiring options; **TX audio source** (front-panel mic / line / **Host** = browser-PC audio — single-select).
- **Antenna relays** per band.
- **Front Panel** enable + the **Button & encoder mapping** grid + **VFO encoder divide** (§18) + **Assume G2-Ultra**.
- **Board options** (shown only when the board has any): ANAN-G2 ADC **Dither** / **Random** linearity toggles; Hermes-Lite 2 **Band Volts**.
- The **RadioSelector** with the OrionMkII-family **variant** dropdown (plain G2 vs **ANAN-G2-1K** etc.) and detected-vs-selected **Override Detection**.

### RECEIVERS

**Exposed receiver count** (how many RX the interface offers, up to 8) and per-receiver **ADC assignment** (ADC 0 / ADC 1) for diversity and multi-antenna work.

### CALIBRATION

One-button **frequency calibration**: tunes to WWV 10.000 MHz, measures the offset on the panadapter, stores a per-radio correction (shown as offset @10 MHz and ppm); VFO/mode/filter/zoom restored afterward; **RESET** clears to factory. Radio must be connected.

### UPDATES

Installed vs latest version, platform, release notes, **INSTALL & RESTART** (on the radio) or the platform download (desktop) — plus the **P2APP** section (status, STOP/START, UPDATE P2APP) and **FPGA FIRMWARE** (radio only). All in §22.

### ABOUT

Version and build date, the ANAN nomenclature note, credits and attributions, **Report a problem** (one click bundles logs and opens a pre-filled issue), and the **Danger Zone** with **Reset & Uninstall**.

*(A hidden HARDWARE diagnostics tab exists for factory/service use; it is unlocked per-session via the header brand mark and re-locks on every launch. Nothing in it is needed for operating.)*
