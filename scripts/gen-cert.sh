#!/usr/bin/env bash
# Generates a Samsung author + distributor certificate for the TV without the Tizen Studio GUI,
# then registers it as the "omegatube" signing profile.
# Usage: scripts/gen-cert.sh <TV DUID> [author name]
set -euo pipefail
cd "$(dirname "$0")/.."

DUID="${1:?usage: gen-cert.sh <DUID> [author name]}"
NAME="${2:-OmegaTube}"
STUDIO="${TIZEN_STUDIO:-$HOME/tizen-studio}"
OUT="$HOME/SamsungCertificate/omegatube"
PLUGIN=$(ls "$STUDIO"/tools/certificate-manager/plugins/org.tizen.common.cert_*.jar | head -n1)

rm -rf build/certjar && mkdir -p build/certjar && unzip -oq "$PLUGIN" -d build/certjar
CP="build/certjar:$(ls build/certjar/lib/*.jar | tr '\n' ':')$STUDIO/tools/ide/lib-ncli/slf4j-api-1.7.36.jar"

java -cp "$CP" tools/samsung-cert/SamsungCert.java "$OUT" "$NAME" "$DUID"

PW=$(sed -n 's/^author=//p' "$OUT/passwords.txt")
"$STUDIO/tools/ide/bin/tizen" security-profiles add -f -n omegatube \
  -a "$OUT/author.p12" -p "$PW" -d "$OUT/distributor.p12" -dp "$PW"
