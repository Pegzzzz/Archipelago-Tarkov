#!/bin/bash
# Put the release files in dist/ from already-built binaries (same layout as the CI workflow):
#   ArchipelagoTarkov-<version>-SPT-4.0.13.zip   extract into the game folder
#   tarkov.apworld                               built with Archipelago's "Build APWorlds"
#   Escape from Tarkov.yaml                      options template
#   ArchipelagoTarkov-<version>-source.zip
#
# Usage: tools/package.sh <archipelago checkout>
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
AP=${1:?usage: tools/package.sh <archipelago checkout>}
PY=${AP_PYTHON:-python3}
VERSION=$(python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['world_version'])" "$ROOT/apworld/tarkov/archipelago.json")
DIST="$ROOT/dist"
SERVER_DLL="$ROOT/server-mod/ArchipelagoTarkov/bin/Release/ArchipelagoTarkov.dll"
CLIENT_DLL="$ROOT/client-plugin/ArchipelagoTarkov.Client/bin/Release/ArchipelagoTarkov.Client.dll"

for f in "$SERVER_DLL" "$CLIENT_DLL"; do
  [ -f "$f" ] || { echo "missing $f - build the mod and the plugin first"; exit 1; }
done

rm -rf "$DIST"
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$DIST" "$STAGE/SPT/user/mods/ArchipelagoTarkov" "$STAGE/BepInEx/plugins/ArchipelagoTarkov"
cp "$SERVER_DLL" "$STAGE/SPT/user/mods/ArchipelagoTarkov/"
cp "$CLIENT_DLL" "$STAGE/BepInEx/plugins/ArchipelagoTarkov/"
(cd "$STAGE" && zip -qr "$DIST/ArchipelagoTarkov-$VERSION-SPT-4.0.13.zip" SPT BepInEx)

# APWorld and template, with Archipelago's own tools
rm -rf "$AP/worlds/tarkov" "$AP/build/apworlds/tarkov.apworld" "$AP/templates-tarkov"
cp -r "$ROOT/apworld/tarkov" "$AP/worlds/tarkov"
find "$AP/worlds/tarkov" -name __pycache__ -prune -exec rm -rf {} +
build_apworld() {
  (cd "$AP" && SKIP_REQUIREMENTS_UPDATE=1 PYTHONDONTWRITEBYTECODE=1 "$PY" Launcher.py "Build APWorlds" -- "Escape from Tarkov" "$@" < /dev/null > /dev/null 2>&1)
}
# --skip_open_folder only exists in newer Archipelago versions
build_apworld --skip_open_folder || build_apworld || true
[ -f "$AP/build/apworlds/tarkov.apworld" ] || { echo "Build APWorlds failed"; exit 1; }
cp "$AP/build/apworlds/tarkov.apworld" "$DIST/"
(cd "$AP" && SKIP_REQUIREMENTS_UPDATE=1 "$PY" -c "import Options; Options.generate_yaml_templates('templates-tarkov', False)" > /dev/null 2>&1)
cp "$AP/templates-tarkov/Escape from Tarkov.yaml" "$DIST/"
rm -rf "$AP/worlds/tarkov" "$AP/templates-tarkov"

# Source
(cd "$ROOT" && zip -qr "$DIST/ArchipelagoTarkov-$VERSION-source.zip" . \
  -x '.git/*' 'dist/*' '*/bin/*' '*/obj/*' '*__pycache__*' 'tests/plugin/run_*')

ls -la "$DIST"
