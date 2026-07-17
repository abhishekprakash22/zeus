# WDSP filter API inventory (Zeus-facing)

Research reference for the Zeus "Filter visualization & filter panel" PRD. Scope: what filter state
WDSP exposes, what Zeus already surfaces through P/Invoke, and where the frontier is for adding
visualization without touching WDSP itself.

Native sources live under `native/wdsp/`; Zeus wrappers under `Zeus.Dsp/Wdsp/`.

---

## 1. The two bandpass families

WDSP ships two distinct overlap-save bandpass implementations — both visible in
`native/wdsp/bandpass.h`.

- **`BPS` (simple overlap-save)** — `bandpass.h:36–81`. Monolithic FIR, the whole filter in a
  single overlap-save block. Exports `SetRXABPSRun`, `SetRXABPSFreqs`, `SetTXABPSRun`,
  `SetTXABPSFreqs`. **Zeus does not expose any of these.** Thetis uses BPS only for the RXA `bp1`
  "compressor-only aux bandpass" slot; `bp1` is bypassed for SSB.
- **`BANDPASS` (partitioned overlap-save)** — `bandpass.h:96–139`. Segmented FIR with tap count
  `nc` and min-phase flag `mp`. Backed by a `FIRCORE` (`firmin.h`). This is the one Zeus actually
  drives via `SetRXABandpassFreqs` / `SetTXABandpassFreqs`.

The struct carries: `f_low`, `f_high`, `samplerate`, `wintype`, `gain`, plus the FIRCORE (tap
count `nc`, min-phase `mp`). Everything in that list is set via a public `Set*` entrypoint in
WDSP, but Zeus only uses the freq + run + window subset.

### RXA chain: three independent filter stages

`WdspDspEngine.ApplyBandpassForMode` (`Zeus.Dsp/Wdsp/WdspDspEngine.cs:1175–1200`) writes the
passband into **three** WDSP objects on every filter change — matching Thetis `rxa.cs:110–124`:

1. `SetRXABandpassFreqs(ch, lo, hi)` — RXA `bp1`. Bypassed for SSB but kept coherent so a
    mode switch doesn't leave a stale passband behind.
2. `RXANBPSetFreqs(ch, lo, hi)` — RXA `nbp0` (notch-bandpass). **This is the stage that
    enforces the SSB passband.** Without this call, `SetRXABandpassFreqs` alone is a no-op for
    SSB audio.
3. `SetRXASNBAOutputBandwidth(ch, lo, hi)` — SNBA output mask, tracks the selected passband.

All three take **signed** Hz offsets from VFO: LSB-family wants `low = -hiAbs, high = -loAbs`,
USB-family `low = +loAbs, high = +hiAbs`; AM/SAM/DSB/FM/DRM are symmetric around 0 (`low = -hi,
high = +hi`). See the switch at `WdspDspEngine.cs:1181–1193`.

### TXA chain: one filter

`SetTXABandpassFreqs(ch, lo, hi)` — called by `ApplyTxBandpassForMode`
(`WdspDspEngine.cs:959–978`). Same sign convention as RXA. `SetTXABandpassRun` is explicitly
**not** called — despite the name it sets `bp1.run` (compressor-only aux) not `bp0`, and flipping
it reintroduced the "TX 0 W until mode toggle" symptom (see comment at `WdspDspEngine.cs:658–663`).

---

## 2. Exposed to Zeus today (via `Zeus.Dsp/Wdsp/NativeMethods.cs`)

| P/Invoke | WDSP function | What it does | Used by |
|---|---|---|---|
| `SetRXABandpassFreqs` | `SetRXABandpassFreqs` in `bandpass.c` | RXA bp1 low/high Hz (signed) | `ApplyBandpassForMode` |
| `RXANBPSetFreqs` | `RXANBPSetFreqs` in `nbp.c` | RXA nbp0 low/high Hz — the SSB-carrying stage | `ApplyBandpassForMode` |
| `SetRXASNBAOutputBandwidth` | `SetRXASNBAOutputBandwidth` in `snba.c` | SNBA output passband | `ApplyBandpassForMode` |
| `SetRXABandpassRun` | `SetRXABandpassRun` | bp1 enable | `OpenChannel` init |
| `SetRXABandpassWindow` | `SetRXABandpassWindow` | FFT window selector (1 = Blackman-Harris) | `OpenChannel` init |
| `SetTXABandpassFreqs` | `SetTXABandpassFreqs` | TXA bp0 low/high Hz (signed) | `ApplyTxBandpassForMode` |
| `SetTXABandpassWindow` | `SetTXABandpassWindow` | TXA FFT window | `OpenTxChannel` |
| `SetRXAMode`, `SetTXAMode` | `SetRXAMode`, `SetTXAMode` | Demod/mod mode selector (LSB/USB/CWL/CWU/AM/FM/SAM/DSB/DIGL/DIGU/SPEC/DRM) | `SetMode`, `SetTxMode` |

