# Thetis filter panel UX

Research reference for the Zeus "Filter visualization & filter panel" PRD. Source:
`ramdor/Thetis` @ `master` — `Project Files/Source/Console/` unless noted otherwise. Line numbers
refer to that mirror's `console.cs` as fetched for this research.

---

## 1. Filter slot model

- Each DSP mode owns its own `FilterPreset[]` row. Slots per mode are **exactly 12**:
  `F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, VAR1, VAR2`.
- Two parallel preset stacks: `rx1_filters[(int)DSPMode]` and `rx2_filters[(int)DSPMode]`
  (`console.cs:5179–5180`). Same shape for both receivers.
- Default "last filter" (the one selected on mode entry) is `F5` for every mode that has
  presets (LSB/USB/CWL/CWU/AM/SAM/DSB/DIGL/DIGU) — see the repeated
  `preset[m].LastFilter = Filter.F5;` at the end of each case.
- Modes without presets (`FM`, `SPEC`, `DRM`, any unused `DSPMode`): `LastFilter = NONE`.
  FM specifically is treated as fixed-bandwidth by the Thetis radio.cs path; there is no
  operator-editable FM filter preset.

---

## 2. Per-mode default preset tables (Hz, signed)

All numbers verbatim from `console.cs:5182–5585` `InitFilterPresets`. Positive = above VFO,
negative = below. `cw_pitch` is the CW sidetone (default 600 Hz). `digl_click_tune_offset` /
`digu_click_tune_offset` are the digital-mode click-tune offsets (set by operator; default
reads as 0 on a fresh profile — Thetis lets the user set them via Setup > DSP > Options).

### LSB — `console.cs:5199–5240`

| Slot | Low | High | Name |
|------|-----|------|------|
| F1 | −5100 | −100 | 5.0k |
| F2 | −4500 | −100 | 4.4k |
| F3 | −3900 | −100 | 3.8k |
| F4 | −3400 | −100 | 3.3k |
| F5 | −3000 | −100 | 2.9k |
| F6 | −2800 | −100 | 2.7k |
| F7 | −2500 | −100 | 2.4k |
| F8 | −2200 | −100 | 2.1k |
| F9 | −1900 | −100 | 1.8k |
| F10 | −1100 | −100 | 1.0k |
| VAR1 | −2800 | −100 | Var 1 |
| VAR2 | −2800 | −100 | Var 2 |

### USB — `console.cs:5241–5282` (mirror of LSB)

| Slot | Low | High | Name |
|------|-----|------|------|
| F1 | 100 | 5100 | 5.0k |
| F2 | 100 | 4500 | 4.4k |
| F3 | 100 | 3900 | 3.8k |
| F4 | 100 | 3400 | 3.3k |
| F5 | 100 | 3000 | 2.9k |
| F6 | 100 | 2800 | 2.7k |
| F7 | 100 | 2500 | 2.4k |
| F8 | 100 | 2200 | 2.1k |
| F9 | 100 | 1900 | 1.8k |
| F10 | 100 | 1100 | 1.0k |
| VAR1 | 100 | 2800 | Var 1 |
| VAR2 | 100 | 2800 | Var 2 |

