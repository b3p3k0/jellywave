#!/usr/bin/env bash
# Publish one release and its manifest entry; run by the release workflow's publish job from a checkout of main.
#   scripts/publish-release.sh <tag>        e.g. v0.5.0.0; expects dist/<name>_<version>.zip and dist/<name>_<version>/meta.json
# Safe to re-run after any interruption:
#   - The release is created as a draft and the zip attached; the manifest entry is written and checked; only then is
#     the release published (the order GitHub's immutable releases need), and the entry pushed.
#   - A zip already attached to the release is never replaced: it is downloaded, checked, and used for the manifest
#     instead of this run's build, so a re-run cannot change bytes a user may already have. An attachment whose
#     upload never completed is deleted and uploaded again while the release is a draft; on a published release it
#     stops the run.
#   - An existing manifest entry for the version with another checksum stops the run before anything is published.
# GH names the gh command (tests pass a stand-in); REMOTE and BRANCH name where the manifest commit goes;
# EXPECTED_SHA, when set, is the commit the workflow verified and built: the tag must still point at it.
set -euo pipefail
. "$(dirname "$0")/lib.sh"

TAG="${1:?tag required, e.g. v0.5.0.0}"
GH="${GH:-gh}"
REMOTE="${REMOTE:-origin}"
BRANCH="${BRANCH:-main}"
PUSH_ATTEMPTS=3

die() { echo "ERROR: $*" >&2; exit 1; }

# update-manifest.sh builds the download URL as .../v<version>/..., so only that tag shape can be published.
[[ "$TAG" =~ ^v[0-9]+(\.[0-9]+){3}$ ]] || die "tag '$TAG' is not v<major>.<minor>.<patch>.<build>"
V="${TAG#v}"

NAME="$(yaml_get name)"; require NAME "$NAME"
GUID="$(yaml_get guid)"; require GUID "$GUID"
ARTIFACT="$(yaml_get artifacts)"; require ARTIFACT "$ARTIFACT"
ZIP_NAME="${NAME}_${V}.zip"
BUILT_ZIP="$ROOT/dist/$ZIP_NAME"
BUILT_META="$ROOT/dist/${NAME}_${V}/meta.json"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# check_zip ZIP ABI: the zip holds exactly the plugin files, and its meta.json names this plugin, version and ABI.
check_zip() {
    local zip="$1" abi="$2" files
    files="$(unzip -Z1 "$zip" | sort | tr '\n' ' ')"
    [ "$files" = "$(printf '%s\n' "$ARTIFACT" meta.json thumb.png | sort | tr '\n' ' ')" ] \
        || die "$zip holds '$files', not $ARTIFACT, meta.json and thumb.png"
    unzip -p "$zip" meta.json > "$WORK/meta.json"
    [ "$(jq -r .guid "$WORK/meta.json")" = "$GUID" ] || die "$zip is not plugin $GUID"
    [ "$(jq -r .version "$WORK/meta.json")" = "$V" ] || die "$zip is not version $V"
    [ "$(jq -r .targetAbi "$WORK/meta.json")" = "$abi" ] || die "$zip targets $(jq -r .targetAbi "$WORK/meta.json"), not $abi"
}

# view_release: the release as JSON in $WORK/release.json; returns 1 only when gh says there is no such release.
# Any other failure (network, rate limit, auth) stops the run: reading it as "no release" could create a second draft.
view_release() {
    if "$GH" release view "$TAG" --json isDraft,assets > "$WORK/release.json" 2> "$WORK/view.err"; then
        return 0
    fi
    grep -qi "release not found" "$WORK/view.err" && return 1
    die "could not read release $TAG: $(cat "$WORK/view.err")"
}

[ -f "$BUILT_ZIP" ] && [ -f "$BUILT_META" ] || die "$BUILT_ZIP or $BUILT_META missing; the build job hands both over"
ABI="$(jq -r .targetAbi "$BUILT_META")"

if [ -n "${EXPECTED_SHA:-}" ]; then
    TAGGED="$(git -C "$ROOT" ls-remote "$REMOTE" "refs/tags/$TAG" "refs/tags/$TAG^{}" | awk '{print $1}' | tail -1)"
    [ "$TAGGED" = "$EXPECTED_SHA" ] || die "tag $TAG points at '$TAGGED', not the verified and built commit $EXPECTED_SHA"