### Filter state Zeus can read today (no new P/Invoke needed)

The engine already stores, per channel, in `ChannelState`
(`WdspDspEngine.cs:93–129`):

- `FilterLowAbsHz`, `FilterHighAbsHz` — unsigned magnitudes (sign is re-derived from
  `CurrentMode`). Default `150..2850 Hz`.
- `CurrentMode` (`RxaMode` enum) — drives sign selection.
- `SampleRateHz` — 48/96/192 kHz input rate.
- `PixelWidth` — analyzer span in pixels (not a filter field, but we need it to map Hz →
  pixel offsets in the overlay).

This is everything required to publish a `FilterStateFrame` today: `{channelId, mode, loHz,
hiHz}` with signed values derived by the same switch `ApplyBandpassForMode` uses.

### Zeus `SetFilter` contract (`WdspDspEngine.cs:333–343`)

```
public void SetFilter(int channelId, int lowHz, int highHz)
```

- Accepts **unsigned magnitudes**; internally reorders if `highHz < lowHz`.
- Stores on `ChannelState`, then calls `ApplyBandpassForMode(state)` which (re)issues all
  three RXA stages with the correct sign.
- Mode switches (`SetMode`) re-invoke `ApplyBandpassForMode` so stored magnitudes survive
  LSB ⇄ USB transitions without operator action.

RX filter state therefore lives **entirely in Zeus.Server memory**; WDSP does not carry
persistent "presets" per mode. Preset tables are a Thetis-side construct (see
`thetis-filter-ux.md`) that would live in Zeus.Server config/store — they are not a WDSP concept.

---

## 3. Gaps (cheap to add if the PRD needs them)

- **`[GAP]` `SetRXABandpassNC(channel, nc)`** — FIR tap count on the partitioned bandpass.
  Thetis default is **1024** (from `rxa.cs`'s `FilterNC` initializer — not verified in Zeus
  context, flag for PRD check). Lets the operator trade audio latency for passband steepness.
  Declared at `bandpass.h:133` but no `NativeMethods` entry. Adding is ~5 lines of P/Invoke plus
  an `engine.SetFilterTaps(int)` pass-through. Not needed for visualization v0 — filter
  visualization draws from `loHz/hiHz`, not from the tap count.
- **`[GAP]` `SetRXABandpassMP(channel, mp)`** — min-phase flag. `bandpass.h:135`. Default is 0
  (linear-phase). Again cheap to add, not needed for v0.
- **`[GAP]` simple BPS family** (`SetRXABPSRun/Freqs`, `SetTXABPSRun/Freqs`) — `bandpass.h:73–81`.
  Zeus does not drive `bp1` (compressor aux) via BPS, and probably never should at the operator
  surface; leave unexposed.
- **`[GAP]` RX2 / second receiver passband** — Zeus today runs a single RXA instance. When/if
  RX2 lands, `SetFilter` needs a channel id the same way Thetis separates `rx1_filters[]` /
  `rx2_filters[]`. Worth designing the contract to be per-channel from the outset (it already is
  — `SetFilter(channelId, ...)`).

No other filter-shape knob is worth exposing for a v0 visualization feature.

---

## 4. Preset mapping — Zeus-side responsibility

Zeus will carry the per-mode preset tables in its own config store (frontend constants +
server-validated write), not via WDSP. The authoritative Thetis defaults live in
`InitFilterPresets` at `console.cs:5182–5585` (12 preset slots `F1..F10, VAR1, VAR2` per mode);
all numeric values and the full mode table are captured in `thetis-filter-ux.md` so the filter
PRD can reference concrete numbers directly.

---

## 5. Verified vs. flagged

- **Verified**: Zeus's `SetFilter` P/Invoke sequence (three stages) matches Thetis
  `rxa.cs:110–124` behavior and keeps SSB audio audible. Already shipping.
- **Verified**: sign convention per mode (`WdspDspEngine.cs:1175–1200`). Already shipping.
- **Flagged for PRD**: tap count `nc` is currently at WDSP's `create_bandpass` default. Zeus
  never calls `SetRXABandpassNC`. If the PRD aims for Thetis parity on passband-edge steepness
  the default `nc` should be matched at `OpenChannel` time (one setter call) — consider
  including in the filter PRD implementation plan or defer as a follow-up.
- **Flagged**: TXA `bp1.run` trap — do not re-enable `SetTXABandpassRun` from the frontend;
  leave it to `OpenTxChannel` which deliberately skips it.
