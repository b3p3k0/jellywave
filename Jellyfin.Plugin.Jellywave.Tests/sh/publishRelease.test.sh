#!/usr/bin/env bash
# scripts/publish-release.sh against a stand-in gh (fake-gh) and a local bare git remote: a first run, and re-runs
# after each interruption the release workflow can leave behind. Plain bash; run from anywhere.
#   bash Jellyfin.Plugin.Jellywave.Tests/sh/publishRelease.test.sh
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
T="$(mktemp -d)"
trap 'rm -rf "$T"' EXIT
TAG=v9.9.9.0
V=9.9.9.0
NAME="$(sed -n 's/^name: "\(.*\)"$/\1/p' "$REPO/build.yaml")"
GUID="$(sed -n 's/^guid: "\(.*\)"$/\1/p' "$REPO/build.yaml")"
ABI="$(sed -n 's/^targetAbi: "\(.*\)"$/\1/p' "$REPO/build.yaml")"
ZIP_NAME="${NAME}_${V}.zip"
pass=0
fail=0
ok() { echo "ok   $1"; pass=$((pass + 1)); }
bad() { echo "FAIL $1"; fail=$((fail + 1)); }
expect() { if [ "$2" = "$3" ]; then ok "$1"; else bad "$1: '$2' != '$3'"; fi; }
md5() { md5sum "$1" | awk '{print toupper($1)}'; }
git_q() { git -c user.name=test -c user.email=test@example.invalid -c init.defaultBranch=main "$@"; }

