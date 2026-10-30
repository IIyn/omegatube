#!/usr/bin/env bash
# Starts a Samsung TV emulator VM and waits until sdb sees it. Usage: scripts/emulator.sh tv7|tv10
# VMs: tv7 (TV 7.0, x86: OMEGA_ARCH=x86) and tv10 (TV 10.0, x86_64: OMEGA_ARCH=x86_64).
set -euo pipefail

VM="${1:?usage: emulator.sh <vm name> (em-cli list-vm)}"
STUDIO="${TIZEN_STUDIO:-$HOME/tizen-studio}"
SDB="$STUDIO/tools/sdb"

# The emulator's bundled Qt only has the xcb plugin (runs under XWayland).
QT_QPA_PLATFORM=xcb "$STUDIO/tools/emulator/bin/em-cli" launch -n "$VM"
for _ in $(seq 1 100); do
  TARGET=$("$SDB" devices | awk -v vm="$VM" '$3==vm && $2=="device" {print $1}')
  [ -n "$TARGET" ] && break
  sleep 3
done
: "${TARGET:?$VM did not come up}"
echo "$VM ready: SDB_TARGET=$TARGET"
