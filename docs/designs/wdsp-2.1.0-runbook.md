# WDSP 2.1.0 runbook — phases 1 and 2, from branch to on-air

Companion to `wdsp-2.1.0-upgrade-plan.md`. That document says *what* and
*why*; this one is the ordered checklist of *what to do next*, with the
command, the expected result, and who signs off. Everything below assumes
branch `wdsp-2.1.0` at commit `9430cfb` or later, pushed to
`abhishekprakash22/zeus`.

State at the time of writing:

| Item | Status |
| --- | --- |
| Native source port (phase 1) | committed: `174f82b`, `9430cfb` |
| NR5 feature (phase 2) | committed: `206e8b4` |
| Checked-in native binaries | still WDSP 1.29 — nothing new is visible in the app until step 1.2 |
| Native workflow run #1 | failed (fixed in `9430cfb`); needs re-run |

---

## Phase 1 — get the port built, proven, and merged

### 1.1 Re-run the native build on GitHub

1. Open https://github.com/abhishekprakash22/zeus/actions/workflows/build-native-libs.yml
2. **Run workflow** → branch `wdsp-2.1.0` → Run.
3. Wait for the five WDSP jobs: *Build Linux Native Libraries (x64, arm64)*,
   *Build Windows Native Libraries (x64, arm64)*, *Build macOS Native Libraries*.

Expected: all five green, each with the three verify steps passing
(*Verify SBNR exports* = 8, *Verify RNNR exports* = 4, *Verify WDSP 2.1.0
exports* = 11 `SetRXANNR*` + `psccF`).

Known noise: the three *Build VST3 Bridge* jobs fail at *Init vst3sdk
submodule* on this fork. That is unrelated to WDSP (the workflow had never
run on the fork before). Ignore for this task, or fix the submodule fetch
separately.

If a WDSP job fails: open the job → expand the red **Build** step → copy
the last ~40 lines. The first `error:` line names the file.

### 1.2 Capture a 1.29 baseline BEFORE replacing binaries

The parity check in 1.5 needs a "before". Do this once, on the old
binaries, with the radio connected and receiving:

```powershell
dotnet run --project OpenhpsdrZeus
# in a second shell, once the app is up and connected:
powershell -NoProfile -File tools\capture-dsp-modernization-bundle.ps1 -BaseUrl http://localhost:6060 -OutputRoot captures\dsp-modernization -Label wdsp129-baseline
```

Expected: a new folder under `captures/dsp-modernization/` (gitignored)
holding the modernization snapshot, live diagnostics, benchmark plan and
manifest.

### 1.3 Pull the rebuilt binaries into the tree

From the green run's **Artifacts** section download all five:
`wdsp-windows-x64`, `wdsp-windows-arm64`, `wdsp-linux-x64`,
`wdsp-linux-arm64`, `wdsp-osx-arm64`. Each zip is the contents of one
`Zeus.Dsp/runtimes/<rid>/native/` folder. Unzip over the matching folder:

```powershell
Expand-Archive -Force wdsp-windows-x64.zip   Zeus.Dsp\runtimes\win-x64\native\
Expand-Archive -Force wdsp-windows-arm64.zip Zeus.Dsp\runtimes\win-arm64\native\
Expand-Archive -Force wdsp-linux-x64.zip     Zeus.Dsp\runtimes\linux-x64\native\
Expand-Archive -Force wdsp-linux-arm64.zip   Zeus.Dsp\runtimes\linux-arm64\native\
Expand-Archive -Force wdsp-osx-arm64.zip     Zeus.Dsp\runtimes\osx-arm64\native\
git status --short Zeus.Dsp/runtimes
```

Expected: five modified `wdsp.dll` / `libwdsp.so` / `libwdsp.dylib` files,
each roughly 8 MB larger than before (the two NNR models).

### 1.4 Verify the Windows binary locally

```powershell
# every symbol Zeus imports resolves — expect "Binary missing: 0"
powershell -NoProfile -ExecutionPolicy Bypass -File tools\audit-wdsp-native-symbols.ps1 -BinaryPath Zeus.Dsp\runtimes\win-x64\native\wdsp.dll -RequireBinaryExports

# packaged-artifact audit — expect no missing current-NR symbols
powershell -NoProfile -ExecutionPolicy Bypass -File tools\audit-wdsp-runtime-artifacts.ps1 -FailOnMissingWinX64CurrentNr

# version — expect 210
Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public static class W { [DllImport(@"Zeus.Dsp\runtimes\win-x64\native\wdsp.dll", CallingConvention=CallingConvention.Cdecl)] public static extern int GetWDSPVersion(); }'
[W]::GetWDSPVersion()

# DSP tests — expect 0 failed (the win-x64 artifact test now sees NR4 + NNR exports)
dotnet test tests\Zeus.Dsp.Tests\Zeus.Dsp.Tests.csproj -c Debug
```

Then launch the app once. The first start regenerates FFTW wisdom because
the binary hash changed; the log shows `wdsp.wisdom initialising` and the
status string progresses through the FFT sizes. On a desktop this is
under a minute, on a Raspberry Pi expect several minutes. Do not kill it.

