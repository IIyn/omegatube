#!/usr/bin/env bash
# Installs and launches the app on the TV. Usage: TV_IP=192.168.1.x scripts/deploy.sh
set -euo pipefail
cd "$(dirname "$0")/.."

: "${TV_IP:?set TV_IP to the TV address}"
STUDIO="${TIZEN_STUDIO:-$HOME/tizen-studio}"
SDB="$STUDIO/tools/sdb"
TIZEN="$STUDIO/tools/ide/bin/tizen"
TARGET="$TV_IP:26101"

"$SDB" connect "$TARGET"
TPK=$(find app/OmegaTube/bin/Release -name '*.tpk' | head -n1)
"$TIZEN" install -n "$(basename "$TPK")" -t "$("$SDB" devices | awk -v t="$TARGET" '$1==t {print $3}')" -- "$(dirname "$TPK")"
"$SDB" -s "$TARGET" shell 0 execute org.omegatube.app
