#!/usr/bin/env bash
# Generates a Samsung author + distributor certificate for the TV without the Tizen Studio GUI,
# then registers it as the "omegatube" signing profile.
# Usage: scripts/gen-cert.sh <TV DUID> [author name]
#        scripts/gen-cert.sh --distributor <DUID>[,<DUID>...]  (new distributor for more devices, e.g. the
#        emulators: keeps the author certificate, so the app on the TV can still be updated)
set -euo pipefail
cd "$(dirname "$0")/.."

if [ "${1:-}" = "--distributor" ]; then
  DIST_ONLY=1
  shift
fi
DUID="${1:?usage: gen-cert.sh [--distributor] <DUID>[,<DUID>...] [author name]}"
NAME="${2:-OmegaTube}"
STUDIO="${TIZEN_STUDIO:-$HOME/tizen-studio}"
OUT="$HOME/SamsungCertificate/omegatube"
PLUGIN=$(ls "$STUDIO"/tools/certificate-manager/plugins/org.tizen.common.cert_*.jar | head -n1)

rm -rf build/certjar && mkdir -p build/certjar && unzip -oq "$PLUGIN" -d build/certjar
CP="build/certjar:$(ls build/certjar/lib/*.jar | tr '\n' ':')$STUDIO/tools/ide/lib-ncli/slf4j-api-1.7.36.jar"

if [ -n "${DIST_ONLY:-}" ]; then
  backup="$OUT/backup-$(date +%Y%m%d-%H%M%S)"
  mkdir -p "$backup" && cp "$OUT"/distributor.* "$backup"/
  java -cp "$CP" tools/samsung-cert/SamsungCert.java --distributor "$OUT" "$DUID"
else
  java -cp "$CP" tools/samsung-cert/SamsungCert.java "$OUT" "$NAME" "$DUID"
fi

PW=$(sed -n 's/^author=//p' "$OUT/passwords.txt")
"$STUDIO/tools/ide/bin/tizen" security-profiles add -f -n omegatube \
  -a "$OUT/author.p12" -p "$PW" -d "$OUT/distributor.p12" -dp "$PW"
