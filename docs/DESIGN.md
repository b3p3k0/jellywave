# Jellywave design

How the plugin works, what it relies on in Jellyfin, and why it is shaped this way.
Written against Jellyfin 12.3; file references are to the Jellyfin server source.

## The tile

Jellyfin builds the "My Media" row from `UserViewManager.GetUserViews`, which appends
every registered `IChannel` as its own view right before the Live TV folder. Jellywave
registers one `IChannel` (`JellywaveChannel`), so the station appears beside the
libraries on every client without any client-side change.

Jellyfin 12 does not discover `IChannel` implementations by reflection. `ChannelManager`
receives `IEnumerable<IChannel>` from dependency injection, so the plugin registers the
channel in `PluginServiceRegistrator` (`AddSingleton<IChannel, JellywaveChannel>()`).
Without that line the plugin loads but no tile appears.

The channel's internal id is derived from its name (`ChannelManager.GetInternalChannelId`),
which is why renaming the tile creates a new channel; the "Refresh Internet Channels"
scheduled task removes channels whose plugin no longer reports them.

The tile image is the embedded `Assets/logo.png`, served through `IChannel.GetChannelImage`.

## The live item

`GetChannelItems` returns exactly one `ChannelItemInfo`:

| Field | Value | Why |
|---|---|---|
| `Id` | `live` | Stable external id so Jellyfin keeps one `Audio` entity across refreshes |
| `MediaType` / `ContentType` | Audio / Song | Becomes an `Audio` item, played by the audio player on every client |
| `IsLiveStream` | true | Jellyfin nulls the item's runtime and tags it `livestream`; lists show "Live" |
| `DateModified` | max(config `LastChangedUtc`, `MediaSourceSchemaDate`) | Jellyfin only re-saves a channel item's media sources when this advances (`ChannelManager.GetChannelItemEntityAsync`) |
| `MediaSources` | one static `MediaSourceInfo` | See below |

`DataVersion` is a hash of every configuration field that affects the item. Jellyfin caches
channel item lists for three hours keyed by that hash, so a config save invalidates the cache.

Static media sources only: Jellyfin returns a channel item's saved sources first and then
appends any dynamic ones (`MediaSourceManager.GetPlaybackMediaSources`), so implementing
`IRequiresMediaInfoCallback` as well would show two copies of the stream.

## Delivery modes

The `MediaSourceInfo` is `Protocol=Http`, `IsRemote=true`, `IsInfiniteStream=true`,
`Container=mp3`, `SupportsProbing=false`, with one audio `MediaStream` describing the
format the station reports (bitrate, sample rate, channels).

**Relay (default):** `SupportsDirectPlay=false`, `SupportsDirectStream=false`,
`SupportsTranscoding=true`. The server transcodes with ffmpeg; clients only talk to
Jellyfin. The server-side URL (`StationBaseUrl` + `StreamPath`) is the ffmpeg input.

**Direct:** `SupportsDirectPlay=true` and `Path` is the client-visible URL
(`ClientStreamUrl`, or the server URL when empty). `EncoderPath` / `EncoderProtocol`
carry the server-side URL so ffmpeg still has a usable input if a client falls back to
transcoding. The web client's universal audio endpoint redirects the browser straight to
`Path` in this mode.

In both modes the station URLs are part of the playback info every signed-in user can
fetch. SUB/WAVE streams are public by design; the plugin does not support listener auth.

## Why the media source advertises a runtime

Jellyfin's audio HLS path has no live mode. The web client plays audio through
`/Audio/{id}/universal` with an HLS transcoding profile; for a source with no runtime,
`DynamicHlsHelper` points the master playlist at `live.m3u8`, and that route exists only
under `Videos/`. The result is a 404 and "fatal playback error" before ffmpeg starts.

