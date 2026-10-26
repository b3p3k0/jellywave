#!/usr/bin/env bash
# Insert or replace one version entry in manifest.json (newest first).
#   scripts/update-manifest.sh <version> [changelog] [timestamp]
# Reads dist/Jellywave_<version>.zip for the checksum (uppercase MD5, as Jellyfin expects).
# REPO_SLUG (default b3p3k0/jellywave) builds the GitHub release sourceUrl.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:?version required, e.g. 0.1.0.0}"
CHANGELOG="${2:-}"
TIMESTAMP="${3:-$(date -u +"%Y-%m-%dT%H:%M:%S.0000000Z")}"
REPO_SLUG="${REPO_SLUG:-b3p3k0/jellywave}"

yaml_get() { awk -F': ' -v k="$1" '$1==k {sub(/^"/,"",$2); sub(/"$/,"",$2); print $2}' "$ROOT/build.yaml"; }
NAME="$(yaml_get name)"
ZIP="$ROOT/dist/${NAME}_${VERSION}.zip"
[ -f "$ZIP" ] || { echo "ERROR: $ZIP not found; run scripts/build.sh first" >&2; exit 1; }

CHECKSUM="$(md5sum "$ZIP" | awk '{print toupper($1)}')"
SOURCE_URL="https://github.com/$REPO_SLUG/releases/download/v$VERSION/${NAME}_${VERSION}.zip"
MANIFEST="$ROOT/manifest.json"

jq --arg version "$VERSION" --arg changelog "$CHANGELOG" --arg targetAbi "$(yaml_get targetAbi)" \
   --arg sourceUrl "$SOURCE_URL" --arg checksum "$CHECKSUM" --arg timestamp "$TIMESTAMP" '
  .[0].versions = (
    [{version:$version, changelog:$changelog, targetAbi:$targetAbi, sourceUrl:$sourceUrl, checksum:$checksum, timestamp:$timestamp}]
    + [ .[0].versions[] | select(.version != $version) ]
  )' "$MANIFEST" > "$MANIFEST.tmp" && mv "$MANIFEST.tmp" "$MANIFEST"

echo "manifest.json: $NAME $VERSION checksum $CHECKSUM"
