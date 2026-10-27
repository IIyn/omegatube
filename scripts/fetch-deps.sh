#!/usr/bin/env bash
# Downloads pinned third-party sources into build/deps (not committed).
set -euo pipefail
cd "$(dirname "$0")/.."

MBEDTLS_VERSION=3.6.7
JSON_VERSION=3.12.0
QRCODEGEN_VERSION=1.8.0
DEPS=build/deps
mkdir -p "$DEPS"

if [ ! -d "$DEPS/mbedtls-$MBEDTLS_VERSION" ]; then
  curl -sfL "https://github.com/Mbed-TLS/mbedtls/releases/download/mbedtls-$MBEDTLS_VERSION/mbedtls-$MBEDTLS_VERSION.tar.bz2" \
    | tar -xj -C "$DEPS"
fi
if [ ! -f "$DEPS/nlohmann/json.hpp" ]; then
  mkdir -p "$DEPS/nlohmann"
  curl -sfL -o "$DEPS/nlohmann/json.hpp" "https://github.com/nlohmann/json/releases/download/v$JSON_VERSION/json.hpp"
fi
# Nayuki's QR Code generator (MIT), for the sign-in QR code.
if [ ! -f "$DEPS/qrcodegen/qrcodegen.cpp" ]; then
  mkdir -p "$DEPS/qrcodegen"
  for f in qrcodegen.cpp qrcodegen.hpp; do
    curl -sfL -o "$DEPS/qrcodegen/$f" "https://raw.githubusercontent.com/nayuki/QR-Code-generator/v$QRCODEGEN_VERSION/cpp/$f"
  done
fi
# Mozilla CA bundle (as packaged by curl), embedded into the core for TLS verification.
if [ ! -f "$DEPS/cacert.pem" ]; then
  curl -sfL -o "$DEPS/cacert.pem" https://curl.se/ca/cacert.pem
fi
ls "$DEPS"
