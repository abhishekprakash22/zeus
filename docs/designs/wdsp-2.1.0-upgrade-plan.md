# WDSP 2.1.0 upgrade plan

Status: phase 1 implemented on branch `wdsp-2.1.0` (2026-09-04); phases 2–5 open.
Inputs: `2026-09-04 WDSP, ver 210.zip` and `WDSP_Guide__Rev_2_1_0.pdf` (Warren Pratt, NR0V).
Patch manifest for the vendored tree: `native/wdsp/ZEUS-PATCHES.md`.

Zeus vendors WDSP **1.29** (2026-01-16) plus Thetis-lineage additions. Upstream is now
**2.1.0** (2026-09-04). Two releases sit between them: 2.0.0 (PureSignal 3, NURBS
free-curve EQ/CFC, phase-rotator auto-cal, new RX half-band decimator, FM broadcast
stereo) and 2.1.0 (deep-neural-network noise reduction "NNR", PS3 tuning, auto-generated
`wdsp.h`).

> **Hard rule reminder.** PureSignal is a KB2UKA burn-zone subsystem (see `CLAUDE.md`).
> This plan deliberately keeps every PS-touching change behind an explicit approval gate.
> Phase 1 ships with **zero managed-side PS changes** by carrying a C compatibility shim.

## 1. Where Zeus is today

| Item | Current state |
| --- | --- |
| Vendored source | `native/wdsp/` = upstream 1.29 + Thetis `rnnr.c` (NR3/RNNoise), `sbnr.c` (NR4/libspecbleach), `FDnoiseIQ.c`, plus Zeus `linux_port.{c,h}` and `wdsp_export.h` |
| P/Invoke surface | 184 entry points in `Zeus.Dsp/Wdsp/NativeMethods.cs` |
| Entry points that disappear in 2.1.0 | 13: the 12 Thetis NR3/NR4 setters (`SetRXARNNR*`, `RNNRloadModel`, `RNNRmodelLoaded`, `SetRXASBNR*`) and `psccF` (Thetis float-I/Q wrapper around upstream `pscc`) |
| Prototype changes for functions Zeus already imports | none except the `SetTXAPHROTCorner` parameter *name* |
| Already-declared imports with no 1.29 implementation | `SetTXAPHROTAutoMode`, `SetTXAPHROTAutoReset`, `GetTXAPHROTAsymmetry` (called from `WdspDspEngine.cs:2652/2664/2708`). They only exist in 2.0+. Whether those paths are reachable with the shipped binary needs checking; the upgrade makes them real either way. |
| Shipped binaries | `libwdsp` ~5.9 MB per RID, checked in for 5 RIDs |

Upstream stopped shipping the Thetis NR3/NR4 hooks; they were never upstream. Richard
Samphire (MW0LGE) has also announced Thetis v2.10.3.14 as the last public release, so
there is no Thetis re-splice of those hooks onto WDSP 2.x to borrow. **Zeus owns that port.**

## 2. What changes in the drop

### 2.1 Exported API diff (`wdsp.h`, 539 → 540 symbols)

Removed (34): `GetTXAiqcValues`, `SetTXAiqc*`, `SetTXAEQMethod`, `SetRXABPS*`/`SetTXABPS*`,
`NewCriticalSection`, `analyze_bandpass_filter`, the Thetis NR3/NR4 setters, `psccF`, and the
PureSignal experiment knobs `SetPSIntsAndSpi`, `SetPSMapMode`, `SetPSPinMode`, `SetPSPtol`,
`SetPSStabilize`. The guide (p.110) says of the PIN/MAP/TINT knobs: "you should remove them".
Zeus never imported them.

Added (35): `SetRXANNR*` (12), `SetNNRModelPath[Slot]`, `GetRXANNRModel`,
`SetRXAEQCurve`/`SetRXAEQWeights`/`GetRXAEQDraw`, `SetTXAEQCurve`/`Weights`/`GetTXAEQDraw`,
`SetTXACFCOMPGprofile`/`Eprofile`/`CompCurve`/`CompWeights`/`PeqCurve`/`PeqWeights`/
`GetTXACFCOMPCompDraw`/`PeqDraw`, `SetTXACFIRCurve`, `SetTXAPHROTAutoMode`/`AutoReset`/
`GetTXAPHROTAsymmetry`, `SetRXAWBFMdmph`/`GetRXAWBFMStereoIndicator`.

