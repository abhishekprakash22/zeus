#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
#
# Refuse to ship a native binary older than the source it was built from.
#
# The native libraries under Zeus.Dsp/runtimes/ are committed by hand: CI
# builds them (build-native-libs.yml) but uploads artifacts rather than
# committing, and the workflows that cut releases build no natives at all. So
# a fix in native/<lib>/ reaches an operator only if somebody remembers to
# rebuild and commit. Three times in two days, nobody did:
#
#   v1.85  shipped a libzeus_ft8 predating the measured-SNR fix (#14), so
#          every Linux operator read ft8_lib's demo score — ~20-30 dB high
#          and never negative — and a field diagnostic added to the estimator
#          printed nothing, because neither was in the binary.
#   v1.86  fixed that and still shipped the pre-#22 libzeus_wspr on both
#          Linux RIDs, whose decoder overflows the thread stack and takes the
#          server process down with it.
#
# Both were invisible: the file was present, the right size, and loaded fine.
# Presence is not freshness, which is the one thing a human eye cannot check
# and git can. For each library this compares the commit date of its source
# directory against the commit date of every binary shipped for it, and fails
# if any binary is older.
#
# Deliberately not a checksum comparison: rebuilding the same source twice
# does not produce the same bytes, so a hash check would cry wolf forever and
# be switched off within a week. Commit order is exact and cheap.
#
# Usage:  tools/check-native-freshness.sh [git-ref]      (default HEAD)
# Exit:   0 everything current, 1 something stale or missing.

set -uo pipefail
cd "$(git rev-parse --show-toplevel)"

REF="${1:-HEAD}"
rc=0

# Committed at "$(date -r ...)"; printed so the failure names both dates and a
# reader can go straight to the commit that moved ahead.
when() { date -r "$1" '+%Y-%m-%d %H:%M' 2>/dev/null || date -d "@$1" '+%Y-%m-%d %H:%M'; }

# One library: its source directory, then every binary that ships from it.
# A platform that does not ship the library is simply not listed — WSPR has no
# Windows build, and that is a fact about the product, not a fault.
check_lib() {
    local src="$1"; shift
    local src_ct
    src_ct="$(git log -1 --format=%ct "$REF" -- "$src")"
    if [ -z "$src_ct" ]; then
        printf '  ?? %-56s no commits touch %s\n' "" "$src"
        rc=1
        return
    fi

    local bin bin_ct
    for bin in "$@"; do
        if ! git cat-file -e "$REF:$bin" 2>/dev/null; then
            printf '  MISSING  %s\n' "$bin"
            rc=1
            continue
        fi
        bin_ct="$(git log -1 --format=%ct "$REF" -- "$bin")"
        if [ -z "$bin_ct" ] || [ "$src_ct" -gt "$bin_ct" ]; then
            printf '  STALE    %s\n' "$bin"
            printf '             %s last changed %s\n' "$src" "$(when "$src_ct")"
            printf '             binary   built from %s\n' "$(when "${bin_ct:-0}")"
            rc=1
        else
            printf '  ok       %s\n' "$bin"
        fi
    done
}

echo "Native binaries vs their source, at ${REF}:"
echo

# (native/ft8 and native/wspr are gone: FT8/FT4 and WSPR are managed code in
# Digital/Ft8 and Digital/Wspr. No hand-committed library is built from
# in-tree source any more; add a check_lib line here if one ever is again.)

echo
if [ "$rc" -ne 0 ]; then
    cat <<'MSG'
A shipped binary is older than the source it is built from, so a release cut
from this tree would not contain the fix its source claims.

To clear it: run the "Build Native Libraries" workflow on this branch, download
the per-platform artifacts from the run summary, and commit the libraries named
above. Then run this again.
MSG
else
    echo "Every shipped native library is at least as new as its source."
fi
exit "$rc"
