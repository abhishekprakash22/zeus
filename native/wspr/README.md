# native/wspr — WSPR decoder + encoder (libzeus_wspr)

Vendored WSPR DSP behind a flat C shim (`zeus_wspr.c`), same shape as
`native/ft8`:

- **Decoder**: K9AN `wsprd` — the WSJT-X WSPR decoder — in the librarified
  form maintained in Guenael Jouchet's `rtlsdr-wsprd` project
  (`wsprd.c`, `wsprd_utils.c`, `wsprsim_utils.c`, `fano.c`, `nhash.c`,
  `tab.c`, headers). GPL-2.0-or-later, same as Zeus. See `LICENSE.wsprd`.
- **Encoder**: `get_wspr_channel_symbols()` from `wsprsim_utils.c` — the
  bit-exact WSJT-X message packing / K=32 convolution / interleave. The
  beacon keyer uses this so no bit-twiddling is ever hand-ported to C#.

## Shim ABI (`zeus_wspr.h` equivalents in `zeus_wspr.c`)

    int zeus_wspr_abi_version(void);                     // == 1
    int zeus_wspr_decode(const float* idat, const float* qdat,
                         int samples,                    // 45000 = 120 s
                         int dial_freq_hz,
                         zeus_wspr_spot* out, int max_out);
    int zeus_wspr_encode(const char* "CALL GRID DBM",
                         unsigned char symbols[162]);    // 0 on success

Decoder input: 120 s of complex baseband at **375 Hz**, centred so the
classic 1500 Hz WSPR audio window sits at 0 Hz. The C# side
(`Digital/WsprService.MixAndDecimate32`) owns that conversion.

## Building

    ./build.sh                                           # native arch
    CC=aarch64-linux-gnu-gcc ./build.sh libzeus_wspr-arm64.so \
        FFTW=/path/to/runtimes/linux-arm64/native/libfftw3f.so.3

Depends on single-precision FFTW (headers from the host's libfftw3-dev;
when cross-compiling, link the staged target `libfftw3f.so.3` directly via
`FFTW=`). Plans use `FFTW_ESTIMATE` — no wisdom file, instant startup.
Outputs stage into `Zeus.Dsp/runtimes/{rid}/native/libzeus_wspr.so`.

Validated end-to-end in-tree: native encode → synthesized slot →
`MixAndDecimate32` → native decode round-trips the message (see
`tests/Zeus.Server.Tests/WsprTests.cs`).