### 1.5 Parity check against the 1.29 baseline

With the new binaries in place, same radio, same band, same settings as
1.2:

```powershell
powershell -NoProfile -File tools\capture-dsp-modernization-bundle.ps1 -BaseUrl http://localhost:6060 -OutputRoot captures\dsp-modernization -Label wdsp210
powershell -NoProfile -File tools\run-dsp-wdsp-fixture-matrix.ps1 -BundleDir captures\dsp-modernization\<wdsp210 folder>
powershell -NoProfile -File tools\summarize-dsp-native-stage-timing.ps1 -BundleDir captures\dsp-modernization\<wdsp210 folder> -FailOnBudget
```

Expected: the fixture matrix reports no regression against the
`current-zeus` comparison; the stage-timing summary stays within the
default budgets (250 ms per stage, 1 s per run). If RX stage time grew,
that is the new half-band decimator (`reshb.c`), and the number is what
the Pi decision in 1.6 needs.

Also listen: NR1, NR2 (with and without post2), NR4, AGC behaviour and the
panadapter noise floor should be indistinguishable from 1.29 by ear and
on screen.

### 1.6 Raspberry Pi 4 timing

Follow `docs/lessons/raspberry-pi-deployment.md` with the `linux-arm64`
runtime folder from 1.3, then repeat the stage-timing capture there with
RX1 and RX2 both open. Record the per-stage numbers in the plan document
under "Phase 1 landing notes". This is the gate for whether NR5 may be
offered on the Pi at all (phase 2, step 2.5).

### 1.7 PureSignal smoke test on the G2 (KB2UKA)

No PS code changed on the managed side, but the native calibration is a
rewrite (PureSignal 3). Bench checklist, in this order:

1. Arm PS, key up on a two-tone, confirm calibration completes
   (`info[5]` increments; the panel shows the cal state advancing).
2. Disarm while **not** keyed — this exercises the seven-zero-block drain
   in `SetPsEnabled` against the new state machine. Re-arm; it must
   calibrate again cleanly.
3. Disarm **while keyed**; re-arm. Same expectation.
4. Save a correction file, restart the app, restore it, confirm
   correction applies without a fresh calibration.
5. Drive the amplifier deliberately too hard once: PS3 should refuse to
   calibrate and `info[6]` should read 2 (over-drive). Back off; it should
   resume.
6. Compare two-tone IMD with the 1.29 numbers, but read the guide's
   Appendix D first: a few dB worse close-in on a two-tone with the same or
   better noise-test regrowth is expected behaviour, not a regression.

Anything unexpected here stops the merge; PS is a hard-rule subsystem.

### 1.8 Commit the binaries, track, and open the PR

1. Decide the open question from the plan: keep binaries checked in, or
   move to LFS / CI-only. If keeping them:
   ```bash
   git add Zeus.Dsp/runtimes
   git commit -m "wdsp: refresh native runtimes to WDSP 2.1.0 (workflow run <id>)"
   git push
   ```
2. Create the tracking issue (`bd` was not installed on the machine that did
   the port):
   ```bash
   bd create "WDSP 2.1.0 port — phases 1 and 2" -t task -p 1 -d "See docs/designs/wdsp-2.1.0-upgrade-plan.md and wdsp-2.1.0-runbook.md"
   bd dolt push --remote origin
   ```
3. Open the GitHub issue for phase 3 (PureSignal 3 follow-ups) so KB2UKA
   can approve the approach before any code.
4. PR. The branch was cut from `freedv-in-core`, which is 293 commits
   ahead of `main`. Two options:
   - **After FreeDV merges:** open the PR from `wdsp-2.1.0` as-is.
   - **Now:** replay the three commits onto `main`:
     ```bash
     git fetch origin
     git checkout -b wdsp-2.1.0-main origin/main
     git cherry-pick 174f82b 9430cfb 206e8b4
     # resolve any Zeus.Dsp conflicts (the freedv branch touched NativeMethods / WdspDspEngine)
     git push -u origin wdsp-2.1.0-main
     ```
   In the PR description list the red-light items for maintainer review:
   the `StateDto` / `NrConfig` wire additions, the NR5 cycle position and
   label, and the fact that the PureSignal shim is a native compatibility
   layer with zero managed-side PS change.

---

## Phase 2 — put NR5 in front of an operator

Prerequisite: step 1.3 done and the app restarted. Until then NR5 is
hidden and every check below reads "unavailable".

### 2.1 Confirm the server sees NNR

```bash
curl -s http://localhost:6060/api/state | jq '{wdspNnrAvailable, nnrPremiumModelAvailable, nnrModelSlotInUse, nr: .nr.nrMode}'
```

Expected on first look: `wdspNnrAvailable: true`,
`nnrPremiumModelAvailable: true` (CI builds with `WDSP_WITH_NNR_PREMIUM=ON`),
`nnrModelSlotInUse: null` (NR5 not running yet). The server log carries
`wdsp.nnr.probe premiumModel=True` once per engine.