# make_zip OUT STAMP [GUID] [EXTRA] [VERSION] [ABI]: a plugin zip whose bytes depend on STAMP (each build embeds its
# own timestamp).
make_zip() {
    local out="$1" stamp="$2" guid="${3:-$GUID}" extra="${4:-}" version="${5:-$V}" abi="${6:-$ABI}" d
    d="$(mktemp -d -p "$T")"
    echo "dll $stamp" > "$d/Jellyfin.Plugin.Jellywave.dll"
    echo "png" > "$d/thumb.png"
    jq -n --arg g "$guid" --arg v "$version" --arg a "$abi" --arg t "$stamp" \
        '{guid:$g, version:$v, targetAbi:$a, timestamp:$t, changelog:("built " + $t)}' > "$d/meta.json"
    [ -z "$extra" ] || echo extra > "$d/$extra"
    (cd "$d" && zip -q -X -j "$out" ./*)
}

# A seed repo holding main with the scripts, build.yaml and the repo's manifest, tagged $TAG.
mkdir -p "$T/seed/scripts"
cp "$REPO/scripts/lib.sh" "$REPO/scripts/update-manifest.sh" "$REPO/scripts/publish-release.sh" "$T/seed/scripts/"
cp "$REPO/build.yaml" "$REPO/manifest.json" "$T/seed/"
git_q init -q "$T/seed"
git_q -C "$T/seed" add -A
git_q -C "$T/seed" commit -q -m seed
git_q -C "$T/seed" tag -a -m "$TAG" "$TAG"
SEED_SHA="$(git -C "$T/seed" rev-parse HEAD)"

# fresh: a new remote cloned from the seed, and no release on the stand-in.
fresh() {
    rm -rf "$T/remote.git" "$T/gh"
    git_q clone -q --bare "$T/seed" "$T/remote.git"
    mkdir -p "$T/gh/assets" "$T/gh/asset-state"; echo false > "$T/gh/draft"; : > "$T/gh/calls"
}
# existing DRAFT [STAMP] [args for make_zip after STAMP]: a release already there (with a zip when STAMP is given).
existing() {
    touch "$T/gh/exists"; echo "$1" > "$T/gh/draft"
    [ -z "${2:-}" ] || make_zip "$T/gh/assets/$ZIP_NAME" "${@:2}"
}
# run STAMP [TAG] [BUILT_ABI]: a fresh checkout of main (as the publish job makes) with this run's build in dist/,
# then the script. Extra environment (FAKE_GH_*, EXPECTED_SHA) passes through.
run() {
    local stamp="$1" tag="${2:-$TAG}" built_abi="${3:-$ABI}" w
    w="$T/run-$RANDOM$RANDOM"
    git_q clone -q "$T/remote.git" "$w"
    git_q -C "$w" config user.name test
    git_q -C "$w" config user.email test@example.invalid
    mkdir -p "$w/dist/${NAME}_${V}"
    make_zip "$w/dist/$ZIP_NAME" "$stamp" "$GUID" "" "$V" "$built_abi"
    unzip -p "$w/dist/$ZIP_NAME" meta.json > "$w/dist/${NAME}_${V}/meta.json"
    LAST_BUILD="$w/dist/$ZIP_NAME"
    GH="$HERE/fake-gh" FAKE_GH_STATE="$T/gh" bash "$w/scripts/publish-release.sh" "$tag" > "$T/out" 2>&1
}
remote_checksum() {
    git -C "$T/remote.git" show main:manifest.json | jq -r --arg v "$V" '[.[0].versions[] | select(.version == $v) | .checksum][0] // ""'
}
remote_head() { git -C "$T/remote.git" rev-parse main; }
draft() { cat "$T/gh/draft"; }
asset_md5() { md5 "$T/gh/assets/$ZIP_NAME"; }
calls() { grep -c "^$1" "$T/gh/calls" || true; }
says() { if grep -q "$2" "$T/out"; then ok "$1"; else bad "$1: $(tail -1 "$T/out")"; fi; }

echo "== first run"
fresh
if EXPECTED_SHA="$SEED_SHA" run stamp-1; then ok "first run succeeds"; else bad "first run: $(cat "$T/out")"; fi
expect "release published" "$(draft)" false
FIRST="$(asset_md5)"
expect "asset is this run's build" "$FIRST" "$(md5 "$LAST_BUILD")"
expect "manifest names the asset's checksum" "$(remote_checksum)" "$FIRST"
expect "created as a draft" "$(grep -c -- '^create .*--draft' "$T/gh/calls")" 1
expect "no --clobber" "$(grep -c -- '--clobber' "$T/gh/calls" || true)" 0

echo "== re-run after success"
HEAD_BEFORE="$(remote_head)"
if run stamp-2; then ok "re-run succeeds"; else bad "re-run: $(cat "$T/out")"; fi
expect "asset unchanged" "$(asset_md5)" "$FIRST"
expect "no new manifest commit" "$(remote_head)" "$HEAD_BEFORE"

echo "== draft with the zip already attached (stopped before publish)"
fresh; existing true stamp-earlier
EARLIER="$(asset_md5)"
if run stamp-3; then ok "run succeeds"; else bad "run: $(cat "$T/out")"; fi
expect "earlier zip kept" "$(asset_md5)" "$EARLIER"
expect "manifest names the earlier zip" "$(remote_checksum)" "$EARLIER"
expect "published" "$(draft)" false
expect "no upload" "$(calls upload)" 0

echo "== published release without a manifest entry"
fresh; existing false stamp-published
PUBLISHED="$(asset_md5)"
if FAKE_GH_IMMUTABLE=1 run stamp-4; then ok "run succeeds"; else bad "run: $(cat "$T/out")"; fi
expect "manifest names the published zip" "$(remote_checksum)" "$PUBLISHED"
expect "no edit of a published release" "$(calls edit)" 0

echo "== manifest push rejected, then re-run"
fresh
printf '#!/bin/sh\necho rejected >&2\nexit 1\n' > "$T/remote.git/hooks/pre-receive"; chmod +x "$T/remote.git/hooks/pre-receive"
if run stamp-5; then bad "run with a rejected push succeeded"; else ok "run with a rejected push fails"; fi
says "says to re-run" "re-run the workflow"
expect "release published anyway" "$(draft)" false
expect "remote manifest unchanged" "$(remote_checksum)" ""
ASSET="$(asset_md5)"
rm "$T/remote.git/hooks/pre-receive"
if run stamp-6; then ok "re-run succeeds"; else bad "re-run: $(cat "$T/out")"; fi
expect "manifest names the first run's zip" "$(remote_checksum)" "$ASSET"

echo "== main moves while the run is publishing"
fresh
OTHER="$T/other-$RANDOM"; git_q clone -q "$T/remote.git" "$OTHER"
MOVE="cd '$OTHER' && echo moved > other.txt && git add other.txt && git -c user.name=o -c user.email=o@example.invalid commit -q -m other && git push -q origin HEAD:main"
if FAKE_GH_ON_EDIT="$MOVE" run stamp-7; then ok "run succeeds"; else bad "run: $(cat "$T/out")"; fi
expect "the other commit is kept" "$(git -C "$T/remote.git" show main:other.txt 2>/dev/null)" moved
expect "manifest names the zip" "$(remote_checksum)" "$(asset_md5)"

echo "== upload fails once, then re-run"
fresh
if FAKE_GH_FAIL=upload run stamp-8; then bad "run with a failed upload succeeded"; else ok "run with a failed upload fails"; fi
expect "still a draft" "$(draft)" true
if run stamp-9; then ok "re-run succeeds"; else bad "re-run: $(cat "$T/out")"; fi
expect "manifest names the uploaded zip" "$(remote_checksum)" "$(asset_md5)"

echo "== upload lands but reports a failure, then re-run"
fresh
if FAKE_GH_FAIL_AFTER=upload run stamp-10; then bad "run succeeded"; else ok "run fails"; fi
LANDED="$(asset_md5)"
if run stamp-11; then ok "re-run succeeds"; else bad "re-run: $(cat "$T/out")"; fi
expect "the landed zip is kept" "$(asset_md5)" "$LANDED"
expect "manifest names it" "$(remote_checksum)" "$LANDED"

echo "== create lands but reports a failure, then re-run"
fresh
if FAKE_GH_FAIL_AFTER=create run stamp-12; then bad "run succeeded"; else ok "run fails"; fi
if run stamp-13; then ok "re-run succeeds"; else bad "re-run: $(cat "$T/out")"; fi
expect "one create in all" "$(calls create)" 1
expect "published" "$(draft)" false

echo "== reading the release fails with a server error"
fresh; existing true
if FAKE_GH_FAIL=view run stamp-14; then bad "run succeeded"; else ok "run stops"; fi
expect "no second draft created" "$(calls create)" 0
says "says why" "could not read release"

echo "== a draft whose zip never finished uploading"
fresh; existing true stamp-partial
echo starter > "$T/gh/asset-state/$ZIP_NAME"
if run stamp-15; then ok "run succeeds"; else bad "run: $(cat "$T/out")"; fi
expect "unfinished zip deleted" "$(calls delete-asset)" 1
expect "this run's build attached" "$(asset_md5)" "$(md5 "$LAST_BUILD")"
expect "manifest names it" "$(remote_checksum)" "$(asset_md5)"

echo "== a published release whose zip never finished uploading"
fresh; existing false stamp-partial
echo starter > "$T/gh/asset-state/$ZIP_NAME"
if run stamp-16; then bad "run succeeded"; else ok "run stops"; fi
expect "nothing deleted" "$(calls delete-asset)" 0
expect "no manifest entry" "$(remote_checksum)" ""

echo "== manifest already lists the version with another checksum"
for start in none draft published; do
    fresh
    case "$start" in draft) existing true ;; published) existing false stamp-other ;; esac
    git_q clone -q "$T/remote.git" "$T/edit-$start"
    jq --arg v "$V" '.[0].versions = [{version:$v, checksum:"00000000000000000000000000000000", targetAbi:"x", sourceUrl:"x", changelog:"", timestamp:""}] + .[0].versions' \
        "$T/edit-$start/manifest.json" > "$T/m.json" && mv "$T/m.json" "$T/edit-$start/manifest.json"
    git_q -C "$T/edit-$start" commit -q -am "conflicting entry" && git -C "$T/edit-$start" push -q origin HEAD:main
    HEAD_BEFORE="$(remote_head)"
    if run stamp-17; then bad "a checksum disagreement succeeded ($start)"; else ok "a checksum disagreement fails ($start)"; fi
    says "says why ($start)" "fix by hand"
    expect "no manifest commit ($start)" "$(remote_head)" "$HEAD_BEFORE"
    [ "$start" = published ] || expect "release left a draft ($start)" "$(draft)" true
done

echo "== attached zip that is not this plugin, version or ABI, or has an extra file"
for kind in guid extra version abi; do
    fresh
    case "$kind" in
        guid) existing true stamp-x 00000000-0000-0000-0000-000000000000 ;;
        extra) existing true stamp-x "$GUID" evil.dll ;;
        version) existing true stamp-x "$GUID" "" 9.9.9.1 ;;
        abi) existing true stamp-x "$GUID" "" "$V" 1.2.3.4 ;;
    esac
    if run stamp-18; then bad "a wrong zip ($kind) was accepted"; else ok "a wrong zip ($kind) is refused"; fi
    expect "not published ($kind)" "$(draft)" true
    expect "no manifest entry ($kind)" "$(remote_checksum)" ""
done

echo "== main's update-manifest.sh writes another ABI than the zip's (it predates TARGET_ABI)"
fresh
git_q clone -q "$T/remote.git" "$T/old-script"
sed -i 's/\${TARGET_ABI:-\$(yaml_get targetAbi)}/$(yaml_get targetAbi)/' "$T/old-script/scripts/update-manifest.sh"
git_q -C "$T/old-script" commit -q -am "an update-manifest.sh without TARGET_ABI" && git -C "$T/old-script" push -q origin HEAD:main
if run stamp-19 "$TAG" 9.8.7.6; then bad "an entry with the wrong ABI was accepted"; else ok "an entry with the wrong ABI is refused"; fi
says "says to merge first" "merge the release's branch"
expect "left a draft" "$(draft)" true
expect "no manifest entry" "$(remote_checksum)" ""

echo "== the tag moved after the workflow verified it, or is malformed"
fresh
if EXPECTED_SHA=0123456789abcdef0123456789abcdef01234567 run stamp-20; then bad "a moved tag was accepted"; else ok "a moved tag is refused"; fi
expect "nothing created" "$(calls create)" 0
fresh
if run stamp-21 9.9.9.0; then bad "a tag without v was accepted"; else ok "a tag without v is refused"; fi
expect "nothing created" "$(calls create)" 0

echo "== $pass passed, $fail failed"
[ "$fail" -eq 0 ]
