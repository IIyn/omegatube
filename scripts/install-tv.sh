#!/usr/bin/env bash
# One-command install for end users (Linux / macOS), published with every release:
#   curl -fsSL https://github.com/IIyn/omegatube/releases/latest/download/install.sh | bash
# Guides through the TV's Developer Mode, sets up Java and the Tizen command-line tools if needed,
# creates a Samsung certificate for the TV, then signs and installs the latest OmegaTube release.
# Usage: install.sh [TV IP address] [OmegaTube .tpk file]   (no arguments: asks interactively)
set -euo pipefail

# Everything runs from main(), so that bash has read the whole script before any command can consume stdin.
main() {
  TV_IP="${1:-}"
  TPK_IN="${2:-}"
  REPO=IIyn/omegatube
  RELEASE="https://github.com/$REPO/releases/latest/download"
  TIZEN_STUDIO_VERSION=6.1
  export TIZEN_STUDIO="${TIZEN_STUDIO:-$HOME/tizen-studio}"
  SDB="$TIZEN_STUDIO/tools/sdb"
  TIZEN="$TIZEN_STUDIO/tools/ide/bin/tizen"
  CERT_DIR="$HOME/SamsungCertificate/omegatube"
  WORK="$HOME/.omegatube-installer"
  SRC_DIR=$(cd "$(dirname "${BASH_SOURCE[0]:-.}")" 2>/dev/null && pwd || echo .)
  mkdir -p "$WORK"

  case "$(uname -s)" in
    Linux) OS=linux ;;
    Darwin) OS=mac ;;
    *) fail "Unsupported system: use Linux, macOS or WSL on Windows." ;;
  esac
  for tool in curl unzip tar; do
    command -v "$tool" >/dev/null || fail "'$tool' is missing: install it with your package manager and run this again."
  done

  printf '\n\033[1mOmegaTube installer\033[0m\n'

  setup_java
  setup_tizen
  [ -n "$TV_IP" ] || developer_mode_guide
  connect_tv
  setup_certificate
  install_app

  printf '\n\033[32mDone! OmegaTube is in the Apps list of your TV.\033[0m\n'
  printf 'Open it, then go to Account to sign in to YouTube with your phone.\n'
}

step() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
fail() { printf '\n\033[31m[!] %s\033[0m\n' "$*" >&2; exit 1; }
ask() { local answer; read -r -p "$1" answer </dev/tty; printf '%s' "$answer"; }

# Java 11+ (Samsung certificate tool): the system one, or a private Temurin JRE.
setup_java() {
  local major
  major=$(java -version 2>&1 | sed -n 's/.*version "\([0-9]*\).*/\1/p' | head -n1 || true)
  if [ "${major:-0}" -lt 11 ]; then
    if [ ! -d "$WORK/jre" ]; then
      step "Downloading Java (one time)"
      local arch
      case "$(uname -m)" in
        x86_64|amd64) arch=x64 ;;
        arm64|aarch64) arch=aarch64 ;;
        *) fail "Unsupported processor: $(uname -m)." ;;
      esac
      mkdir -p "$WORK/jre.tmp"
      curl -fL --progress-bar "https://api.adoptium.net/v3/binary/latest/17/ga/$OS/$arch/jre/hotspot/normal/eclipse" \
        | tar -xz -C "$WORK/jre.tmp" --strip-components=1
      mv "$WORK/jre.tmp" "$WORK/jre"
    fi
    local java_bin
    java_bin=$(find "$WORK/jre" -path '*/bin/java' -type f | head -n1)
    export PATH="$(dirname "$java_bin"):$PATH"
  fi
}

# Tizen command-line tools + Samsung Certificate Extension (one time).
setup_tizen() {
  if [ ! -x "$TIZEN" ]; then
    local installer="$WORK/web-cli_Tizen_Studio_${TIZEN_STUDIO_VERSION}.bin"
    local os_name=ubuntu-64
    [ "$OS" = mac ] && os_name=macos-64
    step "Downloading Samsung's TV tools (one time, a few minutes)"
    curl -fL --progress-bar -o "$installer" \
      "https://download.tizen.org/sdk/Installer/tizen-studio_$TIZEN_STUDIO_VERSION/web-cli_Tizen_Studio_${TIZEN_STUDIO_VERSION}_$os_name.bin"
    step "Installing them into $TIZEN_STUDIO"
    bash "$installer" --accept-license "$TIZEN_STUDIO"
    rm -f "$installer"
  fi
  if [ ! -e "$TIZEN_STUDIO/.info/cert-add-on" ]; then
    step "Installing the Samsung Certificate Extension (one time)"
    "$TIZEN_STUDIO/package-manager/package-manager-cli.bin" install --accept-license cert-add-on
  fi
}

local_ip() {
  if [ "$OS" = mac ]; then
    ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || true
  else
    ip -4 route get 1.1.1.1 2>/dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p' || true
  fi
}

