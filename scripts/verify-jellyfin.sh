#!/usr/bin/env bash
# Verify Jellywave against a running Jellyfin through its API (read-only except PlaybackInfo,
# which only computes a playback plan and opens no stream).
#
#   JELLYFIN_URL=http://your.jellyfin:8096 JELLYFIN_TOKEN=<api key> scripts/verify-jellyfin.sh [username]
#
# JELLYWAVE_CHANNEL names the tile. By default it is read from the plugin's own configuration
# (LibraryName from 0.5.0.0 on, StationName before; needs an administrator's key or token), falling
# back to "Live Radio". It is matched against the Name in GET /Channels exactly (case included), and
# only that channel is checked.
#
# Checks, in order: server version; the plugin is listed and Active; exactly one channel in
# GET /Channels has that name (missing or more than one is a FAIL, because Jellyfin derives the
# channel id from the name); that channel is a user view (My Media) for the chosen user; every
# enabled station in the plugin configuration has an audio item under the tile, matched by the item
# name "<station name> — Live" (the plugin renames an item within one poll interval of a station
# rename, so check again after that); without access to the configuration, at least one audio item.
# Then, for every audio item: the media sources the item stores (GET /Items/{id}) all have
# SupportsDirectPlay false (card L10: relay only; PlaybackInfo cannot show this, since Jellyfin
# recomputes the flag there from the device profile, and this script's profile allows no direct
# play), and PlaybackInfo has no ErrorCode, at least one media source and a TranscodingUrl on every
# source. PlaybackInfo is asked
# with a minimal audio profile whose only target is mp3 over HTTP, with no limit on the input: the
# server transcodes MP3, AAC and HLS input alike to it (with an empty body it has no format to
# transcode to and returns no TranscodingUrl).
#
# It does not check what the now-playing poller writes (description, cover). Since card L14 the server asks a SUB/WAVE
# station only while someone plays it or has its station page open, so with nobody listening its item shows the
# station's own look (the default text and the Jellywave logo); a run of this script plays nothing and opens no page,
# so it never wakes a station.
set -euo pipefail

: "${JELLYFIN_URL:?set JELLYFIN_URL, e.g. http://your.jellyfin:8096}"
: "${JELLYFIN_TOKEN:?set JELLYFIN_TOKEN to an API key (Dashboard > API Keys) or a session token}"
USERNAME="${1:-}"
AUTH="Authorization: MediaBrowser Token=${JELLYFIN_TOKEN}, Client=\"jellywave-verify\", Device=\"cli\", DeviceId=\"jellywave-verify\", Version=\"1\""
PLUGIN_NAME="Jellywave"
DEFAULT_CHANNEL="Live Radio"
PLAYBACK_PROFILE='{"DeviceProfile":{"Name":"jellywave-verify","MaxStreamingBitrate":320000,"DirectPlayProfiles":[],"TranscodingProfiles":[{"Container":"mp3","Type":"Audio","AudioCodec":"mp3","Protocol":"http","Context":"Streaming"}]}}'
fail=0
ok()   { echo "ok   $*"; }
bad()  { echo "FAIL $*"; fail=1; }
get()  { curl -sS -f -H "$AUTH" "$JELLYFIN_URL$1"; }

echo "== Server"; get /System/Info/Public | jq -r '"\(.ServerName) \(.Version)"'

echo "== Plugin"
PLUGIN="$(get /Plugins | jq -c --arg n "$PLUGIN_NAME" '.[] | select(.Name == $n)')"
[ -n "$PLUGIN" ] && ok "plugin listed: $(jq -r '"\(.Version) \(.Status)"' <<<"$PLUGIN")" || bad "plugin $PLUGIN_NAME not in /Plugins"
CONFIGURED_NAME=""
CONFIG_JSON=""
if [ -n "$PLUGIN" ]; then
  CONFIG_JSON="$(get "/Plugins/$(jq -r .Id <<<"$PLUGIN")/Configuration" 2>/dev/null || true)"
  CONFIGURED_NAME="$(jq -r '(.LibraryName // .StationName // "") | gsub("^\\s+|\\s+$"; "")' <<<"${CONFIG_JSON:-null}" 2>/dev/null || true)"
