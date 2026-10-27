# Jellywave design

How the plugin works, what it relies on in Jellyfin, and why it is shaped this way.
Written against Jellyfin 12.3; file references are to the Jellyfin server source. Trust boundaries
and accepted risks are collected in [`THREAT-MODEL.md`](THREAT-MODEL.md).

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
scheduled task removes channels whose plugin no longer reports them. See "Channel identity".

The tile image is the embedded `Assets/logo.png`, served through `IChannel.GetChannelImage`.

## Channel identity (card 15)

Jellyfin has no identity for a channel other than its name. `ChannelManager.GetInternalChannelId(name)`
is `ILibraryManager.GetNewItemId("Channel " + name, typeof(Channel))`, and `LibraryManager.GetNewItemId`
lowercases the key unless `EnableCaseSensitiveItemIds` is on (the 12.3 default), then hashes the type's
full name plus the key (MD5). So whether `SUB/WAVE` and `sub/wave` are one channel is a server setting.

`LiveItemFinder.Resolve` computes the same id through the same call and loads it with `GetItemById`;
only a `Channel` counts. There is no name scan any more: the old case-insensitive scan over every
channel entity could return an entity that is not the one Jellyfin serves for the name.
`LibraryChannelLocator` (the granter's lookup) uses the finder, creates a missing entity through
`IChannelManager.GetChannelsInternalAsync` when the station is configured, then resolves by id again.

**Collisions.** `ChannelManager` maps an entity back to its provider with
`Channels.FirstOrDefault(i => GetInternalChannelId(i.Name) == id)`, so two `IChannel`s whose names derive
one id share one entity: it is listed once per provider, keeps the name of whichever created it, and the
first provider serves its items. The finder compares the id of every other registered `IChannel`
(`IEnumerable<IChannel>` from DI, `JellywaveChannel` excluded) with ours. Comparing derived ids, not
names, applies the server's case rule exactly. The list is resolved from `IServiceProvider` on first
use: `JellywaveChannel` itself is one of them. Its constructor needs only a logger and
`NowPlayingState`, so there is no cycle today, but building every plugin's channel while the finder is
constructed would be out of order.

On a collision the resolution carries no entity, so every consumer fails closed:

- The poller writes nothing to the live item (no overview, no image) and sets `PollerHealth`'s collision
  flag. `GET /Jellywave/Health` resolves the channel itself when read, so a rename shows at once, and sets
  `ChannelStatus` = "channel name collides with another plugin's channel", which the configuration page
  shows in the status line (as text).
- The granter grants nothing; a pass returns that sentence as `Error`, and the page shows
  "Nothing done: …".
- `StationAccess` refuses with `NameCollision`, which the filter answers with 404, like a missing channel:
  the station has no channel of its own to serve, and a rename by the admin ends it.
- One Warning per streak names both channels and the id; one Information line when it clears.

Accepted: the id stays name-derived (Jellyfin's API), so a plugin installed later with the same name
starts a collision. The README asks for a unique tile name and says that renaming changes the id, so
grants (and block lists) naming the old id do not apply to the new one.

`scripts/verify-jellyfin.sh` selects the channel by exact name (`JELLYWAVE_CHANNEL`, default
`SUB/WAVE`) and fails when it is missing or listed more than once.

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

In both modes the station URLs are part of the live item's playback info, which Jellyfin's
`Items/{id}/PlaybackInfo` returns to any signed-in user who can see the item (for a channel item that
includes `Channel.IsVisible`) and to any API key; no playback permission is checked. In Direct mode
the client then fetches the URL itself. The plugin's own station routes never return it. SUB/WAVE streams are public by design; the plugin does
not support listener auth.

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
playing bar keeps what it copied at playback start (the station page repaints it in the web
client and the Android app; see "Station page").

Not written, on purpose: `Album`. The playing bar would show it as its first line, but
`Audio.GetUserDataKeys()` prefixes the item's user-data key with `Album`, and
`UserDataManager.SaveUserData` writes one row per key and keeps rows under old keys, so a
value that changes every track would leave an orphan user-data row per track and listener.
`Name` and `AlbumArtists` are in the same key and are left alone for the same reason. The channel
entity is loaded by its derived id (`LiveItemFinder.Resolve`, see "Channel identity"), not through
the channel manager, because the latter creates channel entities on demand and the poller must not
race Jellyfin's startup channel refresh.

**One station at a time (card 17).** Everything cached from the station is kept with its station
origin, `StationUrls.Origin(baseUrl)` (the parsed base URL in full, `baseUrl.AbsoluteUri`), so a
change of the station URL never mixes two stations. `NowPlayingState.Update` takes the origin with
each feed and clears the `/api/state` data (history, up next, the lyrics flag) when it differs from
the last one; `UpdateStationState` stores a state only for the current feed's origin. A failed
`/api/state` fetch therefore keeps the last state of the same station, and none after a switch.
`ScheduleCache` and the applied cover (below) are keyed the same way. The station routes read the
state through `NowPlayingState.Snapshot()` (feed, state, derived lines and their origin under one
lock) and compare its origin with the configured station's: after a URL change, until the poller
has fetched the new station (at most one poll interval), `Feed` answers as if nothing has been
heard (no track, DJ, history, song id or cover tag; the station name and the channel and item ids
still answer) and `Schedule` names no takeover. The live item itself keeps the old station's text
and cover until that poll writes it. The image tag `Feed` returns once the new station answers is the item's
current Primary, so for the few seconds between that feed and the new cover being saved (the state
fetch and the cover download come first) the page can still show the previous cover, the same lag as
an ordinary track change; a failing logo save keeps it longer and is logged once.

Cover art (`LiveItemArtwork`, config `CoverArtEnabled`, default on): Jellyfin applies a
channel item's `ImageUrl` only when it creates the item, so the poller saves the image
itself with `IProviderManager.SaveImage(item, stream, mimeType, Primary, ...)`. That writes a
file under the item's metadata folder, deletes the previous one, and stamps the image's
`DateModified`, which is what the client-facing image tag hashes, so clients refetch. The
station answers 502 with an empty body when a track has no art; then, and when cover art is
off, polling is off, or the station is unreachable, the logo is saved instead so the item
never shows a blank. A cover the station has no art for is remembered by origin and id for
`MissingCoverRetry` (30 minutes): while that holds, the poller uses the logo without asking, until
the track's cover id or the station changes, so a track without art costs one request instead of
one per poll (180 to 720 an hour). Art the station gains for that track later shows within that
time. The applied cover is keyed by origin and id too (`AppliedOrigin` next to `Applied`), so two
stations whose tracks share an id never share art; Health shows the bare id. The clock is a
`TimeProvider`, so both rules are unit-tested. Accepted covers are JPEG, PNG, WebP or GIF of at most 4 MB and at most
4096 px a side, identified from their bytes (see "Outbound requests"); anything else is "no
art". The download happens before the item is looked up,
so the item is held for milliseconds. A transport failure (timeout, a chunked body over the
cap) keeps the text update and is retried on the next poll, and when the item's cover is another
station's (a station switch) the logo is saved meanwhile, so the old art never sits beside the new
station's text; a failure to write the file is
logged once per streak and the text still goes out. Because the item's parent is a `Channel`,
Jellyfin's `UpdateImagesAsync` takes its "live TV" branch and computes neither dimensions
nor a blurhash for the image, so there is no blurred placeholder while it loads. The tile in
the My Media row is the channel's own image and always stays the logo.

`Artists` is never written. Setting it on an `Audio` item would make Jellyfin's artists
validator create a Music-library artist for every track the station plays.

Failure handling: one warning when the station becomes unreachable, one info line when it
answers again; the loop never throws; while unreachable or disabled the item falls back to
the default overview. A feed that parses as JSON `null` counts as a failure (card 17): health
records "the station answered with an empty feed", the state is cleared and the item falls back
the same way, instead of the last track staying up with neither success nor failure recorded. Clients show the text when they reload the list or start playback;
on standard clients the playing bar does not update mid-track (the station page's repaint is the
only exception).

## Who can see the channel

`Channel.IsVisible` (Jellyfin) shows a channel to a user when the user has a block list that does
not name it, or, with no block list, when the user has `EnableAllChannels` or the channel in
`EnabledChannels`. Administrators are not exempt. Server-side user creation turns
`EnableAllChannels` on (the `AddDefaultPermissions` extension in `UserEntityExtensions`, called from
`UserManager.CreateUserInternalAsync`), but jellyfin-web 12.3's Add User page
(`src/apps/dashboard/routes/users/add.tsx`) leaves "Enable access to all channels" unticked and then
posts the policy, so a user added in the dashboard cannot see the station; templates and provisioning
tools often do the same. That per-user Channel Access setting is the only ongoing
authority; the plugin never changes it on its own (card 15b).

For users who cannot see the station, the plugin page has a one-shot button, "Add the station to every
user now", which calls `POST /Jellywave/GrantAccess` (admin only, `RequiresElevation`).
`AccessGranter.GrantAllAsync` resolves the channel through `LibraryChannelLocator`, then walks every
user: `Classify` mirrors `Channel.IsVisible` exactly, and for a user who needs it
`AddToEnabledChannels` adds the one channel id to `EnabledChannels`, persisted with
`IUserManager.UpdateUserAsync`. Nothing else in the policy is touched: `EnableAllChannels` is never
set, no id is ever removed, and a user whose block list names the channel is skipped and counted.
The result carries the counts (granted, already visible, blocked), or an `Error` and no writes when
the configuration is not loaded, the channel entity does not exist yet, or its name collides
(card 15). One Information line per press carries the counts, never user names.

Nothing else writes a policy: no hosted startup pass, no user event consumer, no setting. Users created
later see the station when the admin ticks "Enable access to all channels" or the station on the
dashboard's Add User form, when the provisioning tool's template includes it, or after another press.
An admin who unticks the station on a user's Access page is not overruled in the background, but the
next press re-adds it: an unticked user looks the same as one never granted. Only a `BlockedChannels`
entry survives a press, and jellyfin-web has no screen for it; the page tells the admin to untick
those users again after pressing. Saving a user's Access page in jellyfin-web clears any
`BlockedChannels` entry: `Access.tsx` posts the policy with `BlockedChannels = null`, so a block set
through the API or a provisioning tool is gone after the next dashboard save of that user.

Each user is re-read by id immediately before it is written, because `GetUsers` hands out untracked
snapshots and `UpdateUserAsync` saves the whole entity. Presses are serialized (card 15): one
`SemaphoreSlim` in `AccessGranter` (a DI singleton, disposed with the container) covers the channel
lookup, every user read and every save, so a second press waits and then re-reads every user after the
first has saved; the plugin never saves a stale copy over its own write. `UpdateUserAsync` publishes no
event (`IUserStore.SaveAsync` must stay that way: an event consumer that called back into the granter
under the gate would wait on it forever). Accepted: a dashboard policy save that lands between the
plugin's read and its `UpdateUserAsync` (milliseconds, and only while a press is running) is
overwritten, because `UpdateUserAsync` copies the whole entity and `IUserManager` has no partial
update; the admin sees the old value on reload and saves again.

**History.** 0.2.0.0 to 0.3.4.0 (card 6) had a setting, `GrantAccessToAllUsers`, that re-granted
after core startup (`AccessGrantService`), after every configuration save, and on
`UserCreatedEventArgs` and `UserUpdatedEventArgs` (`UserCreatedConsumer`, `UserUpdatedConsumer`),
because creation flows post a policy right after creating the user and that replaces the enabled
list. The 0.4.0.0 host test showed the cost: Jellyfin's policy save publishes `UserUpdated`, so an
admin who unticked the station for one user saw it re-added within milliseconds, and the startup pass
would re-add it after every restart. A background writer must never undo the policy owner's explicit
choice, so card 15b removed the setting and all three writers; HI traded the always-on shortcut for
that stability. A saved configuration that still holds `<GrantAccessToAllUsers>` loads normally:
`XmlSerializer` ignores the unknown element, and the next save drops it.

## Station page (optional, cards 8s–9)

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
sign-in; ETag and a day of caching keyed on a content hash, the first 12 hex characters of a SHA-256
over the embedded `ui.js` and `ui.css`, used for the `?v=` query too, so a changed script reaches
browsers even while the plugin version stays the same), `GET /Jellywave/Feed` to users
who can see the channel (see "Station route authorization"; the poller's last feed reduced to display fields plus the channel id, the live
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
3. **Requests.** The form posts text and a free-text name (prefilled with the Jellyfin user name;
   the listener can change it; the plugin collapses its whitespace, cuts it at 40 characters and
   otherwise passes it on unchanged) to the
   plugin, which forwards them to the station's `POST /api/request` and maps the receipt (202) or
   the refusal (400, 429 with `retryAfter`, 503) into one outcome object; the page then polls
   `GET /Jellywave/Request/{id}` every three seconds until the station resolves it and shows the
   DJ's acknowledgement, the queued track and its position. The station limits by caller IP, and
   every Jellyfin user is one caller to it; HI chose to honour that and explain it under the box
   rather than add a limiter in the plugin. The server keeps the real user id with each accepted
   request (see "Station route authorization").

Extras (card 8d), each an endpoint on `JellywaveWebController` behind the same access filter and a block in the header:

| Feature | Server side | Page |
|---|---|---|
| DJ portrait | `GET /Jellywave/Avatar/{id}` proxies the station's `/api/persona-avatar/{id}` (id validated, 4 MB cap, one hour private cache); the id comes from the feed's `dj.avatar` path | `<img>` next to the DJ line; the bytes are fetched with the session through `ApiClient.ajax` and shown as a blob URL (revoked on change), because Jellyfin honours `?api_key=` only under `EnableLegacyAuthorization` and an `<img>` cannot send the header |
| Up next, recently played | the poller also fetches `/api/state` each cycle (a failure keeps the same station's last state, see "One station at a time"); `Feed` carries `UpNext` and up to 8 `Recent` lines ("· requested by X"), skipping null entries | one line and a collapsible list |
| Like | `GET`/`POST /Jellywave/Like` map the station's `/api/like` (403 off, 409 track ended, 429 too fast) into `LikeOutcome` | heart with the count, per track |
| Lyrics | `GET /Jellywave/Lyrics?songId=` maps `/api/lyrics/current` (403 off, 401 private station, 409 ended, 503 music server) into `ok/none/pending/off/unavailable/ended` (null lines skipped); shown only when `state.lyrics.enabled` | collapsible panel, one fetch per track while open, plain lines (no timing-based highlight: the stream's buffering makes a client-side clock unreliable) |
| Today's programme | `GET /Jellywave/Schedule`: `ScheduleCache` holds `/api/schedule` for five minutes with its station origin (stale copy of the same station on failure, retried after one minute; a new station URL drops the copy first, so a failing new station shows no schedule rather than the old one, and without a copy a failure is not retried for one minute either) and `BuildToday` turns the Sunday-first weekly grid into today's blocks in the station's time zone with the current one marked (unit-tested, falls back to UTC on an unknown zone; a repeated show or persona id keeps the first, null entries are skipped) | collapsible panel with hour ranges, show and host; refetched every five minutes while open so the current block moves |

Layout (card 9, HI's option A, "fill the width"): the header is a CSS grid with four named areas,
`cover | body | request` on the first row and `panels` spanning the second, and each direct child
(cover, body, the request form, the panels block) is pinned to its area, so the markup order does not
matter. The columns are `11em`, `minmax(0, 1fr)` and `minmax(16em, 24em)`; the panels block is its
own `repeat(auto-fit, minmax(16em, 1fr))` grid, so Recently played, Today on the station and Lyrics
share a row when there is room. Two breakpoints only re-map the areas: at 60em the request box
drops under cover and body (`"cover body" "request request" "panels panels"`), at 40em everything
stacks and the cover becomes a full-width square. Inside the form the text field spans both of its
columns and the name and Send sit on one row (`minmax(0, 1fr) auto`).

Known limits: the DOM selectors (`.itemsContainer[data-parentid]`, `.nowPlayingBar`, `.card`) are
jellyfin-web internals and can move between releases; the page degrades to "nothing happens"
rather than breaking the client. The Android app wraps the web client and needs a force-close to
refetch `index.html` after the plugin is first installed.

## Station route authorization (card 13)

Jellyfin's default policy (`DefaultAuthorizationHandler`, 12.3) lets any API key through unrestricted and
never looks at channel visibility, so plain `[Authorize]` is not enough for the station routes. Every
user route on `JellywaveWebController` (Feed, Avatar, Like GET and POST, Schedule, Lyrics, Request POST,
Request/{id}) also carries `[StationAccess]`, an `IAuthorizationFilter` attribute. It runs after
Jellyfin's authentication and before model binding, so a refused caller's body is never read, and asks
the `StationAccess` singleton, which refuses in this order:

1. `Jellyfin-IsApiKey` is anything but `False` (Jellyfin sets it with `bool.ToString` on every request).
2. `Jellyfin-UserId` is missing, unreadable or empty (API keys carry an empty id), or names no user
   (`IUserStore.GetUser`).
3. The channel name collides with another plugin's channel (`NameCollision`, see "Channel identity"), or
   the channel entity is missing (`LiveItemFinder.Resolve`; a fresh install, or a rename before the
   channel refresh): fail closed with **404** instead of 403, so the page keeps its short retry.
4. Jellyfin's own `Channel.IsVisible(user)` is false: block list, enabled list or `EnableAllChannels`,
   then the parental check (tags and rating). Administrators are not exempt, same as in Jellyfin.

Every other refusal is a plain 403 with no `X-Application-Error-Code` (jellyfin-web logs a user out on a
403 that carries `ParentalControl`); each refusal writes one Debug line with the rule. An allowed caller's
user id and the resolved `Channel` are left in `HttpContext.Items` for the action, so `Feed` does not
look the channel up a second time. The user entity is not passed on. The assets stay anonymous and `WebUi` stays admin-only; a reflection
test fails when an action is neither anonymous, admin-only nor filtered.

Listener requests: the station sees this server as one caller, so the plugin keeps the user binding
itself. `RequestReceipts` (a singleton, in memory) maps each accepted request id to the submitting
user, for 30 minutes and at most 1000 entries (the oldest goes), pruned on every insert, under a lock.
`GET /Jellywave/Request/{id}` answers 403 when the receipt belongs to someone else, and "unknown"
("The station lost track of that request.") without calling the station when there is no live
receipt. Receipts do not survive a restart: a request pending across one shows that same message. The
free-text name still goes to the station unchanged (HI decision); accounting is the receipt plus the
existing Information line with the request id and user id, never the name or the text.

The cap is shared: one user who submits many requests can fill it and evict other users' receipts,
whose polls then show "lost track". The impact is low, because the station's own rate limit (a few
requests per hour for the whole server) keeps the map far below 1000 in practice. A 403 versus
"unknown" does reveal whether a request id exists here; accepted, since the ids are station-generated.

The page: a 403 from `Jellywave/Feed` (ApiClient rejects with the fetch `Response`, so the script reads
its `status`) drops the feed, the header and the drawer link, and the next feed request waits
`FEED_FORBIDDEN_RETRY_MS` (five minutes) instead of the usual 15 seconds; a 404 (no channel yet) keeps
the 15 seconds. The other routes are only called once a feed exists; a 403 on a request submit or poll
says "You don't have access to this station." instead of a connection error.

## Outbound requests (card 14)

Every request the plugin itself makes to the station (feed, state, schedule, likes, requests,
lyrics, images) goes through `StationClient`, and every `StationClient` call goes through one
private `CreateClient`, which asks for the plugin's own named client `Jellywave` and
sets the 8 s budget and the body cap (256 KB for JSON, 4 MB for images).

**Own client, redirects refused.** Jellyfin's `NamedClient.Default` keeps `SocketsHttpHandler`'s
default `AllowAutoRedirect = true`, so a station answer could send the server to any host or port
(SSRF). `PluginServiceRegistrator` registers `Jellywave` with `AllowAutoRedirect = false`,
`AutomaticDecompression = All` and the User-Agent `Jellywave/<assembly version>`. `AddHttpClient`
comes from the ASP.NET Core shared framework the server already loads, so no package is added and
the zip stays the DLL, `meta.json` and `thumb.png`. Jellyfin's own clients also set its dual-stack
`ConnectCallback` (`HttpClientExtension.OnConnect`, which honours the server's IPv6 setting); it lives
in a server assembly the plugin does not reference, so the plugin uses the default connect. The
station is a LAN host; a name that resolves to an unreachable IPv6 address first costs part of the
8 s budget before IPv4 is tried. A 3xx is the station's answer, not a hop, for those requests. The stream is not one of them: in
Relay mode Jellyfin's ffmpeg fetches it and in Direct mode the client does, so neither goes through
this client, and ffmpeg's http input probably follows a redirect (untested). That is an accepted risk
(see `THREAT-MODEL.md`).

For the plugin's own requests:

- JSON reads (now playing, state, schedule) throw `StationRedirectException`, an
  `HttpRequestException` with the status and the resolved `Location`, so every existing failure path
  handles it. Its message is generic and never carries the location, because it reaches the poller
  status line.
- Images are "no art" (null), like any other non-success.
- Like, lyrics and the request routes get the status without the body (a redirect's body is not
  the station's answer). Like shows a refusal ("The station did not take the like."), lyrics are
  "unavailable", a request submit is "refused" with no receipt recorded, and a request poll is
  "unavailable" with the receipt kept.
- The admin probe (`GET /Jellywave/Station`) answers `Ok = false` with "the station redirected to
  <base>; use that address instead". The base is the `Location` with the `api/…` path the probe
  asked for, and any query or fragment, cut off. It is suggested only when it passes the base URL
  rules below (http or https, no userinfo); otherwise the probe says the station redirected "to an
  address the plugin does not accept" and shows none of it. Only this admin-only route shows it.

**URL rules** (`StationUrls`, pure functions; every caller treats a rejected value as "not
configured", exactly as before, but a refused non-empty base URL says why, see below):

- Base URL: absolute http or https, with no userinfo (not even an empty `@`), no query and no
  fragment. The value is checked as typed, before the trailing slash is added (otherwise
  `http://host/radio?x=1` would turn into a query of `?x=1/`). The probe answers 400 for such a value,
  and the configuration page says why.
- A refused non-empty base URL (an install saved before card 14 as `http://host:7700/?`, or with
  `user:pass@`) is not shown as "not configured". `StationUrls.BaseUrlRefusal` returns a fixed reason
  (not an absolute URL, not http(s), credentials, query, fragment) that never repeats any part of the
  value. `GET /Jellywave/Health` carries it as `StationUrlRefusal` and `StationUrlStatus` ("station URL
  is set but not allowed: <reason>"). The configuration page shows it in the poller status line and
  as a warning under the field, on load and after each save; the save itself is not blocked. The
  channel's listing warning names the reason too.
- Stream path: leading slashes are trimmed, so `//x` stays a path under the base. A path that still
  parses as an absolute URI (it has a scheme) is refused. Then the built URL must equal the base on
  scheme, host, effective port and userinfo (`Uri.Compare(..., SchemeAndServer | UserInfo, ...)`)
  and stay under the base's path, with no `..` segment. The authority check is load-bearing, not a
  backstop: on Linux `\/radio.example:8000/stream.mp3` is not an absolute URI (a `file:` host cannot
  carry a port), yet `Uri` resolves it against the base as a network path to the same host on
  another port. Only the `Uri.Compare` step refuses it, and a host-only compare would let it through
  (a test fails when the compare is reverted). A query or fragment in the stream path stays allowed.
- Client stream URL (Direct mode): absolute http or https without userinfo. Any host and port stays
  allowed, because Direct mode hands this URL to clients on purpose. A rejected value logs the
  existing warning, and Direct mode falls back to the server URL.
- Plain HTTP stays allowed: the station normally runs on plain HTTP on the LAN.

**Image checks** (`ImageSniffer`, covers and avatars). The declared content type must be missing
or start with `image/`; it no longer decides the type, and a missing type is no longer taken as
JPEG. The type comes from the magic bytes (JPEG `FF D8 FF`, PNG's 8-byte signature, `GIF87a` and
`GIF89a`, `RIFF`…`WEBP`). The size comes from the header: PNG IHDR, the GIF logical screen
descriptor, WebP VP8 (lossy keyframe), VP8L and VP8X, and for JPEG a bounds-checked marker walk to
the first SOF0–SOF15 frame header (DHT `C4`, JPG `C8` and DAC `CC` are skipped). An unknown type, a
side of 0, a side over `MaxImageDimension` (4096), or an area over `MaxImagePixels` (16 777 216) is
"no art". With the side limit the area limit cannot trigger on its own; it is kept as a named
bound in case the side limit is ever raised. Every read is bounds-checked, and malformed or
truncated input is "unknown", never an exception. The sniffed type is what Jellyfin saves and what
the avatar route serves.

Accepted risk: animation is not checked. A GIF, an animated PNG (APNG, whose frames sit in `acTL`/
`fcTL` chunks after IHDR) or an animated WebP (the VP8X animation flag) within 4096 × 4096 and 4 MB
can still carry many frames. Only the first frame's header is sized. Jellyfin's image processor
decodes it, and the station is admin-configured.

## Poller health (card 10)

`PollerHealth` is a singleton snapshot the poller writes (running, reachable, last success, last
error with time) and `GET /Jellywave/Health` (admin) reads, together with the live item's current
description and image key and whether the item exists. The route resolves a channel-name collision
itself when read (card 15); the poller records no collision flag (card 17 removed the unread one).
The configuration page shows it under
"Now playing" with a "Refresh status" button, so an admin never has to read the server log to know
whether the station is being reached.

## Configuration surface

`PluginConfiguration` holds the station base URL, stream path, delivery mode, optional
client URL, tile name and tagline, stream format (filled by the config page's "Fetch from
station" button through the admin-only `GET /Jellywave/Station?baseUrl=` endpoint),
now-playing toggle and interval, the cover-art toggle, and
`LastChangedUtc`, set by the page on save. All URL
handling lives in `StationUrls`, which resolves the stream path under the base URL and
rejects anything that escapes it (another scheme, host, port or userinfo, an absolute path, `..`);
see "Outbound requests".

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

`targetAbi` (12.3.0.0) is the oldest server Jellyfin offers a build to, and the runtime binds an
assembly reference only to a loaded assembly of the same or a newer version. A build compiled
against the 12.3.0 packages references 12.3.0.0 assemblies, which a 12.0–12.2 server cannot load, so
`targetAbi` must name the packages the plugin is compiled against. Two checks keep them together:
`check-manifest.sh` compares the `Jellyfin.Controller` and `Jellyfin.Model` versions in both csproj
files with `targetAbi`, and `AbiTests` fails when any `Jellyfin.*` or `MediaBrowser.*` reference in
the built assembly is newer than `targetAbi`. Releases up to v0.3.4.0 claimed 12.0.0.0 while
referencing 12.3.0.0; their manifest entries are left as published.

The release workflow has two jobs. `build` (token `contents: read`, checkout without stored
credentials) restores packages, runs the analyzers, tests, `build.sh` and `check-manifest.sh`, and
uploads the zip and `meta.json` as an artifact. `publish` (`contents: write`) downloads that
artifact, creates or updates the GitHub release and writes the manifest entry, taking changelog,
timestamp and `targetAbi` from the zip's own `meta.json`. While the write token is present only
`gh`, `jq` and main's `scripts/update-manifest.sh` run: no package restore, analyzer or build. The
job then refuses to commit an entry whose `targetAbi` differs from the zip's (main's script predating
the release), so tag only after the release's branch is merged. `ci.yml` runs build, tests, `dotnet format --verify-no-changes`,
`node --check`, `bash -n` and `check-manifest.sh` on every push and pull request with a read-only
token. Every action is pinned to a full commit SHA; `.github/dependabot.yml` proposes updates.