### 2.2 Drive it through the API

```bash
# select NR5 (send the whole NR block; take the current one from /api/state and change nrMode)
curl -s http://localhost:6060/api/rx/nr -H 'content-type: application/json' \
  -d '{"nr":{"nrMode":"Nnr","anfEnabled":false,"snbEnabled":false,"nbpNotchesEnabled":false,"nbMode":"Off","nbThreshold":20}}' | jq '.nnrModelSlotInUse'
# expect 0

# tune it
curl -s http://localhost:6060/api/rx/nnr -H 'content-type: application/json' -d '{"maskFloorDb":-35}' | jq '.nr.nnrMaskFloorDb'
curl -s http://localhost:6060/api/rx/nnr -H 'content-type: application/json' -d '{"modelSlot":1}'   | jq '.nnrModelSlotInUse'
# expect -35, then 1 (or 0 if the build has no Premium model — that is the discovery path working)

# reject bad input
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:6060/api/rx/nnr -H 'content-type: application/json' -d '{"modelSlot":7}'
# expect 400
```

Restart the backend afterwards and read `/api/state` again: `nr.nrMode`,
`nr.nnrMaskFloorDb` and `nr.nnrModelSlot` must survive the restart (they
live in `zeus-prefs.db`).

### 2.3 Walk the UI

1. DSP panel: click the NR button repeatedly. Cycle should read
   NR → NR2 → (NR3 if a model is active) → NR4 → **NR5** → off. Hover on
   NR5 shows the tooltip with the +51 ms note.
2. With NR5 selected, open the settings accordion: "NR5 — NNR" with a
   Model radio (Standard / Premium), an "In use" caption that follows what
   the engine reports, and a Mask floor gauge from −50 to −10.
3. Switch Standard ↔ Premium while audio is running: no click or dropout
   (the guide says the switch is inaudible).
4. Drag the mask floor from −50 to −10: quietest at −50, progressively
   more band noise let through toward −10.
5. Smart NR: enable it, then temporarily point the app at the old 1.29
   binary (or a build with `WDSP_WITH_NR3=OFF` etc.) and confirm a
   persisted NR5 falls back to NR2 with the note "NR5/NNR unavailable in
   the active WDSP build".
6. Mobile layout and G2 front-panel stack show the label **NR5**.

### 2.4 Listen and measure

1. A/B against NR2 and NR4 on a weak SSB signal in real band noise. The
   guide's claim to verify: NR5 removes noise "without leaving a
   signature" and handles static crashes better; at very poor SNR raising
   the floor should make the weak signal *more* readable, not less.
2. CPU: with NR5 Standard on RX1, then on RX1 + RX2, then Premium, read
   the native stage timing (same tool as 1.5). Guide reference points on a
   desktop core: ~10 % Standard, ~32 % Premium, both +51 ms.

### 2.5 Raspberry Pi decision

Repeat 2.4 step 2 on the Pi 4 from 1.6. If two receivers on Standard
cannot hold real time, the follow-up is a capability flag from
`/api/radio/capabilities` that greys NR5 with the reason. Record the
numbers; do not ship NR5 on the Pi until this is decided.

### 2.6 Sign-offs and follow-ups

Decisions that need Brian (EI6LF) or Doug (KB2UKA):

- NR5 at the end of the NR cycle, or somewhere else.
- Button label "NR5" and the +51 ms tooltip wording.
- Whether NR5 should replace NR3 (RNNoise) — the guide's own comparison
  favours NNR; this is the phase 5 retirement question.

Follow-ups to file once the placement is approved, each a small PR:
MIDI `Nr5OnOff`, TCI level mapping, G2 front-panel cycle entry, the
optional `wdsp_nnr_{0,1}.bin` override store, the Pi capability gate.

---

## If something goes wrong

| Symptom | Where to look |
| --- | --- |
| Workflow job red on **Build** | expand the step; the first `error:` line. Linux/macOS errors that mention a system header are usually a name collision — see `native/wdsp/ZEUS-PATCHES.md` (the `dprintf` case). |
| `Binary missing` in the symbol audit | the run built with an NR flag off, or a stale artifact was unzipped into the wrong RID folder. |
| App starts but NR5 never appears | `wdspNnrAvailable` false in `/api/state` → the loaded library is not 2.1.0. Check which `wdsp.dll` was actually resolved (`WdspNativeLoader` log line) — a stray copy elsewhere on the path shadows the bundled one. |
| NR5 selected but audio unchanged | `nnrModelSlotInUse` null → the engine's `SetRXANNRRun` threw `EntryPointNotFound` and logged `wdsp.nnr.unavailable`. Same root cause as above. |
| PS will not calibrate after the upgrade | read `info[6]`: 2 means over-drive refusal (new in PS3, by design). Otherwise stop and report; PS is a hard-rule subsystem. |
| First start hangs for minutes | wisdom regeneration; wait for `FFTW planning complete`. |
