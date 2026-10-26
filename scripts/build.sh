#!/usr/bin/env bash
# Build Jellywave and stage a release layout:
#   dist/Jellywave_<version>/{Jellyfin.Plugin.Jellywave.dll, meta.json, thumb.png}
#   dist/Jellywave_<version>.zip   (flat zip of the same three files)
# then refresh manifest.json via scripts/update-manifest.sh.
#
#   scripts/build.sh                 # version from Directory.Build.props
#   VERSION=0.2.0.0 scripts/build.sh # override (release workflow passes the tag)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJ="$ROOT/Jellyfin.Plugin.Jellywave"
DIST="$ROOT/dist"

yaml_get() { awk -F': ' -v k="$1" '$1==k {sub(/^"/,"",$2); sub(/"$/,"",$2); print $2}' "$ROOT/build.yaml"; }

VERSION="${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")}"
NAME="$(yaml_get name)"
GUID="$(yaml_get guid)"
TARGET_ABI="$(yaml_get targetAbi)"
ARTIFACT="$(yaml_get artifacts)"
TIMESTAMP="$(date -u +"%Y-%m-%dT%H:%M:%S.0000000Z")"
STAGE="$DIST/${NAME}_${VERSION}"

echo "== Building $NAME $VERSION =="
dotnet publish "$PROJ/Jellyfin.Plugin.Jellywave.csproj" -c Release -p:Version="$VERSION" \
    -p:AssemblyVersion="$VERSION" -p:FileVersion="$VERSION" -o "$DIST/publish" --nologo

echo "== Staging $STAGE =="
rm -rf "$STAGE" "$STAGE.zip"
mkdir -p "$STAGE"
cp "$DIST/publish/$ARTIFACT" "$STAGE/"
cp "$PROJ/Assets/logo.png" "$STAGE/thumb.png"

jq -n \
  --arg category "$(yaml_get category)" \
  --arg changelog "$(yaml_get changelog)" \
  --arg description "$(yaml_get description)" \
  --arg guid "$GUID" \
  --arg name "$NAME" \
  --arg overview "$(yaml_get overview)" \
  --arg owner "$(yaml_get owner)" \
  --arg targetAbi "$TARGET_ABI" \
  --arg timestamp "$TIMESTAMP" \
  --arg version "$VERSION" \
  --arg artifact "$ARTIFACT" \
  '{category:$category, changelog:$changelog, description:$description, guid:$guid, name:$name,
    overview:$overview, owner:$owner, targetAbi:$targetAbi, timestamp:$timestamp, version:$version,
    status:"Active", autoUpdate:true, assemblies:[$artifact]}' > "$STAGE/meta.json"

(cd "$STAGE" && zip -q -j "$STAGE.zip" "$ARTIFACT" meta.json thumb.png)

echo "== Updating manifest.json =="
"$ROOT/scripts/update-manifest.sh" "$VERSION" "$(yaml_get changelog)" "$TIMESTAMP"

echo "== Done =="
ls -la "$STAGE" "$STAGE.zip"
