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
| `ImageUrl` | unset | The poller owns the item's Primary image (see Now playing); Jellyfin only applies `ImageUrl` when it first creates the item |

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
`Now playing: Artist – Title (Album) · DJ Name · Show · Up next: Show with Presenter at time`
(the "Up next" part comes from `context.showHandover`, which the station sends near the end
of a show), a track line `Artist – Title`, and the cover id (`nowPlaying.subsonic_id`,
accepted only when it is 1–64 characters of letters, digits, `_` and `-`). `Update` reports
which of those changed. On change the service finds the live item (`InternalItemsQuery` by
channel id and external id `live`) and writes:

| Item field | Value | Why |
|---|---|---|
| `Overview` | the description line | Shown on the item's detail page and in list descriptions |
| Primary image | the track's cover (`GET /api/cover/{id}`) or the embedded logo | See below |

Both go through one `ILibraryManager.UpdateItemAsync(..., MetadataEdit | ImageUpdate)`, and the
poller reconciles the item on every successful poll (one cheap query, a write only when a
field differs), so a re-save by Jellyfin's own channel refresh, which can overwrite the row
from an older in-memory copy, is repaired on the next poll rather than the next track.
Jellyfin does not raise the library-changed event for channel items, so nothing is pushed;
clients show the new text and art on their next list load or playback start, and the
playing bar keeps what it copied at playback start.

Not written, on purpose: `Album`. The playing bar would show it as its first line, but
`Audio.GetUserDataKeys()` prefixes the item's user-data key with `Album`, and
`UserDataManager.SaveUserData` writes one row per key and keeps rows under old keys, so a
value that changes every track would leave an orphan user-data row per track and listener.
`Name` and `AlbumArtists` are in the same key and are left alone for the same reason. The channel entity is looked up with a
plain item query, not through the channel manager, because the latter creates channel
entities on demand and the poller must not race Jellyfin's startup channel refresh.

