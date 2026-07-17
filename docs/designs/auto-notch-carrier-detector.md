# Auto-notch: carrier-line detector

Status: implemented (branch `iamexemplar/rx2`). Supersedes the SNR-run auto-notch
heuristic in `zeus-web/src/dsp/auto-notch.ts`.

## Problem

Operator report: the auto-notch "thinks the low-end in voice is EMF to notch out,
and misses clear EMF like the big carrier in the middle of the waterfall."

Both symptoms are the *same* defect in the same component.

## Two mechanisms wear the "ANF" hat

Enabling the **ANF** button (`nr.anfEnabled`) fires two independent auto-notches:

1. **WDSP native LMS ANF** — `native/wdsp/anf.c`, driven from
   `WdspDspEngine.SetNoiseReduction` (`SetRXAANFRun`). A leaky-LMS adaptive line
   enhancer in the *audio* path. It separates a steady tone from voice by
   **predictability**: a sinusoid is autocorrelated at the decorrelation delay,
   so the predictor learns and subtracts it; voice isn't predictable at that lag
   and passes. This mechanism is correct and is kept as-is.
2. **Zeus frontend spectral detector** — `auto-notch.ts`, run by
   `SignalIntelligenceController`. It scanned the panadapter for narrow high-SNR
   peaks and injected them as **manual** WDSP notches, which cut real audio (not
   just the red overlay bars). **This is the part that misbehaved**, and what
   this design replaces.

## Root cause (old detector)

The old detector classified a notch target using only **SNR over a spatial CFAR
floor** plus **temporal "confidence."** But the confidence map
(`signal-estimator.ts`) measures *persistence* — energy present, neighbour-
supported, within 10 dB of the previous frame. Sustained SSB voice satisfies all
of that. SNR + persistence is equally true of a carrier and of voice, so:

- **False positives on voice** — a run-grower walked outward while SNR stayed
  high and accepted any run ≤ 750 Hz unconditionally, slicing a slab out of the
  voice low-end.
- **False negatives on strong carriers** — three biases hid the obvious blocker:
  - *CFAR self-masking*: the ±3 kHz floor window is lifted by the carrier's own
    leakage skirts, shrinking `snr = spec − floor` right at the peak.
  - *Width-cap rejection*: run-growing across the skirts exceeded the wide-blocker
    width/confidence gates, so the strongest signals were the easiest to reject.
  - *Jitter gating*: an unstable CFAR floor jittered the apparent center, so the
    tracker never locked.

## Approach (research-backed)

A spectral auto-notch must separate a **narrowband stationary carrier** (notch)
from **wideband modulated voice** (keep). SNR/persistence cannot. The literature
(adaptive line enhancers; topographic prominence; spectral flatness/kurtosis;
acoustic-feedback "howling" detectors; CFAR target-masking) converges on an
**AND of orthogonal gates dominated by temporal amplitude stationarity**:

| Gate | Metric | Carrier passes |
| --- | --- | --- |
| Level prefilter | SNR over CFAR floor | ≥ `MIN_SNR_DB` (9 dB) — cheap skip of deep noise |
| **Stationarity** | per-bin amplitude steadiness 0..1 | ≥ `MIN_STEADINESS` (0.6); carriers ≳ 0.85 |
| **Prominence** | topographic: peak − higher local saddle | ≥ `MIN_PROMINENCE_DB` (10 dB) |
| **Narrowness** | width at −6 dB from peak | ≤ `MAX_CARRIER_WIDTH_HZ` (500 Hz) |

Why each fixes a symptom:

- **Stationarity** is the carrier-vs-voice discriminant the pipeline lacked. A
  carrier's level is dead-steady frame-to-frame; voice swings several dB with the
  2–10 Hz syllabic envelope. Implemented in `signal-estimator.ts` as an EWMA of
  per-bin SNR plus an EWMA of its absolute deviation (an exponentially-weighted
  mean-absolute-deviation); deviation → steadiness via `STATIONARITY_DEV_FULL_DB`
  (6 dB). `alpha = 0.2` gives a ~0.3–0.5 s memory at display frame rates, long
  enough to span a syllable. Exposed via `getSignalStationarity()`. This rejects
  the voice low-end the old detector ate.
- **Prominence** (topographic: walk out to the higher of the two local saddle
  minima) replaces "SNR over floor" as the primary level gate. A strong carrier
  that lifts its own CFAR floor still towers over its local saddles, so it is no
  longer missed; a voice formant on a broad hump has little prominence. This
  fixes the missed blocker.
- **Narrowness** at a fixed −6 dB drop (not "above the floor") stops leakage
  skirts from inflating the measured width and rejects 2–3 kHz voice humps.

If the stationarity map is unavailable the detector emits **nothing** (fail safe)
rather than regress to prominence-only, which would re-introduce voice notching.

The verification/locking tracker (`createAutoNotchTracker`) is unchanged; it now
operates on narrow carrier candidates, so emitted notches are surgical (tens to a
few hundred Hz) instead of wide slabs. `MAX_WIDTH_HZ` was lowered to 600 Hz.

## Protected digital segments (FT8/FT4)

FT8 and FT4 sub-bands are wall-to-wall narrow steady carriers — the detector's
ideal prey, and exactly where it must NOT fire (the operator is decoding there).
`digital-segments.ts` holds an authoritative table of FT8 + FT4 dial frequencies
across every supported band and exports `DIGITAL_PROTECTED_RANGES`
(`[dial − 300, dial + 3300]` per segment). The detector skips any candidate whose
center lands in a protected range. This is kept separate from the user-editable
band plan, which only carries coarse "digital" markers, not the precise 3 kHz
windows.

## Zoom-aware narrowness

The narrowness gate was a fixed 500 Hz, but at a full-band span one FFT bin is
hundreds of Hz, so a genuine carrier smears across 2–3 bins and was rejected —
the "missing carriers" symptom on a wide view. The limit is now
`max(MAX_CARRIER_WIDTH_HZ, CARRIER_MAX_BINS × hzPerPixel)` (≈4 bins), so it stays
"a few bins wide" at any zoom while still rejecting multi-kHz voice humps.

## Files

- `zeus-web/src/dsp/signal-estimator.ts` — per-bin amplitude stationarity +
  `getSignalStationarity()`.
- `zeus-web/src/dsp/auto-notch.ts` — `detectAutoNotches` rewritten as a
  prominence + narrowness + stationarity carrier-line detector; `saddleProminenceDb`,
  `widthAtDropHz` helpers; wide-blocker path removed.
- `zeus-web/src/dsp/digital-segments.ts` — FT8/FT4 protected-range table.
- `zeus-web/src/components/SignalIntelligenceController.tsx` — feeds the
  stationarity map and `DIGITAL_PROTECTED_RANGES` into the detector.

## Tuning notes / maintainer review

The gate thresholds (`MIN_PROMINENCE_DB`, `MAX_CARRIER_WIDTH_HZ`, `MIN_STEADINESS`,
`STATIONARITY_*`) are starting points from the literature and should be tuned
on-air. Auto-notch behaviour is operator-facing (red-light in `CLAUDE.md`): the
*defaults* want a bench/on-air check before this is considered final. The
algorithm change (stop notching voice, catch steady carriers) is the fix; the
exact numbers are the dial.
