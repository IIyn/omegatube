#!/usr/bin/env bash
# Cross-compiles the C++ core for the TV and drops libomegacore.so into the app.
set -euo pipefail
cd "$(dirname "$0")/.."

docker build -q -t omegatube-toolchain toolchain/ >/dev/null
docker run --rm -u "$(id -u):$(id -g)" -v "$PWD:/src" -w /src omegatube-toolchain sh -c '
  cmake -S core -B build/core-arm -DCMAKE_TOOLCHAIN_FILE=/src/toolchain/tizen-armel.cmake -DCMAKE_BUILD_TYPE=Release >/dev/null &&
  cmake --build build/core-arm -- -j"$(nproc)" &&
  arm-linux-gnueabi-strip build/core-arm/libomegacore.so &&
  file build/core-arm/libomegacore.so'

cp build/core-arm/libomegacore.so app/OmegaTube/lib/
echo "-> app/OmegaTube/lib/libomegacore.so"
