#!/usr/bin/env bash
# Assert the plugin GUID, version and ABI agree everywhere they are declared:
#   Plugin.cs, configPage.html, build.yaml, manifest.json, Directory.Build.props,
#   and, when dist/ holds the current version, its meta.json and zip checksum.
set -euo pipefail
. "$(dirname "$0")/lib.sh"
fail=0
check() { if [ "$2" = "$3" ]; then echo "ok   $1: $2"; else echo "FAIL $1: '$2' != '$3'"; fail=1; fi; }

NAME="$(yaml_get name)"
G_YAML="$(yaml_get guid)"
G_CS="$(sed -n 's/.*Guid.Parse("\([^"]*\)").*/\1/p' "$ROOT/Jellyfin.Plugin.Jellywave/Plugin.cs")"
G_HTML="$(grep -oE "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}" "$ROOT/Jellyfin.Plugin.Jellywave/Configuration/configPage.html" | head -1)"
G_MAN="$(jq -r '.[0].guid' "$ROOT/manifest.json")"
V_PROPS="$(props_version)"
V_MAN="$(jq -r '.[0].versions[0].version' "$ROOT/manifest.json")"
ABI_YAML="$(yaml_get targetAbi)"
ABI_MAN="$(jq -r '.[0].versions[0].targetAbi' "$ROOT/manifest.json")"

check "guid Plugin.cs == build.yaml"      "$G_CS"    "$G_YAML"
check "guid configPage == build.yaml"     "$G_HTML"  "$G_YAML"
check "guid manifest == build.yaml"       "$G_MAN"   "$G_YAML"
check "version manifest[0] == props"      "$V_MAN"   "$V_PROPS"
check "targetAbi manifest[0] == yaml"     "$ABI_MAN" "$ABI_YAML"
jq -e '.[0].versions[0].checksum | test("^[0-9A-F]{32}$")' "$ROOT/manifest.json" >/dev/null \
  && echo "ok   checksum is uppercase MD5" || { echo "FAIL checksum format"; fail=1; }

STAGE="$ROOT/dist/${NAME}_${V_PROPS}"
if [ -f "$STAGE/meta.json" ] && [ -f "$STAGE.zip" ]; then
    check "dist meta.json guid"       "$(jq -r .guid "$STAGE/meta.json")"      "$G_YAML"
    check "dist meta.json version"    "$(jq -r .version "$STAGE/meta.json")"   "$V_PROPS"
    check "dist meta.json targetAbi"  "$(jq -r .targetAbi "$STAGE/meta.json")" "$ABI_YAML"
    check "dist zip md5 == manifest"  "$(md5sum "$STAGE.zip" | awk '{print toupper($1)}')" \
          "$(jq -r --arg v "$V_PROPS" '.[0].versions[] | select(.version == $v) | .checksum' "$ROOT/manifest.json")"
else
    echo "skip dist checks (no dist/${NAME}_${V_PROPS}); run scripts/build.sh to include them"
fi
exit $fail