Changed: `GetPSDisp` gains four correction-curve vectors plus `nsamps_out`, `cpts_out`,
`phs_ref_deg_out` (AmpView support). Zeus does not import it today.

### 2.2 Source-tree diff

New files: `nnr.c`, `nnet.c`, `nnio.c`, `nnr_model_0.c` (10 MB source, 2.1 MB data),
`nnr_model_1.c` (24 MB source, 4.5 MB data), `nurbs.c`, `nurbs_fit.c`, `nurbs_spline.c`,
`phrot.c`, `reshb.c` (RX half-band decimator), `wbfm.c`, `extrapolate.c`, `snoop.c`,
`version.h`, `resource1.h`, a bundled `fftw3.h`.

Gone upstream: nothing Zeus needs. Gone from the Zeus tree: `FDnoiseIQ.c` (only `emnr.c`
used it, 2.1.0's `emnr.c` does not), the `make_*.c` generator tools (never built).

Real churn (whitespace-insensitive line changes): `calcc.c` 2789, `analyzer.c` 1222,
`eq.c` 861, `emnr.c` 810, `cfcomp.c` 687, `iir.c` 668, `snb.c` 486, `gen.c` 470,
`fir.c` 452, `nobII.c` 418, `RXA.c` 292, `TXA.c` 205. Everything else is under 400.

### 2.3 Portability status of the drop

The drop is Windows-only again: `comm.h` includes `<Windows.h>`/`<avrt.h>` unconditionally
and `PORT` is `__declspec(dllexport)`. The existing `linux_port.h` covers every Win32 token
the new tree uses except six:

| Token | Where | Fix |
| --- | --- | --- |
| `WaitForMultipleObjects` (+ `WAIT_OBJECT_0`, `CreateSemaphoreW`) | `calcc.c` `doPSCorrChange` thread waits on 5 semaphores | Real shim needed: either a poll loop over `sem_trywait` with a short sleep, or replace the 5-semaphore fan-in with one semaphore plus an index queue. Must be reviewed as PS-adjacent (thread plumbing, not algorithm). |
| `GetCurrentThread` | `main.c` fallback priority path | trivial macro |
| `OutputDebugStringA` | `utilities.c` `dprintf` | map to `fputs(stderr)` |
| `QueryPerformanceCounter`/`Frequency`, `LARGE_INTEGER` | `nnet.c` profiling | already `#ifdef _WIN32`-guarded upstream; no work |

`wdsp.h` now wraps prototypes in `WDSP_API`, which falls back to empty off Windows. Symbol
visibility still comes from `PORT` on the definitions, so mapping `PORT` → `WDSP_EXPORT`
(the existing `comm.h` patch) keeps `-fvisibility=hidden` builds correct.

### 2.4 Behavioural changes an operator will feel

- **PureSignal 3.** NURBS + LOWESS fit to raw data, smoothing applied after the fit.
  Refuses to calibrate below ~6% usable high-amplitude data and reports probable severe
  over-drive as `info[6] == 2`. Adds `info[7]` (attempted calibrations). State enum
  `LRESET..LTURNON` (0..9) is unchanged, so Zeus's `PsStageMeters` decoding of `info[4]`,
  `[5]`, `[14]`, `[15]` still holds. Calibration thread uses more CPU; `SetPSLoopDelay`
  throttles it. Two-tone close-in IMD can read a few dB *worse* than PS2 while noise/speech
  tests read the same or better (guide Appendix D). The pihpsdr-derived "seven zero blocks on
  disarm" drain in `SetPsEnabled` must be re-validated against the rewritten state machine.
- **RX input decimator.** `reshb.c` replaces the input resampler for all RXA input decimation:
  better alias rejection, more output bandwidth, up to 6144 kHz input, explicitly more CPU.
  Needs a Raspberry Pi 4 measurement before release (cross-platform hard requirement).
- **Wisdom.** Single `MAX_WISDOM_SIZE = 262144` and file name `wdspWisdom01` (1.29 wrote
  `wdspWisdom00`; `WdspWisdomInitializer.WisdomFileName` already says `01`, so the stale-
  delete path starts matching). First start after upgrade regenerates wisdom; on a Pi that is
  minutes, so the existing `wisdom_get_status` progress surface matters.
- **NNR latency** adds 51.17 ms post-AGC (NR2 adds 64 ms). NNR and NR2 are documented as
  mutually exclusive.

## 3. Strategy

Vendor 2.1.0 verbatim, re-apply a *small, documented* patch set, and keep the managed API
surface unchanged in the first landing. Add features in separate PRs. Rationale: the last
re-vendor left one commit (`6958584 upload`) and no patch manifest, which is why this
analysis had to reverse-engineer the drift. Fix that this time.

### Phase 0. Prep (half a day)

1. Record the current tree: `tools/compare-wdsp-source-drift.ps1 -ReferenceDir <2.1.0 dir> -CandidateDir native/wdsp` and commit the report under `docs/designs/dsp/`.
2. Create `native/wdsp/ZEUS-PATCHES.md` listing every non-upstream file and every edited
   upstream file, with the reason. Future bumps re-apply from this list.
3. Open the bd issue and a GitHub issue for the PS-adjacent items (section 3, Phase 3) so
   KB2UKA can approve before that code is written.

### Phase 1. Port 2.1.0 with API parity (the only mandatory phase)

Native:

1. Replace `native/wdsp/*.c *.h` with the drop. Drop `FDnoiseIQ.*`, `make_*.c`, the bundled
   `fftw3.h` (system FFTW stays the source of truth).
2. Re-apply `comm.h`: platform include block, `#include "linux_port.h"`, `PORT WDSP_EXPORT`.
   Keep `wdsp_export.h`, `linux_port.{c,h}`.
3. Extend `linux_port` with the four tokens in 2.3. The `WaitForMultipleObjects` shim is the
   one piece of real engineering.
4. Add `native/wdsp/zeus_compat.c` carrying Thetis's 20-line `psccF` (float I/Q → interleaved
   double → `pscc`). This keeps `NativeMethods.psccF` and `FeedPsFeedbackBlock` byte-identical,
   i.e. no PS logic change in phase 1.
