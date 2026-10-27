#!/usr/bin/env bash
# Builds the .tpk (core + C# shell) and re-signs it with the Samsung certificate profile.
set -euo pipefail
cd "$(dirname "$0")/.."

PROFILE="${TIZEN_PROFILE:-omegatube}"
TIZEN="${TIZEN_STUDIO:-$HOME/tizen-studio}/tools/ide/bin/tizen"

# Single version source: VERSION -> manifest (the core embeds it at compile time).
VERSION=$(head -n1 VERSION)
# (" version=" with a leading space, so api-version is left alone)
sed -i -E "/<manifest /s/ version=\"[^\"]*\"/ version=\"$VERSION\"/" app/OmegaTube/tizen-manifest.xml

# Dev builds: OMEGA_DEV_LOG=host:port mirrors the core log to scripts/serve-test-media.sh.
# Never set it for a release build (res/dev.json is git-ignored).
DEV_JSON=app/OmegaTube/res/dev.json
if [ -n "${OMEGA_DEV_LOG:-}" ]; then
  mkdir -p "$(dirname "$DEV_JSON")"
  printf '{"logHost": "%s", "logPort": %s}\n' "${OMEGA_DEV_LOG%:*}" "${OMEGA_DEV_LOG##*:}" > "$DEV_JSON"
  echo "dev log -> $OMEGA_DEV_LOG"
else
  rm -f "$DEV_JSON"
fi

scripts/build-core.sh
rm -rf app/OmegaTube/bin/Release
dotnet build app/OmegaTube/OmegaTube.csproj -c Release

TPK=$(find app/OmegaTube/bin/Release -name '*.tpk' | head -n1)
"$TIZEN" package -t tpk -s "$PROFILE" -- "$TPK"
echo "-> $TPK"