fi
# Enabled stations in grid order (SortOrder, list order breaking ties), as the item names the plugin gives them.
# Empty when the configuration cannot be read (not an administrator) or predates 0.5.0.0 (no Stations).
EXPECTED_ITEMS=""
if [ -n "$CONFIG_JSON" ] && jq -e '.Stations | type == "array"' <<<"$CONFIG_JSON" >/dev/null 2>&1; then
  EXPECTED_ITEMS="$(jq -c '[.Stations | to_entries[] | select(.value != null and .value.Enabled == true)]
    | sort_by(.value.SortOrder, .key)
    | map(((.value.Name // "") | gsub("^\\s+|\\s+$"; "")) as $n | (if $n == "" then "SUB/WAVE" else $n end) + " — Live")' <<<"$CONFIG_JSON")"
fi
CHANNEL_NAME="${JELLYWAVE_CHANNEL:-${CONFIGURED_NAME:-$DEFAULT_CHANNEL}}"
echo "     station tile name: \"$CHANNEL_NAME\" ($([ -n "${JELLYWAVE_CHANNEL:-}" ] && echo "from JELLYWAVE_CHANNEL" || { [ -n "$CONFIGURED_NAME" ] && echo "from the plugin configuration" || echo "default; set JELLYWAVE_CHANNEL if the tile was renamed"; }))"

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
  AUDIO_IDS="$(jq -r '.Items[] | select(.Type == "Audio") | .Id' <<<"$ITEMS")"
  if [ -n "$EXPECTED_ITEMS" ]; then
    EXPECTED_COUNT="$(jq 'length' <<<"$EXPECTED_ITEMS")"
    [ "$EXPECTED_COUNT" -gt 0 ] && ok "$EXPECTED_COUNT enabled station(s) in the plugin configuration" || bad "no enabled station in the plugin configuration"
    # One line per enabled station; two stations with one name need two items of that name.
    while IFS= read -r name; do
      [ -n "$name" ] || continue
      want="$(jq --arg n "$name" '[.[] | select(. == $n)] | length' <<<"$EXPECTED_ITEMS")"
      have="$(jq --arg n "$name" '[.Items[] | select(.Type == "Audio" and .Name == $n)] | length' <<<"$ITEMS")"
      [ "$have" -ge "$want" ] && ok "station item \"$name\" is under the tile" || bad "station item \"$name\" is missing (station URL or format refused: see the server log; or the station was renamed less than one poll interval ago)"
    done < <(jq -r 'unique[]' <<<"$EXPECTED_ITEMS")
  elif [ -z "$AUDIO_IDS" ]; then
    bad "\"$CHANNEL_NAME\" has no audio item (is a station enabled and its URL allowed?)"
  else
    echo "     no station list (configuration not readable with this key, or from before 0.5.0.0); checking every audio item instead of every station"
  fi
  for ITEM_ID in $AUDIO_IDS; do
    ITEM_NAME="$(jq -r --arg id "$ITEM_ID" '.Items[] | select(.Id == $id) | .Name' <<<"$ITEMS")"
    if STORED="$(get "/Items/$ITEM_ID?userId=$USER_ID")"; then
      jq -e '(.MediaSources | length) > 0 and all(.MediaSources[]; .SupportsDirectPlay == false)' <<<"$STORED" >/dev/null \
        && ok "\"$ITEM_NAME\": every stored media source has SupportsDirectPlay false (relay only)" \
        || bad "\"$ITEM_NAME\": a stored media source allows direct play, or the item has none: $(jq -c '[.MediaSources[]?.SupportsDirectPlay]' <<<"$STORED")"
    else
      bad "item request for \"$ITEM_NAME\" failed"
    fi
    echo "== PlaybackInfo for \"$ITEM_NAME\" ($ITEM_ID)"
    PI="$(curl -sS -f -X POST -H "$AUTH" -H 'Content-Type: application/json' "$JELLYFIN_URL/Items/$ITEM_ID/PlaybackInfo?userId=$USER_ID" -d "$PLAYBACK_PROFILE")" || { bad "PlaybackInfo request for \"$ITEM_NAME\" failed"; continue; }
    # Jellyfin puts the caller's key in TranscodingUrl as ApiKey=; never print it.
    jq '.MediaSources[] | {Path, Protocol, IsRemote, IsInfiniteStream, Container, SupportsDirectPlay, SupportsDirectStream, SupportsTranscoding, TranscodingUrl: (.TranscodingUrl // null | if . then gsub("(?<k>[Aa]pi_?[Kk]ey)=[^&]*"; "\(.k)=<redacted>") else . end), TranscodingContainer, ErrorCode}' <<<"$PI"
    jq -e '.ErrorCode == null' <<<"$PI" >/dev/null && ok "\"$ITEM_NAME\": PlaybackInfo has no ErrorCode" || bad "\"$ITEM_NAME\": PlaybackInfo ErrorCode: $(jq -r .ErrorCode <<<"$PI")"
    if ! jq -e '(.MediaSources | length) > 0' <<<"$PI" >/dev/null; then
      bad "\"$ITEM_NAME\": PlaybackInfo has no media source"
      continue
    fi
    CONTAINERS="$(jq -r '[.MediaSources[].Container // "unset"] | join(",")' <<<"$PI")"
    jq -e 'all(.MediaSources[]; .TranscodingUrl != null)' <<<"$PI" >/dev/null && ok "\"$ITEM_NAME\": PlaybackInfo gives every source a TranscodingUrl (container $CONTAINERS)" || bad "\"$ITEM_NAME\": a source has no TranscodingUrl in PlaybackInfo (container $CONTAINERS): NoCompatibleStream"
  done
fi

[ "$fail" = 0 ] && echo "== ALL CHECKS PASSED" || { echo "== SOME CHECKS FAILED"; exit 1; }