5. Re-splice NR3/NR4 onto the new `RXA.{c,h}`: the two struct members, create/destroy/flush/
   x/setSamplerate/setSize/setBuffers hooks (~20 lines, mirror the `nnr` hooks that now sit
   in the same places), and extend `RXAbp1Check` with `rnnr_run, sbnr_run` (it now already
   carries `nnr_run`). Update the callers in `rnnr.c`/`sbnr.c` to the new arity. Keep the
   `WDSP_WITH_NR3` / `WDSP_WITH_NR4` gates and stubs as they are.
6. `CMakeLists.txt`: `VERSION 2.10.0`; add `extrapolate nnet nnio nnr nnr_model_0 nnr_model_1
   nurbs nurbs_fit nurbs_spline phrot reshb wbfm snoop`; remove `FDnoiseIQ`. Add a
   `WDSP_WITH_NNR_PREMIUM` option (default ON) that compiles `nnr_model_1.c`; when OFF the
   slot reports empty and `SetRXANNRModel(…,1)` returns 0, which the guide documents as the
   discovery mechanism.
7. CI (`build-native-libs.yml`, `release.yml`): keep the 8 SBNR + 4 RNNR export checks,
   add a 13-symbol NNR check, add `GetWDSPVersion() == 210` to `audit-wdsp-native-symbols.ps1`.
   Verify MSVC (x64 + arm64 static) accepts the 24 MB initializer in `nnr_model_1.c`; if it
   is unreasonably slow, fall back to a `.bin` staged next to the library and loaded via
   `SetNNRModelPathSlot` (nnet.c already supports this).

Managed:

8. No `NativeMethods` removals. Add the NNR and `GetPSDisp` imports but do not call them yet.
9. Bump the wisdom version stamp so every RID regenerates once.

