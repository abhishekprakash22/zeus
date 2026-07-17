#!/usr/bin/env bash
# Build libzeus_ft8.so — run this ON THE PI (or any Linux box matching the target).
#
#   ./build.sh            -> libzeus_ft8.so for the native arch
#
# No CMake, no deps beyond gcc + libm. ft8_lib vendors its own FFT (kiss_fft).
set -euo pipefail
cd "$(dirname "$0")"
OUT=${1:-libzeus_ft8.so}
CC=${CC:-gcc}
CFLAGS="-O3 -DHAVE_STPCPY -fPIC -I."
echo "compiling ft8_lib + shim..."
rm -rf .obj && mkdir -p .obj
for s in ft8/*.c common/*.c fft/*.c; do
  $CC $CFLAGS -c "$s" -o ".obj/$(echo "$s" | tr '/' '_').o"
done
$CC $CFLAGS -shared -fvisibility=hidden -o "$OUT" zeus_ft8.c .obj/*.o -lm
echo "built $OUT"
${NM:-nm} -D --defined-only "$OUT" | grep zeus_ft8 || true
