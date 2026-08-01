#!/usr/bin/env bash
# Build libzeus_wspr.so — vendored K9AN wsprd decoder + wsprsim encoder + shim.
#
#   ./build.sh                          -> native arch
#   CC=aarch64-linux-gnu-gcc ./build.sh libzeus_wspr-arm64.so \
#       FFTW=/path/to/runtimes/linux-arm64/native/libfftw3f.so.3
#
# Depends on single-precision FFTW (fftw3f). Headers come from the build
# host's libfftw3-dev (arch-neutral); when cross-compiling, link directly
# against the staged target .so via FFTW= instead of -lfftw3f.
set -euo pipefail
cd "$(dirname "$0")"
OUT=${1:-libzeus_wspr.so}
CC=${CC:-gcc}
FFTW=${FFTW:--lfftw3f}
CFLAGS="-O3 -fPIC -I. -Wno-unused-result"
echo "compiling wsprd + shim ($CC -> $OUT)..."
rm -rf .obj && mkdir -p .obj
for s in wsprd.c wsprd_utils.c wsprsim_utils.c fano.c nhash.c tab.c; do
  $CC $CFLAGS -c "$s" -o ".obj/${s%.c}.o"
done
$CC $CFLAGS -shared -fvisibility=default -o "$OUT" zeus_wspr.c .obj/*.o $FFTW -lm
echo "built $OUT"
${NM:-nm} -D --defined-only "$OUT" | grep zeus_wspr || true
