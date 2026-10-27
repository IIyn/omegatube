#!/usr/bin/env bash
# Downloads pinned third-party sources into build/deps (not committed).
set -euo pipefail
cd "$(dirname "$0")/.."

MBEDTLS_VERSION=3.6.7
JSON_VERSION=3.12.0
QRCODEGEN_VERSION=1.8.0
QUICKJS_VERSION=0.17.0
EJS_VERSION=0.8.0
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
# QuickJS-ng (MIT): runs YouTube's player JS to decipher stream URLs of the fallback client.
if [ ! -d "$DEPS/quickjs-$QUICKJS_VERSION" ]; then
  curl -sfL "https://github.com/quickjs-ng/quickjs/archive/refs/tags/v$QUICKJS_VERSION.tar.gz" | tar -xz -C "$DEPS"
fi
# yt-dlp EJS solver (Unlicense; bundles meriyah ISC + astring MIT): finds the n / sig functions in the player JS.
if [ ! -f "$DEPS/ejs-$EJS_VERSION/core.min.js" ]; then
  mkdir -p "$DEPS/ejs-$EJS_VERSION"
  WHEEL=$(mktemp)
  curl -sfL -o "$WHEEL" "https://files.pythonhosted.org/packages/py3/y/yt-dlp-ejs/yt_dlp_ejs-$EJS_VERSION-py3-none-any.whl"
  for f in core.min.js lib.min.js; do
    python3 -I -c "import sys, zipfile; sys.stdout.buffer.write(zipfile.ZipFile(sys.argv[1]).read('yt_dlp_ejs/yt/solver/' + sys.argv[2]))" \
      "$WHEEL" "$f" > "$DEPS/ejs-$EJS_VERSION/$f"
  done
  rm -f "$WHEEL"
fi
# Mozilla CA bundle (as packaged by curl), embedded into the core for TLS verification.
if [ ! -f "$DEPS/cacert.pem" ]; then
  curl -sfL -o "$DEPS/cacert.pem" https://curl.se/ca/cacert.pem
fi
ls "$DEPS"
