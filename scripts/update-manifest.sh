#!/usr/bin/env bash
# Insert or replace one version entry in manifest.json, keeping versions sorted newest first.
#   scripts/update-manifest.sh <version> [changelog] [timestamp]
# Reads dist/Jellywave_<version>.zip for the checksum (uppercase MD5, as Jellyfin expects).
# REPO_SLUG (default b3p3k0/jellywave) builds the GitHub release sourceUrl.
set -euo pipefail
. "$(dirname "$0")/lib.sh"

VERSION="${1:?version required, e.g. 0.1.0.0}"
CHANGELOG="${2:-}"
TIMESTAMP="${3:-$(date -u +"%Y-%m-%dT%H:%M:%S.0000000Z")}"
REPO_SLUG="${REPO_SLUG:-b3p3k0/jellywave}"

NAME="$(yaml_get name)"; require NAME "$NAME"
ZIP="$ROOT/dist/${NAME}_${VERSION}.zip"
MANIFEST="$ROOT/manifest.json"
[ -f "$ZIP" ] || { echo "ERROR: $ZIP not found; run scripts/build.sh first" >&2; exit 1; }
jq -e 'type == "array" and length >= 1 and (.[0].versions | type == "array")' "$MANIFEST" >/dev/null 2>&1 \
  || { echo "ERROR: $MANIFEST is missing or not a plugin manifest (array with .[0].versions)" >&2; exit 1; }

CHECKSUM="$(md5sum "$ZIP" | awk '{print toupper($1)}')"
SOURCE_URL="https://github.com/$REPO_SLUG/releases/download/v$VERSION/${NAME}_${VERSION}.zip"

jq --arg version "$VERSION" --arg changelog "$CHANGELOG" --arg targetAbi "$(yaml_get targetAbi)" \
   --arg sourceUrl "$SOURCE_URL" --arg checksum "$CHECKSUM" --arg timestamp "$TIMESTAMP" '
  .[0].versions = (
    [ .[0].versions[] | select(.version != $version) ]
    + [{version:$version, changelog:$changelog, targetAbi:$targetAbi, sourceUrl:$sourceUrl, checksum:$checksum, timestamp:$timestamp}]
    | sort_by(.version | split(".") | map(tonumber)) | reverse
  )' "$MANIFEST" > "$MANIFEST.tmp" && mv "$MANIFEST.tmp" "$MANIFEST"

echo "manifest.json: $NAME $VERSION checksum $CHECKSUM"
