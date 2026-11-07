#!/usr/bin/env bash
# Runs the custom ELF loader (the Tizen 5.5 fix, GitHub issue #15) against the real cross-built core under
# qemu-arm - no TV required. Needs a soft-float ARM cross compiler and qemu-user:
#   sudo apt-get install -y gcc-arm-linux-gnueabi qemu-user-static
# and the core already built (scripts/build-core.sh -> app/OmegaTube/lib/libomegacore.so).
set -euo pipefail
cd "$(dirname "$0")"
ROOT=$(cd ../.. && pwd)

CC_ARM="${CC_ARM:-arm-linux-gnueabi-gcc}"
QEMU="${QEMU:-qemu-arm-static}"
SYSROOT="${ARM_SYSROOT:-/usr/arm-linux-gnueabi}"
CORE="${1:-$ROOT/app/OmegaTube/lib/libomegacore.so}"
VERSION=$(head -n1 "$ROOT/VERSION")
OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT

[ -f "$CORE" ] || { echo "core not found: $CORE (run scripts/build-core.sh first)" >&2; exit 1; }

"$CC_ARM" -O2 -marm loader.c -ldl -o "$OUT/loader-arm"
# -L points qemu at the ARM sysroot so the core's deps (libc/libm/libpthread/libdl) resolve.
"$QEMU" -L "$SYSROOT" "$OUT/loader-arm" "$CORE" "$VERSION"