Cover art (`LiveItemArtwork`, config `CoverArtEnabled`, default on): Jellyfin applies a
channel item's `ImageUrl` only when it creates the item, so the poller saves the image
itself with `IProviderManager.SaveImage(item, stream, mimeType, Primary, ...)`. That writes a
file under the item's metadata folder, deletes the previous one, and stamps the image's
`DateModified`, which is what the client-facing image tag hashes, so clients refetch. The
station answers 502 with an empty body when a track has no art; then, and when cover art is
off, polling is off, or the station is unreachable, the logo is saved instead so the item
never shows a blank. Accepted covers are JPEG, PNG, WebP or GIF (a missing content type
counts as JPEG, the station's default) of at most 4 MB, judged from the headers before the
body is read; anything else is "no art". The download happens before the item is looked up,
so the item is held for milliseconds. A transport failure (timeout, a chunked body over the
cap) keeps the text update and is retried on the next poll; a failure to write the file is
logged once per streak and the text still goes out. Because the item's parent is a `Channel`,
Jellyfin's `UpdateImagesAsync` takes its "live TV" branch and computes neither dimensions
nor a blurhash for the image, so there is no blurred placeholder while it loads. The tile in
the My Media row is the channel's own image and always stays the logo.

`Artists` is never written. Setting it on an `Audio` item would make Jellyfin's artists
validator create a Music-library artist for every track the station plays.

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

## Station page (optional, cards 8s–8c)

jellyfin-web has no hook for a plugin page that non-admin users can reach: plugin pages, the
`configurationpage` route and `EnableInMainMenu` all live behind the admin dashboard, the
"custom menu links" setting opens external tabs, and CSS cannot navigate. The only maintained way
in is the third-party File Transformation plugin, which rewrites web files as Jellyfin serves them.
Jellywave treats it as an optional extra: `WebUiStartupTask` (an `IScheduledTask` on the startup
trigger, like SeerrFin's) looks for an assembly named `*.FileTransformation`, and when it is absent
logs one Information line and records `WebUiState.NotInstalled` for the configuration page.

Registration goes through reflection, because plugins cannot reference each other. The other
plugin's `PluginInterface.RegisterTransformation` takes a Newtonsoft `JObject` from its own load
context; the task takes that parameter type from the method signature and calls its static
`Parse(string)` on a JSON string, so Jellywave carries no Newtonsoft reference and nothing crosses
load contexts. Two transformations are registered with the plain file names `index.html` and
`config.json`: File Transformation keeps one pipeline per pattern string and runs only the first
pipeline whose pattern matches a request, so an anchored regex for the same file would never run
once another plugin registered the plain name (SeerrFin does). Callbacks are static methods on
`WebUiTransforms`; the other plugin converts `{"contents": ...}` into `TransformPayload` by
property name and takes the returned string.

- `index.html`: a stylesheet link and a **synchronous** `<script>` at the end of `head`, with
  relative URLs (`../Jellywave/ui.js`) so base-URL and reverse-proxy setups keep working. The
  bundles are `defer`, so the script defines `window.JellywaveUi` before the plugin loader runs.
- `config.json`: `"JellywaveUi"` added to the `plugins` array. jellyfin-web's plugin manager loads a
  string that names a window global as a client plugin and instantiates it with its own
  `playbackManager`, `appRouter`, `events` and connections, which is the only sanctioned way for
  injected code to drive Jellyfin's player.

`JellywaveWebController` serves the two assets anonymously (they are part of the page load, before
sign-in; ETag and a day of caching keyed on the assembly version), `GET /Jellywave/Feed` to any
signed-in user (the poller's last feed reduced to display fields plus the channel id, the live
item id and its image tag; never the station URL), `POST /Jellywave/Request` and
`GET /Jellywave/Request/{id}` for listener requests, and `GET /Jellywave/WebUi` (admin) for the
status line on the configuration page.

The script (`Web/ui.js`) does three things:

1. **The page.** The default (modern) layout has no user drawer, so the station page is the channel's
   own library page, one click from the tile or the top bar. Both layouts render a channel through
   the legacy list controller, whose `.itemsContainer` carries `data-parentid`; when that equals the
   channel id, a header is inserted above the toolbar row: cover (the live item's own Primary image,
   by tag), track, DJ with tagline, show, what's next, listener count, Play/Stop through
   `playbackManager.play({ids:[liveItemId]})` and `stop()`, and the request form. In the legacy
   layout a drawer link named after the station opens the same page via `appRouter.showItem`.
2. **Repainting while the station plays.** Every client copies the playing item at playback start
   and never re-reads it (`playbackmanager.js` `getNowPlayingItemForReporting`), so while the live
   item is the current item the script repaints the bar's thumbnail and text, the item's card in any
   visible grid, and the browser `MediaSession` metadata (lock-screen art) every five seconds from
   the feed. `playbackManager.currentItem(player)` throws when no player exists, so the script asks
   `getCurrentPlayer()` first and guards each step.
3. **Requests.** The form posts text and a name (prefilled with the Jellyfin user name) to the
   plugin, which forwards them to the station's `POST /api/request` and maps the receipt (202) or
   the refusal (400, 429 with `retryAfter`, 503) into one outcome object; the page then polls
   `GET /Jellywave/Request/{id}` every three seconds until the station resolves it and shows the
   DJ's acknowledgement, the queued track and its position. The station limits by caller IP, and
   every Jellyfin user is one caller to it; HI chose to honour that and explain it under the box
   rather than add a limiter in the plugin.

Extras (card 8d), each a signed-in endpoint on `JellywaveWebController` and a block in the header:

| Feature | Server side | Page |
|---|---|---|
| DJ portrait | `GET /Jellywave/Avatar/{id}` proxies the station's `/api/persona-avatar/{id}` (id validated, 4 MB cap, one hour private cache); the id comes from the feed's `dj.avatar` path | `<img>` next to the DJ line, loaded with the session token as `api_key` |
| Up next, recently played | the poller also fetches `/api/state` each cycle; `Feed` carries `UpNext` and up to 8 `Recent` lines ("· requested by X") | one line and a collapsible list |
| Like | `GET`/`POST /Jellywave/Like` map the station's `/api/like` (403 off, 409 track ended, 429 too fast) into `LikeOutcome` | heart with the count, per track |
| Lyrics | `GET /Jellywave/Lyrics?songId=` maps `/api/lyrics/current` (403 off, 401 private station, 409 ended, 503 music server) into `ok/none/pending/off/unavailable/ended`; shown only when `state.lyrics.enabled` | collapsible panel, one fetch per track while open, plain lines (no timing-based highlight: the stream's buffering makes a client-side clock unreliable) |
| Today's programme | `GET /Jellywave/Schedule`: `ScheduleCache` holds `/api/schedule` for five minutes (stale on failure) and `BuildToday` turns the Sunday-first weekly grid into today's blocks in the station's time zone with the current one marked (unit-tested, falls back to UTC on an unknown zone) | collapsible panel with hour ranges, show and host |

Known limits: the DOM selectors (`.itemsContainer[data-parentid]`, `.nowPlayingBar`, `.card`) are
jellyfin-web internals and can move between releases; the page degrades to "nothing happens"
rather than breaking the client. The Android app wraps the web client and needs a force-close to
refetch `index.html` after the plugin is first installed.

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
