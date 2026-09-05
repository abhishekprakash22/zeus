# Zeus patches over upstream WDSP

Upstream baseline: **WDSP 2.1.0** (Warren Pratt, NR0V, 2026-09-04), vendored
verbatim from the `2026-09-04 WDSP, ver 210` source drop. `GetWDSPVersion()`
returns `210`.

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
| `CMakeLists.txt` | The build. Source list, NR3/NR4/NNR-Premium gates, FFTW detection, output naming. Per-compiler warning downgrades: upstream is written against MSVC, so clang / clang-cl / GCC-14 hard errors for implicit int, implicit function declarations, int↔pointer conversions and incompatible pointer types are turned back into warnings (`-Wno-…`). |
| `wdsp_export.h` | `WDSP_EXPORT` visibility macro that `PORT` resolves to. |
| `linux_port.{c,h}` | Win32 → POSIX shim (threads, critical sections, semaphores, events, aligned malloc). 2.1.0 additions: `WaitForMultipleObjects`, `CreateSemaphoreW`, `WAIT_OBJECT_0` / `WAIT_TIMEOUT`, `GetCurrentThread`, `OutputDebugStringA`; `WaitForSingleObject(h, 0)` now performs one try and reports `WAIT_TIMEOUT` (calcc.c drains semaphores with that call). |
| `zeus_compat.{c,h}` | `psccF` — the Thetis float-I/Q wrapper around `pscc` that upstream 2.x dropped. Keeps `Zeus.Dsp` P/Invoke and the PureSignal feedback pump unchanged (PureSignal hard rule in `CLAUDE.md`). |
| `rnnr.{c,h}` | NR3 (RNNoise). Thetis lineage (MW0LGE), never upstream. Gated by `WDSP_WITH_NR3`. |
| `sbnr.{c,h}` | NR4 (libspecbleach). Thetis lineage, never upstream. Gated by `WDSP_WITH_NR4`. |
| `stubs/nr3/`, `stubs/nr4/` | No-op replacements + opaque headers used when the matching gate is OFF. |
| `stubs/nnr/nnr_model_1_stub.c` | Empty Premium NNR model used when `WDSP_WITH_NNR_PREMIUM=OFF`; slot 1 reports itself empty. |
| `ZEUS-PATCHES.md` | This file. |

## Edited upstream files

Every edit is marked in-source with a `Zeus` comment.

| File | Edit |
| --- | --- |
| `comm.h` | (1) Platform include block: Linux/macOS include `linux_port.h`; `<Windows.h>`, `<process.h>`, `<intrin.h>`, `<avrt.h>` guarded by `_WIN32`. (2) `#define dprintf wdsp_dprintf` right after the system headers: upstream's debug helper `void dprintf(const char*, ...)` collides with POSIX `dprintf(int, ...)` in glibc / macOS `<stdio.h>`; the macro renames every WDSP-side use without touching upstream files. (3) Include `rnnr.h`, `sbnr.h` immediately after `emnr.h` (they must precede `RXA.h`, whose struct embeds the handle types) and `zeus_compat.h` after the upstream block headers. (4) `#define PORT WDSP_EXPORT` via `wdsp_export.h` instead of `__declspec(dllexport)`. (5) `#include <limits.h>` after `<stdint.h>`: `wbfm.c` uses `INT_MAX`; MSVC pulls the header in transitively via `<Windows.h>`, glibc and macOS clang do not. |
| `snoop.c` | `void xsnoop(channel)` → `void xsnoop(int channel)`. Implicit `int` is an error under clang / clang-cl (the Windows CI toolset) and GCC 14. |
| `main.c` | MMCSS "Pro Audio" calls (`AvSetMmThreadCharacteristics` etc.) guarded by `_WIN32`. |
| `wisdom.c` | `AllocConsole` / `freopen_s` / `FreeConsole` progress console guarded by `_WIN32`. |
| `channel.c` | `_MM_SET_FLUSH_ZERO_MODE` guarded out on Linux, macOS and ARM64 (no SSE intrinsics). |
| `utilities.c` | `NewCriticalSection` / `DestroyCriticalSection` (Thetis VAC helpers) and the developer raw-audio capture block (`WriteAudioFile` … `WriteScaledAudio`) guarded to Windows, as in the 1.29 tree. |
| `RXA.h` | `rnnr` and `sbnr` block members added to the RXA struct after `nnr`; `RXAbp1CheckEx` prototype. |
| `RXA.c` | NR3/NR4 create / destroy / x (pre- and post-AGC) / setSamplerate / setSize / setBuffers hooks next to the upstream `nnr` hooks; `RXAbp1CheckEx` (adds `rnnr_run`, `sbnr_run`) with `RXAbp1Check` kept as the upstream-signature wrapper; `RXAbp1Set` considers the NR3/NR4 run flags. |
| `calcc.c` | `destroy_calcc` releases the `psccF` staging buffers (`zeus_compat_release_pscc_buffers`). |

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
nm -gU <libwdsp> | grep GetWDSPVersion     # then call it: expect 210
# 3. Thetis-lineage exports survived the splice
nm -gU <libwdsp> | grep -cE 'SetRXASBNR'   # 8
nm -gU <libwdsp> | grep -cE 'RNNR'         # 4
nm -gU <libwdsp> | grep -cE 'NNR'          # includes SetRXANNR* (11) + SetNNRModelPath*
nm -gU <libwdsp> | grep psccF              # 1
```
