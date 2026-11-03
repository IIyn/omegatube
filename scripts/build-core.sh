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

# Tizen 5.5 TVs (oldest supported) have glibc 2.24: one newer symbol and the TV refuses to load the library.
MAX_GLIBC="${OMEGA_MAX_GLIBC:-2.24}"
NEWER=$(readelf --dyn-syms -W "$OUT/libomegacore.so" | grep -oE '@GLIBC_[0-9.]+' | sort -u | sed 's/@GLIBC_//' |
  while read -r v; do [ "$(printf '%s\n%s\n' "$v" "$MAX_GLIBC" | sort -V | tail -n1)" != "$MAX_GLIBC" ] && echo "$v"; done || true)
if [ -n "$NEWER" ]; then
  echo "libomegacore.so needs glibc newer than $MAX_GLIBC (Tizen 5.5 TVs cannot load it):" >&2
  readelf --dyn-syms -W "$OUT/libomegacore.so" | grep -E "@GLIBC_($(echo $NEWER | tr ' ' '|' | sed 's/\./\\./g'))" >&2
  exit 1
fi

mkdir -p app/OmegaTube/lib
cp "$OUT/libomegacore.so" app/OmegaTube/lib/
echo "-> app/OmegaTube/lib/libomegacore.so ($ARCH)"
