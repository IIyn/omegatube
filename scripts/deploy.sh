#!/usr/bin/env bash
# Installs and launches the app on the TV. Usage: TV_IP=192.168.1.x scripts/deploy.sh
# Emulator (already running, see scripts/emulator.sh): SDB_TARGET=emulator-26101 scripts/deploy.sh
set -euo pipefail
cd "$(dirname "$0")/.."

STUDIO="${TIZEN_STUDIO:-$HOME/tizen-studio}"
SDB="$STUDIO/tools/sdb"
TIZEN="$STUDIO/tools/ide/bin/tizen"
if [ -n "${SDB_TARGET:-}" ]; then
  TARGET="$SDB_TARGET"
else
  : "${TV_IP:?set TV_IP to the TV address (or SDB_TARGET for an emulator)}"
  TARGET="$TV_IP:26101"
  "$SDB" connect "$TARGET"
fi
TPK=$(find app/OmegaTube/bin/Release -name '*.tpk' 2>/dev/null | head -n1)
if [ -z "$TPK" ]; then
  echo "no .tpk in app/OmegaTube/bin/Release: run scripts/build-app.sh first" >&2
  exit 1
fi
"$TIZEN" install -n "$(basename "$TPK")" -t "$("$SDB" devices | awk -v t="$TARGET" '$1==t {print $3}')" -- "$(dirname "$TPK")"
"$SDB" -s "$TARGET" shell 0 execute org.omegatube.app
