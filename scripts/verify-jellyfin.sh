#!/usr/bin/env bash
# Verify Jellywave against a running Jellyfin through its API (read-only except PlaybackInfo,
# which only computes a playback plan and opens no stream).
#
#   JELLYFIN_URL=http://your.jellyfin:8096 JELLYFIN_TOKEN=<api key> scripts/verify-jellyfin.sh [username]
#
# JELLYWAVE_CHANNEL names the station's tile (default "SUB/WAVE"); set it when the tile was renamed
# under Dashboard > Plugins > Jellywave. It is matched against the Name in GET /Channels exactly
# (case included), and only that channel is checked.
#
# Checks, in order: server version; the plugin is listed and Active; exactly one channel in
# GET /Channels has that name (missing or more than one is a FAIL, because Jellyfin derives the
# channel id from the name); that channel is a user view (My Media) for the chosen user; it has
# an audio item; PlaybackInfo for that item has no ErrorCode and a play method (Relay:
# TranscodingUrl present; Direct: direct play).
set -euo pipefail

: "${JELLYFIN_URL:?set JELLYFIN_URL, e.g. http://your.jellyfin:8096}"
: "${JELLYFIN_TOKEN:?set JELLYFIN_TOKEN to an API key (Dashboard > API Keys) or a session token}"
USERNAME="${1:-}"
AUTH="Authorization: MediaBrowser Token=${JELLYFIN_TOKEN}, Client=\"jellywave-verify\", Device=\"cli\", DeviceId=\"jellywave-verify\", Version=\"1\""
PLUGIN_NAME="Jellywave"
CHANNEL_NAME="${JELLYWAVE_CHANNEL:-SUB/WAVE}"
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
CHANNELS="$(get "/Channels?userId=$USER_ID")"
jq -r '.Items[] | "     channel: \(.Name) \(.Id)"' <<<"$CHANNELS"
MATCHES="$(jq -c --arg n "$CHANNEL_NAME" '[.Items[] | select(.Name == $n)]' <<<"$CHANNELS")"
COUNT="$(jq 'length' <<<"$MATCHES")"
CH_ID=""
case "$COUNT" in
  0) bad "no channel named \"$CHANNEL_NAME\" (set JELLYWAVE_CHANNEL to the tile name; IChannel registration missing, plugin not loaded, or the channel is hidden from this user)" ;;
  1) CH_ID="$(jq -r '.[0].Id' <<<"$MATCHES")"; ok "channel \"$CHANNEL_NAME\" found once ($CH_ID)" ;;
  *) bad "$COUNT channels named \"$CHANNEL_NAME\": another plugin's channel has the same name, so they share one channel id" ;;
esac

echo "== My Media (UserViews)"
VIEWS="$(get "/UserViews?userId=$USER_ID")"
echo "$VIEWS" | jq -r '.Items[] | "     view: \(.Name) [\(.Type)\(if .CollectionType then "/" + .CollectionType else "" end)]"'
if [ -n "$CH_ID" ]; then
  jq -e --arg id "$CH_ID" '.Items[] | select(.Type == "Channel" and .Id == $id)' <<<"$VIEWS" >/dev/null && ok "\"$CHANNEL_NAME\" appears among user views" || bad "\"$CHANNEL_NAME\" is not in UserViews"
fi

if [ -n "$CH_ID" ]; then
  echo "== Channel items"
  ITEMS="$(get "/Channels/$CH_ID/Items?userId=$USER_ID&Fields=MediaSources,Overview,Tags")"
  N="$(jq '.TotalRecordCount' <<<"$ITEMS")"
  echo "     channel $CH_ID: $N item(s)"
  jq -r '.Items[] | "       - \(.Name) [\(.Type)] RunTimeTicks=\(.RunTimeTicks) tags=\(.Tags|join(",")) id=\(.Id)"' <<<"$ITEMS"
  ITEM_ID="$(jq -r '.Items[] | select(.Type == "Audio") | .Id' <<<"$ITEMS" | head -1)"
  if [ -z "$ITEM_ID" ]; then
    bad "\"$CHANNEL_NAME\" has no audio item (is the station base URL set and allowed?)"
  else
    echo "== PlaybackInfo for $ITEM_ID"
    PI="$(curl -sS -f -X POST -H "$AUTH" -H 'Content-Type: application/json' "$JELLYFIN_URL/Items/$ITEM_ID/PlaybackInfo?userId=$USER_ID" -d '{}')"
    jq '.MediaSources[] | {Path, Protocol, IsRemote, IsInfiniteStream, Container, SupportsDirectPlay, SupportsDirectStream, SupportsTranscoding, TranscodingUrl, TranscodingContainer, ErrorCode}' <<<"$PI"
    jq -e '.ErrorCode == null' <<<"$PI" >/dev/null && ok "PlaybackInfo has no ErrorCode" || bad "PlaybackInfo ErrorCode: $(jq -r .ErrorCode <<<"$PI")"
    jq -e '.MediaSources[0] | (.SupportsDirectPlay or (.TranscodingUrl != null))' <<<"$PI" >/dev/null && ok "a play method exists (direct play or TranscodingUrl)" || bad "no play method: NoCompatibleStream"
  fi
fi

[ "$fail" = 0 ] && echo "== ALL CHECKS PASSED" || { echo "== SOME CHECKS FAILED"; exit 1; }