developer_mode_guide() {
  local ip
  ip=$(local_ip)
  step "Turn on Developer Mode on the TV"
  cat <<EOF
  1. On the TV, open the Apps screen.
  2. On the remote, type 1 2 3 4 5 (no number keys? press the 123 button to show a number pad).
  3. Switch Developer Mode to On.
  4. In "Host PC IP", enter this computer's address: ${ip:-"(not found: look it up in your network settings)"}
  5. Restart the TV: hold the power button until the Samsung logo shows again.

  (Already done before? Make sure the Host PC IP is still this computer's address.)
EOF
  ask $'\nPress Enter when the TV has restarted... ' >/dev/null
}

connect_tv() {
  while true; do
    if [ -z "$TV_IP" ]; then
      printf '\nThe TV address is in Settings > General > Network > Network Status > IP Settings.\n'
      TV_IP=$(ask "TV IP address (e.g. 192.168.1.9): " | tr -d ' ')
      [ -n "$TV_IP" ] || continue
    fi
    TARGET="$TV_IP:26101"
    step "Connecting to the TV at $TV_IP"
    "$SDB" connect "$TARGET" >/dev/null 2>&1 || true
    DEVICE=$("$SDB" devices | awk -v t="$TARGET" '$1==t && $2=="device" {print $3}')
    [ -n "$DEVICE" ] && break
    printf '\033[31mThe TV did not answer.\033[0m Check that Developer Mode is On with this computer'"'"'s address\n'
    printf '(%s), that the TV was restarted afterwards and that both are on the same network.\n' "$(local_ip)"
    [ -r /dev/tty ] || exit 1
    TV_IP=""
  done
  DUID=$("$SDB" -s "$TARGET" shell 0 getduid | tr -d '\r\n ')
  [ -n "$DUID" ] || fail "Could not read the TV's unique id (DUID)."
  echo "Found $DEVICE (DUID $DUID)"
}

# Samsung author + distributor certificate for the TV, registered as the "omegatube" signing profile.
# Reused as long as it covers this TV, so updates keep the same signature.
setup_certificate() {
  local duids=""
  if [ -f "$CERT_DIR/distributor.crt" ]; then
    duids=$(keytool -printcert -file "$CERT_DIR/distributor.crt" 2>/dev/null \
      | grep -o 'deviceid=[A-Z0-9]*' | cut -d= -f2 | paste -sd, - || true)
  fi
  if printf ',%s,' "$duids" | grep -q ",$DUID,"; then
    "$TIZEN" security-profiles list 2>/dev/null | grep -qw omegatube && return
  else
    step "Creating your Samsung certificate: a browser page opens, sign in with your Samsung account"
    local java_src="$SRC_DIR/../tools/samsung-cert/SamsungCert.java"
    if [ ! -f "$java_src" ]; then
      java_src="$WORK/SamsungCert.java"
      curl -fsSL -o "$java_src" "$RELEASE/SamsungCert.java"
    fi
    local plugin cp
    plugin=$(ls "$TIZEN_STUDIO"/tools/certificate-manager/plugins/org.tizen.common.cert_*.jar | head -n1)
    rm -rf "$WORK/certjar" && mkdir -p "$WORK/certjar" && unzip -oq "$plugin" -d "$WORK/certjar"
    cp="$WORK/certjar:$(ls "$WORK"/certjar/lib/*.jar | tr '\n' ':')$(ls "$TIZEN_STUDIO"/tools/ide/lib-ncli/slf4j-api-*.jar | head -n1)"
    java -cp "$cp" "$java_src" "$CERT_DIR" OmegaTube "${duids:+$duids,}$DUID"
  fi
  local pw
  pw=$(sed -n 's/^author=//p' "$CERT_DIR/passwords.txt")
  "$TIZEN" security-profiles add -f -n omegatube \
    -a "$CERT_DIR/author.p12" -p "$pw" -d "$CERT_DIR/distributor.p12" -dp "$pw" >/dev/null
}

install_app() {
  local tpk="$WORK/OmegaTube.tpk"
  if [ -n "$TPK_IN" ]; then
    cp "$TPK_IN" "$tpk"
  else
    step "Downloading the latest OmegaTube"
    curl -fL --progress-bar -o "$tpk" "$RELEASE/OmegaTube.tpk" \
      || fail "Could not download OmegaTube from github.com/$REPO/releases."
  fi
  step "Signing the app for your TV"
  "$TIZEN" package -t tpk -s omegatube -- "$tpk" >/dev/null
  step "Installing on the TV"
  "$TIZEN" install -n "$(basename "$tpk")" -t "$DEVICE" -- "$WORK"
  "$SDB" -s "$TARGET" shell 0 execute org.omegatube.app >/dev/null 2>&1 || true
}

main "$@"