fi

# 1. A draft with the zip attached. Each step is skipped when an earlier run already did it.
if ! view_release; then
    "$GH" release create "$TAG" --draft --verify-tag --title "$NAME $V" --notes "$(jq -r .changelog "$BUILT_META")"
    view_release || die "release $TAG was created but cannot be read back"
fi
DRAFT="$(jq -r '.isDraft' "$WORK/release.json")"
STATE="$(jq -r --arg n "$ZIP_NAME" '[.assets[] | select(.name == $n) | (.state // "uploaded")][0] // "none"' "$WORK/release.json")"

if [ "$STATE" != none ] && [ "$STATE" != uploaded ]; then
    [ "$DRAFT" = true ] || die "published release $TAG has $ZIP_NAME in state '$STATE'; fix it by hand"
    echo "draft $TAG has an unfinished $ZIP_NAME ($STATE); uploading it again"
    "$GH" release delete-asset "$TAG" "$ZIP_NAME" --yes
    STATE=none
fi

if [ "$STATE" = uploaded ]; then
    echo "release $TAG already has $ZIP_NAME; using it, not this run's build"
    "$GH" release download "$TAG" --pattern "$ZIP_NAME" --dir "$WORK/published"
    ZIP="$WORK/published/$ZIP_NAME"
    check_zip "$ZIP" "$ABI"
else
    check_zip "$BUILT_ZIP" "$ABI"
    "$GH" release upload "$TAG" "$BUILT_ZIP"
    ZIP="$BUILT_ZIP"
fi

# 2. The manifest entry, from the attached zip's own bytes and meta.json, written and checked before anything is public.
CHECKSUM="$(md5sum "$ZIP" | awk '{print toupper($1)}')"
LISTED="$(jq -r --arg v "$V" '[.[0].versions[] | select(.version == $v) | .checksum][0] // ""' "$ROOT/manifest.json")"
[ -z "$LISTED" ] || [ "$LISTED" = "$CHECKSUM" ] \
    || die "manifest lists $V with checksum $LISTED, but the release's zip is $CHECKSUM; fix by hand (the release is still a draft if it was one)"
if [ -z "$LISTED" ]; then
    # update-manifest.sh reads the zip from dist/; put the attached bytes there.
    [ "$ZIP" = "$BUILT_ZIP" ] || cp "$ZIP" "$BUILT_ZIP"
    TARGET_ABI="$ABI" "$ROOT/scripts/update-manifest.sh" "$V" "$(jq -r .changelog "$WORK/meta.json")" "$(jq -r .timestamp "$WORK/meta.json")"
    jq -e --arg v "$V" --arg c "$CHECKSUM" --arg abi "$ABI" \
        'any(.[0].versions[]; .version == $v and .checksum == $c and .targetAbi == $abi)' "$ROOT/manifest.json" >/dev/null \
        || die "manifest entry for $V does not describe the release's zip; merge the release's branch into $BRANCH first"
fi

# 3. Publish, then push the entry (a draft's download URL is not public, so the entry must not land first).
if [ "$DRAFT" = true ]; then
    "$GH" release edit "$TAG" --draft=false
fi
if [ -n "$LISTED" ]; then
    echo "manifest already lists $V with $CHECKSUM"
    exit 0
fi

git -C "$ROOT" add manifest.json
git -C "$ROOT" commit -q -m "chore: manifest for $TAG"
for attempt in $(seq "$PUSH_ATTEMPTS"); do
    if git -C "$ROOT" push -q "$REMOTE" "HEAD:$BRANCH"; then
        echo "manifest for $TAG pushed to $BRANCH"
        exit 0
    fi
    [ "$attempt" -lt "$PUSH_ATTEMPTS" ] || break
    echo "push attempt $attempt failed; rebasing on $REMOTE/$BRANCH" >&2
    git -C "$ROOT" pull -q --rebase "$REMOTE" "$BRANCH"
done
die "could not push the manifest after $PUSH_ATTEMPTS attempts; re-run the workflow (the release itself is published)"
