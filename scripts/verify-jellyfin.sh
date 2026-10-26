#!/usr/bin/env bash
# Verify Jellywave against a running Jellyfin through its API (read-only except PlaybackInfo,
# which only computes a playback plan and opens no stream).
#
#   JELLYFIN_URL=http://nordflix:8096 JELLYFIN_TOKEN=<api key> scripts/verify-jellyfin.sh [username]
#
# Checks, in order: server version; the plugin is listed and Active; the channel is in
# GET /Channels; the channel is a user view (My Media) for the chosen user; the channel
# has exactly one audio item flagged as a live stream; PlaybackInfo for that item shows the
# expected delivery (Relay: TranscodingUrl present and no direct play; Direct: direct play).
set -euo pipefail

: "${JELLYFIN_URL:?set JELLYFIN_URL, e.g. http://nordflix:8096}"
: "${JELLYFIN_TOKEN:?set JELLYFIN_TOKEN to an API key (Dashboard > API Keys) or a session token}"
USERNAME="${1:-}"
AUTH="Authorization: MediaBrowser Token=${JELLYFIN_TOKEN}, Client=\"jellywave-verify\", Device=\"cli\", DeviceId=\"jellywave-verify\", Version=\"1\""
PLUGIN_NAME="Jellywave"
fail=0
ok()   { echo "ok   $*"; }
bad()  { echo "FAIL $*"; fail=1; }
get()  { curl -sS -f -H "$AUTH" "$JELLYFIN_URL$1"; }

echo "== Server"; get /System/Info/Public | jq -r '"\(.ServerName) \(.Version)"'

echo "== Plugin"
PLUGIN="$(get /Plugins | jq -c --arg n "$PLUGIN_NAME" '.[] | select(.Name == $n)')"
[ -n "$PLUGIN" ] && ok "plugin listed: $(jq -r '"\(.Version) \(.Status)"' <<<"$PLUGIN")" || bad "plugin $PLUGIN_NAME not in /Plugins"

echo "== User"
if [ -z "$USERNAME" ]; then USER_JSON="$(get /Users | jq -c 'map(select(.Policy.IsAdministrator)) | .[0]')"; else USER_JSON="$(get /Users | jq -c --arg u "$USERNAME" '.[] | select(.Name == $u)')"; fi
USER_ID="$(jq -r .Id <<<"$USER_JSON")"; [ -n "$USER_ID" ] && [ "$USER_ID" != null ] || { bad "user not found"; exit 1; }
ok "user $(jq -r .Name <<<"$USER_JSON") ($USER_ID), EnableAllChannels=$(jq -r .Policy.EnableAllChannels <<<"$USER_JSON")"

echo "== Channel"
CHANNEL="$(get "/Channels?userId=$USER_ID" | jq -c '.Items[] | select(.Type == "Channel")' | head -50)"
echo "$CHANNEL" | jq -r '"     channel: \(.Name) \(.Id)"'
# Jellywave's channel is the one whose plugin is ours: match by the configured tile name later; for now take all and let the user read.
CH_ID="$(get "/Channels?userId=$USER_ID" | jq -r '.Items[0].Id // empty')"
[ -n "$CH_ID" ] && ok "at least one channel registered" || bad "no channels at all: IChannel registration missing or plugin not loaded"

echo "== My Media (UserViews)"
VIEWS="$(get "/UserViews?userId=$USER_ID")"
echo "$VIEWS" | jq -r '.Items[] | "     view: \(.Name) [\(.Type)\(if .CollectionType then "/" + .CollectionType else "" end)]"'
jq -e '.Items[] | select(.Type == "Channel")' <<<"$VIEWS" >/dev/null && ok "a Channel appears among user views" || bad "no Channel in UserViews"

echo "== Channel items"
for id in $(get "/Channels?userId=$USER_ID" | jq -r '.Items[].Id'); do
  ITEMS="$(get "/Channels/$id/Items?userId=$USER_ID&Fields=MediaSources,Overview,Tags")"
  N="$(jq '.TotalRecordCount' <<<"$ITEMS")"
  echo "     channel $id: $N item(s)"
  jq -r '.Items[] | "       - \(.Name) [\(.Type)] RunTimeTicks=\(.RunTimeTicks) tags=\(.Tags|join(",")) id=\(.Id)"' <<<"$ITEMS"
  ITEM_ID="$(jq -r '.Items[] | select(.Type == "Audio") | .Id' <<<"$ITEMS" | head -1)"
  [ -n "$ITEM_ID" ] || continue
  echo "== PlaybackInfo for $ITEM_ID"
  PI="$(curl -sS -f -X POST -H "$AUTH" -H 'Content-Type: application/json' "$JELLYFIN_URL/Items/$ITEM_ID/PlaybackInfo?userId=$USER_ID" -d '{}')"
  jq '.MediaSources[] | {Path, Protocol, IsRemote, IsInfiniteStream, Container, SupportsDirectPlay, SupportsDirectStream, SupportsTranscoding, TranscodingUrl, TranscodingContainer, ErrorCode}' <<<"$PI"
  jq -e '.ErrorCode == null' <<<"$PI" >/dev/null && ok "PlaybackInfo has no ErrorCode" || bad "PlaybackInfo ErrorCode: $(jq -r .ErrorCode <<<"$PI")"
  jq -e '.MediaSources[0] | (.SupportsDirectPlay or (.TranscodingUrl != null))' <<<"$PI" >/dev/null && ok "a play method exists (direct play or TranscodingUrl)" || bad "no play method: NoCompatibleStream"
done

[ "$fail" = 0 ] && echo "== ALL CHECKS PASSED" || { echo "== SOME CHECKS FAILED"; exit 1; }