> Note: Zeus's default `FilterLowAbsHz=150, FilterHighAbsHz=2850`
> (`WdspDspEngine.cs:114–115`) corresponds to **neither** Thetis F6 (100, 2800, 2.7k) nor F7
> (100, 2500, 2.4k) exactly. Zeus chose 150 Hz low-cut instead of 100 Hz intentionally (see the
> OpenTxChannel comment at `WdspDspEngine.cs:649–653`: "wider than the classic SSB 300-2700 to
> keep low-frequency voice energy through the chain"). The PRD should decide whether to
> preserve this Zeus default or snap to Thetis F5/F6.

### CWL — `console.cs:5367–5408` (centered on `-cw_pitch`)

| Slot | Half-width | Width | Name |
|------|-----------|-------|------|
| F1 | ±500 | 1.0k | 1.0k |
| F2 | ±400 | 800 | 800 |
| F3 | ±300 | 600 | 600 |
| F4 | ±250 | 500 | 500 |
| F5 | ±200 | 400 | 400 |
| F6 | ±125 | 250 | 250 |
| F7 | ±75 | 150 | 150 |
| F8 | ±50 | 100 | 100 |
| F9 | ±25 | 50 | 50 |
| F10 | ±13 | 25 | 25 |
| VAR1/VAR2 | ±250 | 500 | Var 1 / Var 2 |

Actual Hz: `low = -cw_pitch - half`, `high = -cw_pitch + half`. With default `cw_pitch = 600`
the F5 (400 Hz) slot spans −800..−400 Hz.

### CWU — `console.cs:5409–5450` (mirror of CWL, centered on `+cw_pitch`)

Same half-widths as CWL; `low = cw_pitch - half`, `high = cw_pitch + half`. With default
`cw_pitch = 600` the F5 (400 Hz) slot spans +400..+800 Hz.

### AM — `console.cs:5451–5492` (symmetric around 0)

| Slot | Half-width | Width | Name |
|------|-----------|-------|------|
| F1 | ±10000 | 20k | 20k |
| F2 | ±9000 | 18k | 18k |
| F3 | ±8000 | 16k | 16k |
| F4 | ±6000 | 12k | 12k |
| F5 | ±5000 | 10k | 10k |
| F6 | ±4500 | 9.0k | 9.0k |
| F7 | ±4000 | 8.0k | 8.0k |
| F8 | ±3500 | 7.0k | 7.0k |
| F9 | ±3000 | 6.0k | 6.0k |
| F10 | ±2500 | 5.0k | 5.0k |
| VAR1/VAR2 | ±3000 | 6.0k | Var 1 / Var 2 |

### SAM — `console.cs:5493–5534` (identical to AM table)

### DSB — `console.cs:5535–5576`

| Slot | Half-width | Width | Name |
|------|-----------|-------|------|
| F1 | ±8000 | 16k | 16k |
| F2 | ±6000 | 12k | 12k |
| F3 | ±5000 | 10k | 10k |
| F4 | ±4000 | 8.0k | 8.0k |
| F5 | ±3300 | 6.6k | 6.6k |
| F6 | ±2600 | 5.2k | 5.2k |
| F7 | ±2000 | 4.0k | 4.0k |
| F8 | ±1550 | 3.1k | 3.1k |
| F9 | ±1450 | 2.9k | 2.9k |
| F10 | ±1200 | 2.4k | 2.4k |
| VAR1/VAR2 | ±3300 | 6.6k | Var 1 / Var 2 |

### DIGL — `console.cs:5283–5324` (centered on `-digl_click_tune_offset`)

Half-widths: 1500 / 1250 / 1000 / 750 / 500 / 400 / 300 / 150 / 75 / 38. Names: 3.0k / 2.5k /
2.0k / 1.5k / 1.0k / 800 / 600 / 300 / 150 / 75. VAR1/VAR2 = ±400 "Var 1/2". Default
`digl_click_tune_offset = 0` so the F5 (1.0k) slot spans −500..+500 Hz until the operator sets
a click-tune offset.

### DIGU — `console.cs:5325–5366` (mirror of DIGL)

Half-widths identical to DIGL, centered on `+digu_click_tune_offset`.

### FM — no entries in `InitFilterPresets`

FM falls into the `default:` branch (`console.cs:5577–5580`) which only sets
`LastFilter = NONE`. The FM filter is instead driven by the FMN/FMW mode in the radio-path code;
operator-editable FM filter presets do not exist in Thetis 2.10.

---

## 3. Variable filter behavior

`VAR1` / `VAR2` are edit-in-place slots: clicking VAR1 selects it, then the operator edits the
low/high directly (Setup > Filters > FilterForm, or by dragging the passband edges on the
panadapter — see §4). The numbers are persisted per mode back into the same preset array, which
is then written to the profile DB. There is no separate "variable mode" — the selected preset
slot is always `Filter`-typed, and VAR* slots simply behave as mutable presets.

`FilterPreset.SetLow(Filter, int)` / `SetHigh(Filter, int)` (`filter.cs` class) are the setters
called by the drag interaction and by the Setup dialog. Both clamp to the form's `udLow.Minimum
/ Maximum` bounds — `FilterForm.cs:371–407` shows the `NumericUpDown` range on the manual-edit
form but the literal min/max are not in the uppermost 200 lines I pulled; typical values are
±9999 for SSB, ±20000 for AM/SAM. Flag for PRD: confirm the clamp range if the UI is going to
mimic Thetis's behavior exactly.

---

## 4. Panadapter passband overlay (Thetis)

From `PanDisplay.cs` (not fully read — structural summary only, based on known Thetis behavior
and the `filterColor` naming found in searches):

- **Shaded region** drawn between `vfo + low` and `vfo + high` pixels. Color is
  configurable (`grid_filter_color` / `filter_color` — default a semi-transparent green on
  white grid, cyan on dark grid).
- **Edge lines**: thin vertical at the low and high pixel positions; these are drag-handles
  when the operator clicks within a few pixels of them (the drag action writes back into the
  active `FilterPreset` slot — VAR1/VAR2 for ad-hoc, or the selected F-slot if the operator has
  "Edit mode" engaged).
- **Center marker**: a vertical line at the VFO (not the filter center). For CW this means
  the filter passband sits offset from the center line by `cw_pitch`.
- **Waterfall**: the same shaded passband column is drawn down the waterfall so the operator
  can see the filter's relationship to moving signals.

> Caveat: I did not paste exact pixel-drawing code from PanDisplay.cs into this doc — the full
> file wasn't needed to produce the Zeus PRD, since the Zeus overlay will be rendered by the
> web frontend, not a direct port. The PRD can work from this structural summary plus the
> numeric preset tables in §2.

---

## 5. Out-of-band indication

**Thetis does NOT draw any filter-specific out-of-band indicator on the panadapter.** The
region-based band-edge information is surfaced separately:

1. `BandText` (the sub-band label shown under the VFO) turns to "Out of Band" in amber/red
   when the current carrier frequency is outside any matching band segment
   (`database.cs:9566` `BandText()`, fallback return); see `thetis-bandplan.md` §2b.
2. TX is inhibited via `CheckValidTXFreq` (`console.cs:6778`), which for SSB/AM/FM etc.
   re-checks with `freq + filterLow` / `freq + filterHigh` — so a filter extending past the
   band edge denies TX even if the carrier is in-band. There is NO visual warning for this on
   the panadapter; the operator only learns by hitting MOX and seeing the TX-denied light.

This is a **Zeus PRD opportunity**: once the band-planning feature lands, Zeus can draw a
colored warning band in the filter overlay when `vfo + loHz` or `vfo + hiHz` crosses a
band-plan boundary. The contract already needs `inBand(freqHz, mode)` for the band-planning
PRD; the filter PRD just consumes it.

---

## 6. Gotchas

- **Filter is an RX concept separate from TX.** `rx1_filters[]` and `rx2_filters[]` are
  receiver-side; TX filter is derived from the TXA mode + a single per-profile TX filter
  low/high pair (Setup > DSP > Transmit). The operator does not pick from a TX preset list.
  The Zeus PRD should follow suit — visualize and edit only the RX filter.
- **CW filter is centered on sidetone, not 0 Hz.** Naïve "draw passband at `vfo ± halfwidth`"
  will put the CW passband on the wrong side of the carrier. The overlay must read the signed
  low/high produced by `ApplyBandpassForMode` or re-apply the same sign logic client-side.
- **DIGL/DIGU click-tune offset is user-configurable** and defaults to 0. Any visualization
  that assumes a non-zero offset will mis-position the digital passband for fresh profiles.
- **VAR1/VAR2 are per-mode, per-receiver** — edits to RX1's USB VAR1 do not propagate to
  RX2 or to any other mode.
- **Zeus's 150 Hz low-cut** deviates from Thetis's 100 Hz low-cut on SSB. The PRD has to
  pick: keep Zeus's current default (wider low end) or reset to Thetis F6 on fresh connect.

---

## 7. Zeus-side implication summary

- **Frontend preset tables** can be shipped as a TypeScript constant identical to §2 — no
  server round-trip needed just to render labels. Per-operator edits to VAR1/VAR2 will need
  server persistence (fits the `DspSettingsStore` pattern).
- **Wire format**: the frame only needs `{channelId, mode, loHz, hiHz}` (signed), plus
  optionally the name of the currently-selected slot if the UI wants to highlight it. The
  frontend can reconstruct the preset table from its TypeScript constant.
- **Visualization**: shaded passband rectangle + two drag-handle vertical lines, drawn at
  `vfo + loHz`..`vfo + hiHz` on the panadapter AND the waterfall. Amber (`#FFA028`) at low
  alpha for the fill, solid amber lines at the edges — stay on-palette (see CLAUDE.md).
- **Out-of-band warning**: deferred to band-planning PRD landing; coloring flips from amber
  to red once `inBand(...)` returns false for either edge.
