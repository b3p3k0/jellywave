#!/usr/bin/env bash
# Assert the plugin GUID and version agree everywhere they are declared:
#   Plugin.cs, configPage.html, build.yaml, manifest.json, Directory.Build.props
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
fail=0
yaml_get() { awk -F': ' -v k="$1" '$1==k {sub(/^"/,"",$2); sub(/"$/,"",$2); print $2}' "$ROOT/build.yaml"; }

G_YAML="$(yaml_get guid)"
G_CS="$(sed -n 's/.*Guid.Parse("\([^"]*\)").*/\1/p' "$ROOT/Jellyfin.Plugin.Jellywave/Plugin.cs")"
G_HTML="$(sed -n "s/.*pluginUniqueId: '\([^']*\)'.*/\1/p" "$ROOT/Jellyfin.Plugin.Jellywave/Configuration/configPage.html")"
G_MAN="$(jq -r '.[0].guid' "$ROOT/manifest.json")"
V_PROPS="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"
V_MAN="$(jq -r '.[0].versions[0].version' "$ROOT/manifest.json")"
ABI_YAML="$(yaml_get targetAbi)"; ABI_MAN="$(jq -r '.[0].versions[0].targetAbi' "$ROOT/manifest.json")"

check() { if [ "$2" = "$3" ]; then echo "ok   $1: $2"; else echo "FAIL $1: '$2' != '$3'"; fail=1; fi; }
check "guid Plugin.cs == build.yaml"      "$G_CS"   "$G_YAML"
check "guid configPage == build.yaml"     "$G_HTML" "$G_YAML"
check "guid manifest == build.yaml"       "$G_MAN"  "$G_YAML"
check "version manifest[0] == props"      "$V_MAN"  "$V_PROPS"
check "targetAbi manifest[0] == yaml"     "$ABI_MAN" "$ABI_YAML"
jq -e '.[0].versions[0].checksum | test("^[0-9A-F]{32}$")' "$ROOT/manifest.json" >/dev/null \
  && echo "ok   checksum is uppercase MD5" || { echo "FAIL checksum format"; fail=1; }
exit $fail
