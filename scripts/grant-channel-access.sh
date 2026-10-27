#!/usr/bin/env bash
# Grant every user access to one channel (default: the Jellywave station) by adding the channel
# to each user's EnabledChannels. Users who already have "all channels" or the channel are skipped.
# Dry run by default; --apply writes the policies. Administrators are treated like everyone else:
# Jellyfin's channel visibility check has no admin exemption.
#
#   JELLYFIN_URL=http://your.jellyfin:8096 JELLYFIN_TOKEN=<admin api key> scripts/grant-channel-access.sh [--apply] [channel name]
#
# Why this exists: Jellyfin shows a channel only to users with "Enable access to all channels" or
# with that channel ticked under Dashboard > Users > user > Access > Channel access. Users created
# from a template or by a provisioning tool often have neither.
set -euo pipefail
: "${JELLYFIN_URL:?set JELLYFIN_URL, e.g. http://your.jellyfin:8096}"
: "${JELLYFIN_TOKEN:?set JELLYFIN_TOKEN to an administrator API key}"
APPLY=0; CHANNEL_NAME=""
for arg in "$@"; do case "$arg" in --apply) APPLY=1 ;; *) CHANNEL_NAME="$arg" ;; esac; done
AUTH="Authorization: MediaBrowser Token=${JELLYFIN_TOKEN}, Client=\"jellywave-grant\", Device=\"cli\", DeviceId=\"jellywave-grant\", Version=\"1\""
get() { curl -sS -f -H "$AUTH" "$JELLYFIN_URL$1"; }

CHANNELS="$(get /Channels)"
if [ -z "$CHANNEL_NAME" ]; then
    N="$(jq '.Items | length' <<<"$CHANNELS")"
    [ "$N" = 1 ] || { echo "found $N channels; name one of: $(jq -r '.Items[].Name' <<<"$CHANNELS" | paste -sd, -)" >&2; exit 1; }
    CHANNEL_NAME="$(jq -r '.Items[0].Name' <<<"$CHANNELS")"
fi
CHANNEL_ID="$(jq -r --arg n "$CHANNEL_NAME" '.Items[] | select(.Name == $n) | .Id' <<<"$CHANNELS")"
[ -n "$CHANNEL_ID" ] || { echo "channel '$CHANNEL_NAME' not found" >&2; exit 1; }
echo "channel: $CHANNEL_NAME ($CHANNEL_ID); mode: $([ "$APPLY" = 1 ] && echo APPLY || echo dry-run)"

USERS="$(get /Users)"   # fetched first so a failure stops the script instead of yielding an empty loop
changed=0
while IFS=$'\t' read -r id name all has; do
    if [ "$all" = true ]; then echo "skip  $name (all channels)"; continue; fi
    if [ "$has" = true ]; then echo "skip  $name (already enabled)"; continue; fi
    echo "grant $name"
    changed=$((changed + 1))
    if [ "$APPLY" = 1 ]; then
        POLICY="$(get "/Users/$id" | jq --arg c "$CHANNEL_ID" '.Policy | .EnabledChannels = ((.EnabledChannels // []) + [$c] | unique)')"
        curl -sS -f -o /dev/null -X POST -H "$AUTH" -H 'Content-Type: application/json' "$JELLYFIN_URL/Users/$id/Policy" -d "$POLICY"
    fi
done < <(jq -r --arg c "$CHANNEL_ID" '.[] | [.Id, .Name, (.Policy.EnableAllChannels // false), (((.Policy.EnabledChannels // []) | map(ascii_downcase) | index($c | ascii_downcase)) != null)] | @tsv' <<<"$USERS")
echo "$changed user(s) $([ "$APPLY" = 1 ] && echo updated || echo would be updated); re-run with --apply to write"