Acceptance: `dotnet build Zeus.slnx` + tests green on macOS/Windows/Linux; DSP fixture
matrix (`tools/run-dsp-wdsp-fixture-matrix.ps1`) within tolerance vs the 1.29 baseline for
RX audio, NR1/NR2/NR4 and panadapter pixels; `summarize-dsp-native-stage-timing.ps1` on a
Pi 4 shows RXA within budget with the new decimator; PS smoke test on the G2 by KB2UKA
(arm, calibrate, disarm, re-arm, restore-from-file), with the seven-zero-block drain
re-validated.

Size note: each `libwdsp` grows by ~8 MB (models). With five RIDs checked in that is ~40 MB
of binaries per refresh. Decide before phase 1 lands whether checked-in runtimes move to
CI-only artifacts or Git LFS.

#### Phase 1 landing notes (2026-09-04, branch `wdsp-2.1.0`)

Done: tree re-vendored; `ZEUS-PATCHES.md` manifest; shim extended (with a fix
for the zero-timeout wait that `calcc.c` now relies on); `zeus_compat.c`
`psccF`; NR3/NR4 re-spliced via `RXAbp1CheckEx`; CMake sources / version /
`WDSP_WITH_NNR_PREMIUM`; CI export checks for NNR + `psccF`; NNR, `GetPSDisp`
and `GetWDSPVersion` declared in `NativeMethods` (uncalled). Verified locally on
Windows x64 with MSVC: builds, `GetWDSPVersion() == 210`, every Zeus import
resolves except the NR4 set (off in the local build because libspecbleach needs
clang-cl, which the CI runners have and this machine does not).

Still open before phase 1 is complete: (a) the manual native workflow must
rebuild all five RIDs so Linux / macOS compile the shim and libspecbleach
links on Windows; (b) checked-in runtime binaries were NOT refreshed here —
the local build lacks NR4 and must not be committed; (c) the fixture-matrix
comparison and the Pi 4 stage-timing run; (d) KB2UKA's PureSignal smoke test
on the G2. `bd` is not installed on the machine that did this work, so the
tracking issue still has to be created.

### Phase 2. NNR as a new RX noise-reduction mode

- `IDspEngine`: `NrMode.Nnr`; `NrConfig` gains `NnrMaskFloorDb` (−50..−10, default −25 per
  WDSP) and `NnrModelSlot` (0 Standard / 1 Premium). Defaults follow WDSP verbatim; any
  deviation is a red-light default decision.
- `WdspDspEngine.SetNoiseReduction`: new case that turns off EMNR/SBNR/RNNR, pushes mask
  floor + model, `SetRXANNRRun(1)`. Add `Nr5NnrAvailable` using the same
  `AllNativeExportsAvailable` guard pattern as `Nr4SbnrAvailable`. Report the slot actually
  selected back into state so the UI can hide Premium when the build lacks it.
- Model override: mirror `Nr3ModelStore` as an optional `NnrModelStore` that stages
  operator-supplied `wdsp_nnr_{0,1}.bin` under `%LOCALAPPDATA%/Zeus/nnr-models/` and calls
  `SetNNRModelPathSlot` with absolute paths **before the first `OpenChannel`** (models load at
  channel creation; nnet.c opens the path relative to the process CWD otherwise).
- Endpoints/contracts: extend `/api/rx/nr` and `StateDto` (wire-format change → red-light,
  flag in PR). Persist in `DspSettingsStore`.
- Frontend: `NrMode` union + `NrSettingsSection` controls (one slider, one two-way toggle,
  a "Standard / Premium" label driven by the reported slot). `SmartNrController` needs a
  rule for NNR.
- Platform gate: NNR Standard is ~10% of one desktop core (Premium ~32%), plain-C double
  math with no SIMD. Measure on Pi 4 and macOS arm64; if Pi cannot hold real time for two
  RX channels, surface a capability flag from `/api/radio/capabilities` and show the mode
  greyed with the reason instead of letting audio stutter.
- Tests: extend `NoiseReductionTests` and the persistence tests; add a stage-timing fixture.

### Phase 3. PureSignal 3 follow-ups (KB2UKA approval required before code)

Proposed, in priority order, each as its own approved change:

