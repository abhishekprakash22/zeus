# Zeus patches over upstream WDSP

Upstream baseline: **WDSP 2.11** (Warren Pratt, NR0V, 2026-10-15), vendored
from `TAPR/OpenHPSDR-wdsp/wdsp 2.11/Source` with line endings normalised to LF
(several 2.11 files ship CRLF; the content is otherwise verbatim).
`GetWDSPVersion()` returns `211`.

Re-vendor recipe: three-way merge each edited file with the previous upstream
drop as the base (`git merge-file ours prev-upstream new-upstream`), copy every
unedited file straight over, then re-check this list.

This file is the manifest every re-vendor must be replayed from. If a file is
not listed here it is byte-identical to upstream. Keep it that way: when a new
drop lands, copy upstream over the tree, then re-apply exactly what is below.

## Vendoring rules

Copy every upstream `*.c` / `*.h` **except**:

| Upstream file | Action | Why |
| --- | --- | --- |
| `fftw3.h` | do not copy | System / vcpkg FFTW headers are the source of truth. Windows local builds point `FFTW_ROOT` at a folder holding `fftw3.h` next to the import libraries. |
| `calculus` (binary, no extension) | do not copy | Optional runtime override for the NR2 gain table; `calculus.c` embeds the same data. |
| `*.vcxproj`, `*.sln`, `Makefile`, `.o` | do not copy | Zeus owns the build (`CMakeLists.txt`). |

## Zeus-owned files (not upstream)

| File | Purpose |
| --- | --- |
| `CMakeLists.txt` | The build. Source list, NR3/NR4/NNR-Premium gates, FFTW detection, output naming. Per-compiler warning downgrades: upstream is written against MSVC, so clang / clang-cl / GCC-14 hard errors for implicit int, int↔pointer conversions and incompatible pointer types are turned back into warnings (`-Wno-…`). Implicit function declarations are the exception: hard ERRORS (`-Werror=implicit-function-declaration`) — the old downgrade hid a 32-bit pointer truncation on `_aligned_malloc` that segfaulted the first TXA open off Windows. Linux links with `-Wl,--no-undefined` + explicit `-lm` so undefined symbols fail in CI instead of deferring to load time (macOS is already strict by default). |
| `wdsp_export.h` | `WDSP_EXPORT` visibility macro that `PORT` resolves to. |
| `linux_port.{c,h}` | Win32 → POSIX shim (threads, critical sections, semaphores, events, aligned malloc). 2.1.0 additions: `WaitForMultipleObjects`, `CreateSemaphoreW`, `WAIT_OBJECT_0` / `WAIT_TIMEOUT`, `GetCurrentThread`, `OutputDebugStringA`; `WaitForSingleObject(h, 0)` now performs one try and reports `WAIT_TIMEOUT` (calcc.c drains semaphores with that call).; real `_aligned_malloc` / `_aligned_free` symbols (posix_memalign / free). Since 2.11 the standalone files use upstream's `wdsp_align.h` (static inline) instead, so these symbols are a safety net only; comm.h users still get the `linux_port.h` macros. |
| `zeus_compat.{c,h}` | `psccF` — the Thetis float-I/Q wrapper around `pscc` that upstream 2.x dropped. Keeps `Zeus.Dsp` P/Invoke and the PureSignal feedback pump unchanged (PureSignal hard rule in `CLAUDE.md`). |
| `rnnr.{c,h}` | NR3 (RNNoise). Thetis lineage (MW0LGE), never upstream. Gated by `WDSP_WITH_NR3`. |
| `sbnr.{c,h}` | NR4 (libspecbleach). Thetis lineage, never upstream. Gated by `WDSP_WITH_NR4`. |
| `stubs/nr3/`, `stubs/nr4/` | No-op replacements + opaque headers used when the matching gate is OFF. |
| `stubs/nnr/nnr_model_1_stub.c`, `stubs/nnr/nnr_model_3_stub.c` | Empty Premium NNR models (SSB slot 1, CW slot 3) used when `WDSP_WITH_NNR_PREMIUM=OFF`; those slots report themselves empty. |
| `ZEUS-PATCHES.md` | This file. |

## Edited upstream files

Every edit is marked in-source with a `Zeus` comment.

