# native/ft8 — FT8/FT4 decoder

Vendors **ft8_lib** (Kārlis Goba — MIT, see LICENSE.ft8_lib) plus `zeus_ft8.c`,
a flat shim exposing two symbols for P/Invoke from
`Zeus.Server.Hosting/Digital/Ft8Native.cs`:

    int zeus_ft8_version(void);
    int zeus_ft8_decode(const float* audio, int n, int rate, int is_ft4,
                        zeus_ft8_decode_t* out, int max_out);

The shim keeps ft8_lib's monitor/candidate/decode machinery on the C side and
returns a plain array of PODs, so only blittable structs cross the boundary.
Input may be any sample rate — it resamples to 12 kHz internally (the RX tap
delivers 48 kHz).

## You do not normally need to build this

Prebuilt libraries are committed under `Zeus.Dsp/runtimes/{rid}/native/` and are
copied by `dotnet publish` automatically:

    linux-arm64/native/libzeus_ft8.so   (Raspberry Pi 4/5, 64-bit)
    linux-x64/native/libzeus_ft8.so

## Rebuilding

    ./build.sh                  # native arch -> libzeus_ft8.so

gcc + libm only. No CMake, no FFTW — ft8_lib vendors kiss_fft.

Cross-compiling for the Pi from an x86 Linux box:

    CC=aarch64-linux-gnu-gcc ./build.sh

Then copy the result over `Zeus.Dsp/runtimes/linux-arm64/native/libzeus_ft8.so`.