1. Replace the `psccF` shim with direct `pscc` on an interleaved `double[]` in
   `FeedPsFeedbackBlock` (removes a float→double copy per block). Managed-side PS change.
2. Surface `info[6] == 2` as an operator-visible "probable severe over-drive, check drive
   level" warning in the PS panel and log, and `info[7]` as attempted calibrations next to
   the existing completed count.
3. AmpView: import the new `GetPSDisp` and draw amplifier gain/phase plus the correction
   curves (4096 sample points, 512 curve points, phase reference returned). Visual design →
   Brian/Doug.
4. Re-tune `SetPSLoopDelay` default only if bench data on the G2 shows calibration-thread
   CPU contention. Default change → red-light.
5. Keep `HwPeakByBoard` untouched; the 0.4072 default in 2.1.0 is unchanged.

### Phase 4. TX audio and RX tooling that 2.1.0 unlocks

- Phase rotator auto-cal (`SetTXAPHROTAutoMode`/`AutoReset`/`GetTXAPHROTAsymmetry`) becomes
  functional; expose the searching/converged state (`auto_step`) and IN/OUT asymmetry in the
  TX fidelity panel.
- CFC: move from `SetTXACFCOMPprofile` to the independent `Gprofile`/`Eprofile` calls and
  draw the real curves with `GetTXACFCOMPCompDraw`/`PeqDraw` (1024 points, non-uniform X).
  Optional higher-degree curves via `SetTXACFCOMP*Curve` (degree 3/5/7).
- RX/TX EQ: Zeus does not drive the WDSP equalizer today (`SetTXAEQRun` only). If EQ is ever
  wired natively, `SetRXAEQCurve`/`GetRXAEQDraw` give a drawable continuous-gain curve.
- FM broadcast stereo (RXA mode 12): requires `dsp_rate = 192k` and ≥192k input; niche, but a
  P2 board can do it. Low priority.
- CW APF DoublePole/Matched/Gaussian filters already exist in 1.29 and are not exposed.

### Phase 5. Decide the future of NR3 (RNNoise)

The guide reports NNR beating RNNoise in both metrics and listening on HF, with no
pitch-tracker settling artefact. Once phase 2 has bench listening from Brian/Doug, propose
retiring NR3 (drop `rnnr.c`, the vendored `native/rnnoise/`, `Nr3ModelStore`, the model
upload endpoints and panel). That is a UX/scope decision, not an engineering one; keep NR4
(libspecbleach) regardless, it is a different algorithm class with its own persistence tests.

## 4. Risks

| Risk | Mitigation |
| --- | --- |
| `WaitForMultipleObjects` shim subtly changes PS correction-change ordering | Keep semantics identical (wake on any of 5, process that index); review with KB2UKA; exercised by PSSaveCorr/PSRestoreCorr smoke test |
| Pi 4 cannot keep up with `reshb` decimation + NNR | Measure in phase 1 (decimator alone) and phase 2 (NNR); gate NNR by capability |
| MSVC time/memory on 24 MB model initializer | Test in CI early; `.bin` fallback via `SetNNRModelPathSlot` |
| Repo bloat from +8 MB × 5 RIDs | Move runtimes to CI artifacts / LFS before landing |
| Wisdom regen on first start (minutes on Pi) | Stamp bump + progress UI already exist; note in release notes |
| PS3 "won't calibrate" on an over-driven chain looks like a regression | Ship the over-drive warning (phase 3.2) early; document in `docs/lessons/` |
| 51 ms extra latency when NNR is on | Mutually exclusive with NR2 and off by default; call it out in the mode tooltip |
| Analyzer churn (1222 lines) shifts panadapter levels | Fixture matrix comparison against 1.29 pixels |

## 5. Open questions for the maintainers

1. Checked-in native runtimes: keep, LFS, or CI-only? (blocks phase 1 size decision)
2. Approve phase 3 items as a batch or individually? (KB2UKA)
3. Is Premium NNR worth the 24 MB source / 5.9 MB binary on every RID, or Windows/macOS
   x64 only? (`WDSP_WITH_NNR_PREMIUM`)
4. Retire NR3 after phase 2 listening, or keep three NR engines? (Brian/Doug)