| File | Edit |
| --- | --- |
| `comm.h` | (1) Platform include block: Linux/macOS include `linux_port.h`; `<Windows.h>`, `<process.h>`, `<intrin.h>`, `<avrt.h>` guarded by `_WIN32`. (2) `#define dprintf wdsp_dprintf` right after the system headers: upstream's debug helper `void dprintf(const char*, ...)` collides with POSIX `dprintf(int, ...)` in glibc / macOS `<stdio.h>`; the macro renames every WDSP-side use without touching upstream files. (3) Include `rnnr.h`, `sbnr.h` immediately after `emnr.h` (they must precede `RXA.h`, whose struct embeds the handle types) and `zeus_compat.h` after the upstream block headers. (4) `#define PORT WDSP_EXPORT` via `wdsp_export.h` instead of `__declspec(dllexport)`. (Edit (5), `<limits.h>` for `wbfm.c`, was retired in 2.11: upstream now includes it in `wbfm.c`.) |
| `snoop.c` | `void xsnoop(channel)` → `void xsnoop(int channel)`. Implicit `int` is an error under clang / clang-cl (the Windows CI toolset) and GCC 14. |
| `main.c` | MMCSS "Pro Audio" calls (`AvSetMmThreadCharacteristics` etc.) guarded by `_WIN32`. |
| `wisdom.c` | `AllocConsole` / `freopen_s` / `FreeConsole` progress console guarded by `_WIN32`. |
| `channel.c` | `_MM_SET_FLUSH_ZERO_MODE` guarded out on Linux, macOS and ARM64 (no SSE intrinsics). |
| `utilities.c` | `NewCriticalSection` / `DestroyCriticalSection` (Thetis VAC helpers) and the developer raw-audio capture block (`WriteAudioFile` … `WriteScaledAudio`) guarded to Windows, as in the 1.29 tree. |
| `RXA.h` | `rnnr` and `sbnr` block members added to the RXA struct after `nnrbypass`; `RXAbp1CheckEx` prototype (5th argument is `nnrbypass_run`, matching 2.11's `RXAbp1Check`). |
| `RXA.c` | NR3/NR4 create / destroy / setSamplerate / setSize / setBuffers hooks next to the upstream `nnrbypass` hooks. In `xrxa` the pre-AGC `xrnnr`/`xsbnr` calls sit inside upstream's `if (!getRun_nnrbypass(...))` block after `xemnr(…,0)` — a CW NNR model bypasses them exactly as it bypasses ANF/ANR/EMNR — and the post-AGC calls follow `xnnr(…,1)`. `RXAbp1CheckEx` (adds `rnnr_run`, `sbnr_run`) with `RXAbp1Check` kept as the upstream-signature wrapper. `RXAbp1Set` counts the NR3/NR4 run flags and `RXAbp1Position` their run+position, because from 2.11 bp1's position is derived there rather than written by each NR's position setter. |
| `calcc.c` | `destroy_calcc` releases the `psccF` staging buffers (`zeus_compat_release_pscc_buffers`). |
| `nnrbypass.c` | New in 2.11 and, like the NURBS files, does not include `comm.h`: its `<malloc.h>` / `<Windows.h>` are guarded by `_WIN32`, and off Windows it includes `linux_port.h` (CRITICAL_SECTION for `firmin.h`, `_aligned_malloc`). |

## Retired with 2.11 (upstream fixed them)

- `extrapolate.c`, `nurbs_fit.c`, `nurbs_spline.c` — our `_aligned_malloc`
  prototypes are gone; upstream includes its own `wdsp_align.h`.
- `comm.h` edit (5), `<limits.h>` for `wbfm.c`.

## Fork-owned files touched by 2.11

- `rnnr.c`, `sbnr.c`: `RXAbp1CheckEx` callers pass
  `getRun_nnrbypass(...)` (2.11 changed that argument's meaning), and
  `SetRXARNNRPosition` / `SetRXASBNRPosition` call `RXAbp1Set` instead of
  writing `bp1->position`, which 2.11 recomputes on every `RXAbp1Set`.

## ANAN Core API addition (not upstream)

- `osctrl.c` / `wdsp.h`: `SetTXAosctrlBandwidth (int channel, double
  bandwidth)` — settable CESSB overshoot-controller design bandwidth
  (clamped 300..12000 Hz, rebuilt under `csDSP` on change). Upstream fixes it
  at 3000 Hz. Raise with Warren before the next re-vendor (feature needs are
  a conversation with him, not a modification).

## Things that look like patches but are not

- `wdsp.h` is upstream's auto-generated public header. Its `WDSP_API` macro
  falls back to empty off Windows; symbol visibility comes from `PORT` on the
  definitions, so no edit is needed. Nothing in the tree includes it.
- `impulse_cache.{c,h}` select a 64-bit hash only under `_WIN64`; other
  platforms use the 32-bit path. Upstream behaviour, left alone.
- `nnet.c` already guards its profiling timer (`QueryPerformanceCounter`) and
  `_fullpath` behind `_WIN32`.

## Verifying a re-vendor

```sh
# 1. Every symbol Zeus imports must still exist (fails on missing exports)
powershell -File tools/audit-wdsp-native-symbols.ps1 -BinaryPath <libwdsp> -RequireBinaryExports
# 2. Version
nm -gU <libwdsp> | grep GetWDSPVersion     # then call it: expect 211
# 3. Thetis-lineage exports survived the splice
nm -gU <libwdsp> | grep -cE 'SetRXASBNR'   # 8
nm -gU <libwdsp> | grep -cE 'RNNR'         # 4
nm -gU <libwdsp> | grep -cE 'NNR'          # includes SetRXANNR* (11) + SetNNRModelPath*
nm -gU <libwdsp> | grep psccF              # 1
```
