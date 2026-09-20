#!/usr/bin/env bash
# Build the zeus_ft8 shared library for the host — or for Windows, with a
# mingw-w64 cross compiler.
#
#   ./build.sh                                  -> libzeus_ft8.so  (Linux)
#   ./build.sh libzeus_ft8.dylib                -> macOS
#   CC=x86_64-w64-mingw32-gcc ./build.sh zeus_ft8.dll   -> Windows, cross
#
# No CMake, no deps beyond a C compiler + libm. ft8_lib vendors its own FFT
# (kiss_fft).
set -euo pipefail
cd "$(dirname "$0")"
OUT=${1:-libzeus_ft8.so}
CC=${CC:-gcc}

# -pthread: the callsign hashtable outlives a decode call and is reached
# from the decode worker and the keyer thread, so it is taken under a mutex.
# A no-op on glibc >= 2.34 (pthreads are in libc), needed on older ones.
CFLAGS="-O3 -pthread -I."

# Windows: no -fPIC (meaningless, and mingw warns), and ft8_lib's one use of
# POSIX stpcpy needs the shim in win-compat.h. Detected from the output name or
# the compiler, so a cross build and a native MSYS2 build both work.
case "$OUT$CC" in
  *.dll*|*mingw*|*MINGW*)
    CFLAGS="$CFLAGS -include win-compat.h"
    ;;
  *)
    CFLAGS="$CFLAGS -fPIC"
    ;;
esac

echo "compiling ft8_lib + shim ($CC)..."
rm -rf .obj && mkdir -p .obj
for s in ft8/*.c common/*.c fft/*.c; do
  $CC $CFLAGS -c "$s" -o ".obj/$(echo "$s" | tr '/' '_').o"
done
$CC $CFLAGS -shared -fvisibility=hidden -o "$OUT" zeus_ft8.c .obj/*.o -lm
echo "built $OUT"

# Best-effort export listing — the flags differ per platform and this is a
# convenience, never a gate. tests/run.sh is the gate.
if [ "${OUT##*.}" = "dylib" ]; then
  ${NM:-nm} -gU "$OUT" 2>/dev/null | grep zeus_ft8 || true
else
  ${NM:-nm} -g --defined-only "$OUT" 2>/dev/null | grep zeus_ft8 || true
fi
