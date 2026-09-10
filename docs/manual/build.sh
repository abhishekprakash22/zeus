#!/usr/bin/env bash
# Build the ANAN Core Operator's Manual PDF. Thin wrapper: the builder itself
# is build.mjs (cross-platform node), kept so CI and existing muscle memory
# keep working. Usage: ./build.sh [output.pdf]
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
exec node "$HERE/build.mjs" "$@"
