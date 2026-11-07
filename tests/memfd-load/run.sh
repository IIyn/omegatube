#!/usr/bin/env bash
# Host test for the Tizen 5.5 core-load fix (GitHub issue #15). Needs only a C/C++ compiler; no TV, no Docker.
# Builds a small library from the core's real omega_version/omega_selftest, then loads it by file path and
# from an in-memory fd (the memfd route the app uses on Tizen 5.5) and checks both run the C++ runtime.
set -euo pipefail
cd "$(dirname "$0")"
ROOT=$(cd ../.. && pwd)
VERSION=$(head -n1 "$ROOT/VERSION")
CXX="${CXX:-g++}"
CC="${CC:-gcc}"
OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT

# Build the stand-in core the same way the real one links its C++ runtime (static) and glibc wrap.
"$CXX" -std=c++17 -O2 -fPIC -shared -fvisibility=hidden \
  -DOMEGA_VERSION="\"$VERSION\"" \
  selftest_core.cpp "$ROOT/core/src/util/glibc_compat.cpp" \
  -Wl,--wrap=explicit_bzero -static-libstdc++ -static-libgcc -pthread \
  -o "$OUT/libomegacore.so"

"$CC" -O2 load_test.c -ldl -o "$OUT/load_test"
"$OUT/load_test" "$OUT/libomegacore.so" "$VERSION"
