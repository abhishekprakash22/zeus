#!/usr/bin/env bash
# Build and run the zeus_ft8 C tests.
#
#   ./tests/run.sh          (from native/ft8, or from anywhere — it cd's itself)
#
# Same toolchain assumptions as build.sh: a C compiler and libm, nothing else.
# Objects are built into .obj-test/ so a test run never disturbs build.sh's.
set -euo pipefail
cd "$(dirname "$0")/.."

CC=${CC:-cc}
CFLAGS="-O1 -g -DHAVE_STPCPY -pthread -I."
OBJ=.obj-test
BIN=${TMPDIR:-/tmp}/zeus_ft8_tests

echo "compiling ft8_lib..."
rm -rf "$OBJ" && mkdir -p "$OBJ"
for s in ft8/*.c common/*.c fft/*.c; do
  $CC $CFLAGS -c "$s" -o "$OBJ/$(echo "$s" | tr '/' '_').o"
done

status=0
for t in tests/*_test.c; do
  name=$(basename "$t" .c)
  echo "building $name..."
  $CC $CFLAGS -o "$BIN-$name" "$t" "$OBJ"/*.o -lm
  "$BIN-$name" || status=1
done

rm -rf "$OBJ"
exit $status