So the media source carries a nominal runtime (`JellywaveChannel.NominalRuntime`, six
hours). The server then emits a normal segmented playlist and transcodes on demand, which
works for a live input as long as nobody seeks: seeking restarts ffmpeg with `-ss` on a
live stream. The item itself keeps `IsLiveStream`, so lists still show it as live. The
proper fix is an `Audio/{id}/live.m3u8` route upstream; not pursued yet.

## Now playing

`NowPlayingService` is an `IHostedService` registered by the plugin. Every
`NowPlayingPollSeconds` (clamped 5–300) it fetches the station's public
`GET /api/now-playing`. `NowPlayingState` turns the feed into one line, for example
`Now playing: Artist – Title (Album) · DJ Name · Show`, and reports whether it changed.
On change the service finds the live item (`InternalItemsQuery` by channel id and
external id `live`) and writes the line into `Overview` with
`ILibraryManager.UpdateItemAsync(..., ItemUpdateType.MetadataEdit)`. Jellyfin does not raise
the library-changed event for channel items, so nothing is pushed; clients show the new
text on their next list load or playback start. The channel entity is looked up with a
plain item query, not through the channel manager, because the latter creates channel
entities on demand and the poller must not race Jellyfin's startup channel refresh.

Only `Overview` is written. Setting `Artists` on an `Audio` item would make Jellyfin's
artists validator create a Music-library artist for every track the station plays.

Failure handling: one warning when the station becomes unreachable, one info line when it
answers again; the loop never throws; while unreachable or disabled the item falls back to
the default overview. Clients show the text when they reload the list or start playback;
the playing bar does not update mid-track.

## Who can see the channel

`Channel.IsVisible` (Jellyfin) shows a channel to a user when the user has a block list that does
not name it, or, with no block list, when the user has `EnableAllChannels` or the channel in
`EnabledChannels`. Administrators are not exempt. Provisioning tools often create users with none
of these, so the plugin offers "Make the station visible to every user" (off by default).

`AccessGranter.Classify` mirrors that rule exactly and `AddToEnabledChannels` writes the one
channel id into `EnabledChannels`, persisted with `IUserManager.UpdateUserAsync`. Nothing else in
the policy is touched; `EnableAllChannels` is never set; explicit blocks win. Passes run after core
startup (`AccessGrantService`), after a save with the box ticked (`POST /Jellywave/GrantAccess`,
admin only, also behind "Run now"), and on `UserCreatedEventArgs` and `UserUpdatedEventArgs` (the dashboard and provisioning tools
post a policy right after creating a user, and `UpdatePolicyAsync` replaces the enabled-channel list;
the plugin's own `UpdateUserAsync` saves publish no event, and a re-grant on a visible user writes
nothing, so there is no loop). Each user is re-read by id immediately before it is written, because
`GetUsers` hands out untracked snapshots and `UpdateUserAsync` saves the whole entity. Turning
the setting off stops future passes and revokes nothing. One Information line per pass carries
counts, never user names.

## Configuration surface

`PluginConfiguration` holds the station base URL, stream path, delivery mode, optional
client URL, tile name and tagline, stream format (filled by the config page's "Fetch from
station" button through the admin-only `GET /Jellywave/Station?baseUrl=` endpoint),
now-playing toggle and interval, and `LastChangedUtc`, set by the page on save. All URL
handling lives in `StationUrls`, which resolves the stream path under the base URL and
rejects anything that escapes it (different host, `..`).

No environment-specific value lives in source: the base URL is empty until configured,
and the channel publishes no items while it is empty.

## Packaging

`build.yaml` is the single source of plugin metadata; `scripts/build.sh` publishes the
assembly, writes `meta.json` and zips the three artifacts. `manifest.json` is written
only by the release workflow, from the zip it publishes (`UPDATE_MANIFEST=1 scripts/build.sh`
refreshes it locally on purpose). `scripts/check-manifest.sh` asserts the GUID, version and ABI agree
across `Plugin.cs`, the config page, `build.yaml`, `manifest.json`,
`Directory.Build.props` and the staged `meta.json`. The release workflow builds the
tagged commit and commits the manifest to `main`.
