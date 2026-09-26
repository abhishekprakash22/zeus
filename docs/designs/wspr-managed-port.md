# WSPR in managed C# — evaluation (zeus-n3im)

**Decision: GO.** Port the vendored `wsprd` decoder and `wsprsim` encoder
(`native/wspr`) to C#. Keep the native library as the reference until the
managed path matches it on a golden corpus, then drop it.

## Why

- **Every platform needs its own native build.** `libzeus_wspr` is built per
  RID (linux-x64, linux-arm64, osx-arm64) and linked against FFTW. That has
  already left **Windows without WSPR** (zeus-ez81: `zeus_wspr.dll` was never
  built or shipped). A managed decoder removes the problem by construction,
  as the pure-C# SSTV decoder did for that mode.
- **Two latent defects in the native decoder go away:**
  - `wspr_decode()` **writes `fftw_wisdom.dat` into the process working
    directory** on every slot. It also tries to read it back first.
  - `sync_and_demodulate()` keeps `static float fplast` across calls. It is
    harmless while Zeus decodes one slot at a time, but the function is not
    reentrant.
  - `get_wspr_channel_symbols()` **crashes the process** on a message it
    mistakes for type 1 but that has no grid token (e.g. `"HELLO"`: strtok
    returns NULL and the grid is dereferenced). Zeus always builds
    "CALL GRID DBM", so it is not reachable today; the managed encoder returns
    false.

## What there is to port

| File | Lines | Content | Notes |
|---|---|---|---|
| `wsprd.c` | 874 | Candidate search (averaged spectrum, noise percentile, local maxima), coarse sync/drift search, `sync_and_demodulate` (modes 0/1/2), `subtract_signal2`, two-pass loop, de-dupe | Straight numeric C. FFTW is used for **one** 512-point complex FFT per block (~350 blocks per pass). |
| `fano.c` | 238 | Fano sequential decoder + K=32 convolutional `encode` | Integer arithmetic, deterministic, so it can be ported exactly. |
| `wsprd_utils.c` | 313 | `unpk_`, `unpack50`, `unpackcall`, `unpackgrid`, `unpackpfx`, `deinterleave` | Bit and string manipulation. |
| `wsprsim_utils.c` | 317 | `get_wspr_channel_symbols`: pack, convolve, interleave, sync merge | Must be bit-exact with WSJT-X. |
| `nhash.c` | 451 | Bob Jenkins lookup3 `hashlittle` (mostly comments) | Needs published test vectors. |
| `metric_tables.h`, `tab.c` | 180 | Fano metric table, parity table | Data. |

About 2.3k lines of C, roughly 1.2–1.5k lines of C#.

## Performance

Measured on the development Mac (Apple Silicon) with a synthesized 120 s slot
holding 5 beacons (−5 to −19 dB) in Gaussian noise:

| Stage | Time |
|---|---|
| `MixAndDecimate32` (already C#) | 220 ms |
| native `wspr_decode` | 115–121 ms, 5/5 decoded |

The budget is the slot itself (~100 s after the boundary). A managed port that
is several times slower than native, running on a Raspberry Pi (~5–10× slower),
still lands in single-digit seconds. A simple managed radix-2 FFT is enough, so
there is no need for FFTW or a new package.

## How: keep native as the oracle

1. **Encoder first.** `WsprEncoder` in C#, with a test asserting its 162 channel
   symbols equal `WsprNative.Encode` for a message table: type-1 calls,
   prefixed/suffixed calls (type 2), and hashed type-3 messages.
2. **Hash.** A lookup3 port, checked against published vectors and against the
   native `nhash` through the encoder above.
3. **Fano + unpack.** Feed identical soft symbols into both implementations;
   the decoded bits must be identical.
4. **Decoder.** A managed `WsprDecoder.Decode(I, Q, dialHz)` with the same
   options (2 passes, subtraction on, no hash-table file). The golden tests
   decode a corpus with **both** implementations and compare the spot lists:
   message and drift must be equal; SNR, dt and frequency must agree within
   small tolerances. Corpus:
   - the existing loopback (`WsprTests`);
   - synthesized multi-signal slots at a range of SNRs and drifts;
   - **real recorded slots**, a few 120 s captures from 20 m and 40 m, stored
     as 375 Hz IQ (≈360 KB each).
5. **Switch** `WsprService` to the managed decoder and encoder. Keep
   `WsprNative` only in the test project as the oracle for one release, then
   delete `native/wspr`, its CI legs and the per-RID libraries.

Exactness caveats:
- `cosf`/`sinf` versus `MathF.Cos`/`MathF.Sin`, and the order of float
  accumulation, can change **borderline** weak decodes. The acceptance bar is
  therefore "same spots on the corpus", not bit-identical intermediates.
- The Fano, packing and hash paths are integer code and must be exact.

## Effort

Roughly 2–3 focused sessions:

| Work | Sessions |
|---|---|
| Encoder + hash | ½ |
| Fano + unpack | ½ |
| Decoder | 1 |
| Corpus + golden tests + switch-over | ½–1 |

## Consequences

- **Windows gets WSPR** (closes zeus-ez81 without building a DLL).
- **Build:** `native/wspr` and the `zeus_wspr` build steps in
  `build-native-libs.yml` go away.
- **FFTW stays shipped:** WDSP still depends on it; only WSPR's dependency on
  it goes.
- **Licence:** the port keeps the original copyright headers (K1JT, K9AN,
  VA2GKA) and the GPL notice. **Open question:** `wsprd.c`'s header says **GPL
  v3 or later**, while `native/wspr/README.md` describes the vendored tree as
  GPL-2.0-or-later. Reconcile this before the port lands; it applies to the
  native code shipping today just as much.
