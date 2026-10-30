#!/usr/bin/env bash
# Cross-compiles the C++ core and drops libomegacore.so into the app.
# OMEGA_ARCH: arm (TV, default), x86 (TV 7-9 emulators) or x86_64 (TV 10 emulator).
set -euo pipefail
cd "$(dirname "$0")/.."

ARCH="${OMEGA_ARCH:-arm}"
case "$ARCH" in
  arm) TOOLCHAIN=tizen-armel; STRIP=arm-linux-gnueabi-strip ;;
  x86) TOOLCHAIN=tizen-x86; STRIP=i686-linux-gnu-strip ;;
  x86_64) TOOLCHAIN=tizen-x86_64; STRIP=strip ;;
  *) echo "unknown OMEGA_ARCH=$ARCH (arm, x86, x86_64)" >&2; exit 1 ;;
esac
OUT="build/core-$ARCH"

docker build -q -t omegatube-toolchain toolchain/ >/dev/null
docker run --rm -u "$(id -u):$(id -g)" -v "$PWD:/src" -w /src omegatube-toolchain sh -c "
  cmake -S core -B $OUT -DCMAKE_TOOLCHAIN_FILE=/src/toolchain/$TOOLCHAIN.cmake -DCMAKE_BUILD_TYPE=Release >/dev/null &&
  cmake --build $OUT -- -j\"\$(nproc)\" &&
  $STRIP $OUT/libomegacore.so &&
  file $OUT/libomegacore.so"

mkdir -p app/OmegaTube/lib
cp "$OUT/libomegacore.so" app/OmegaTube/lib/
echo "-> app/OmegaTube/lib/libomegacore.so ($ARCH)"
