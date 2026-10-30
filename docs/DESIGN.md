# Jellywave design

How the plugin works, what it relies on in Jellyfin, and why it is shaped this way.
Written against Jellyfin 12.3; 0.5.0.0 targets 12.4, which changed only the .NET runtime (its release
notes list only ".NET 10.0.14", and jellyfin-web 12.4 changes only `package.json` and `package-lock.json`),
so what is said of 12.3 here holds for 12.4. File references and line numbers are to the 12.3 Jellyfin
server source; facts read on the test host name the 12.3 build they were read from. Trust boundaries
and accepted risks are collected in [`THREAT-MODEL.md`](THREAT-MODEL.md).

## The tile

Jellyfin builds the "My Media" row from `UserViewManager.GetUserViews`, which appends
every registered `IChannel` as its own view right before the Live TV folder. Jellywave
registers one `IChannel` (`JellywaveChannel`), so the tile appears beside the
libraries on every client without any client-side change. From 0.5.0.0 the tile is named by
`LibraryName` ("Live Radio" by default) and holds one item per enabled station (card L2; see "The station
items").

Jellyfin 12 does not discover `IChannel` implementations by reflection. `ChannelManager`
receives `IEnumerable<IChannel>` from dependency injection, so the plugin registers the
channel in `PluginServiceRegistrator` (`AddSingleton<IChannel, JellywaveChannel>()`).
Without that line the plugin loads but no tile appears.

The channel's internal id is derived from its name (`ChannelManager.GetInternalChannelId`),
which is why renaming the tile creates a new channel; Jellyfin's "Refresh Channels" scheduled task
(category "Internet Channels", key `RefreshInternetChannels`; by default it runs every 24 hours,
`RefreshChannelsScheduledTask.GetDefaultTriggers`) removes channels whose plugin no longer reports them. See "Channel identity" and its
rename bridge.

The tile image is the embedded `Assets/logo.png`, served through `IChannel.GetChannelImage`.

## Channel identity (card 15)

Jellyfin has no identity for a channel other than its name. `ChannelManager.GetInternalChannelId(name)`
is `ILibraryManager.GetNewItemId("Channel " + name, typeof(Channel))`, and `LibraryManager.GetNewItemId`
lowercases the key unless `EnableCaseSensitiveItemIds` is on (the default in 12.3 and 12.4), then hashes the type's
full name plus the key (MD5). So whether `SUB/WAVE` and `sub/wave` are one channel is a server setting.

`LiveItemFinder.Resolve` computes the same id for the tile name (`LibraryName`, blank meaning "Live Radio";
`ChannelIds.Derive` is the one place that builds the key) and loads it with `GetItemById`;
only a `Channel` counts. There is no name scan any more: the old case-insensitive scan over every
channel entity could return an entity that is not the one Jellyfin serves for the name.
`LibraryChannelLocator` (the granter's lookup) uses the finder, creates a missing entity through
`IChannelManager.GetChannelsInternalAsync` when at least one station is listed (`StationSources.Publish`, plain streams
included; card L7), then resolves by id again. With no listed station it creates nothing and the buttons say "the
channel does not exist yet (add a station and save)".

**Collisions.** `ChannelManager` maps an entity back to its provider with
`Channels.FirstOrDefault(i => GetInternalChannelId(i.Name) == id)`, so two `IChannel`s whose names derive
one id share one entity: it is listed once per provider, keeps the name of whichever created it, and the
first provider serves its items. The finder compares the id of every other registered `IChannel`
(`IEnumerable<IChannel>` from DI, `JellywaveChannel` excluded) with ours. Comparing derived ids, not
names, applies the server's case rule exactly. The list is resolved from `IServiceProvider` on first
use, not in the constructor: `JellywaveChannel` itself is one of them, and it takes `RenameBridge`, which takes the
finder, so resolving the list while the finder is constructed would be a cycle (and would build every plugin's channel
out of order).

On a collision the resolution carries no entity, so every consumer fails closed:

- The poller writes nothing to any item (no overview, no image; it still fetches the stations, so their health
  stays current). `GET /Jellywave/Health` resolves the channel itself when read, so a rename shows at once, and sets
  `ChannelStatus` = "channel name collides with another plugin's channel", which the configuration page
  shows in the status line (as text).
- The granter grants nothing; a pass returns that sentence as `Error`, and the page shows
  "Nothing done: …".
- `StationAccess` refuses with `NameCollision`, which the filter answers with 404, like a missing channel:
  the stations have no channel of their own to serve, and a rename by the admin ends it.
- One Warning per streak names both channels and the id; one Information line when it clears.

Accepted: the id stays name-derived (Jellyfin's API), so a plugin installed later with the same name
starts a collision. A generic name like "Live Radio" makes that likelier; the README asks for a unique
tile name.

### Rename bridge (card L1)

Renaming the tile gives it a new id. Users who had the old id in `EnabledChannels` stop seeing it, and,
worse, users whose `BlockedChannels` named the old id start seeing it: with a block list present
`Channel.IsVisible` only checks that list, and it does not name the new id. The 0.5.0.0 upgrade renames
every install ("SUB/WAVE" or the admin's name becomes "Live Radio"), so the plugin bridges renames:

- **Earlier names are recorded, server side.** `PluginConfiguration.PreviousChannelNames` holds names, not
  ids; the ids are derived when used, through `GetNewItemId`, so they follow `EnableCaseSensitiveItemIds`
  if the admin changes it later. The migration records the 0.4.x tile name (also when no URL was set:
  0.4.x listed an empty tile too). `Plugin.UpdateConfiguration` (12.3: `PluginsController.UpdatePluginConfiguration`
  deserializes the page's POST and calls it) runs `ConfigMigration.PrepareSave` before the base class
  stores and saves: the outgoing tile name is added when the name changes, and every name the current
  configuration lists is kept, so a page or API client that drops the list cannot lift old blocks. A
  name is not added when it derives the current id or one already listed. The plugin never removes a
  name while it is an earlier name (a rename back to it makes it the current tile, which the bridge then
  skips). `CarriedChannelNames` (see "Who can see the channel") is kept on every save the same way.
- **Old blocks are honoured, read-only.** `RenameBridge.PreviousIds` derives the ids (the current one
  excluded). `JellywaveChannel.IsEnabledFor(userId)` returns false when the user's block list names one
  of them; Jellyfin 12.4 asks it after `IsVisible` for every channel listing
  (`ChannelManager.GetChannelsInternalAsync`, lines 221–237), and My Media is built from that list, so
  the tile is hidden. It parses the id strictly as "N" (what Jellyfin passes), reads the user through
  `IUserReader` only when earlier names exist, and fails closed (false) on a bad id, a missing user or any
  exception. `IUserReader` is `UserManagerReader`, which wraps `IUserManager.GetUserById` and is not an
  `IUserStore`, so the channel holds nothing that can save a user. After a failed *migration* the
  configuration runs as loaded (schema 0) and counts its 0.4.x tile name as earlier, so old blocks hold, and
  the next page save records that name for good (`CaptureRename`). After a failed *load* the plugin runs on
  defaults and cannot know the real old name: the empty tile shows to users blocked on it (no station, so
  no content and no station data), and the configuration page warns (`Health.ConfigurationWarning`) until a
  save writes a new file (the unreadable one is kept as a copy first, card R1). `StationAccess` refuses the same users right after the user lookup
  (`PreviousChannelBlocked`, 403). Nothing is written.
- **Not covered by the read-only half:** item lists come from the library database, and Jellyfin's channel
  items route (`GetChannelItems`) checks neither `IsVisible` nor `IsEnabledFor`. The channel id is derived
  from the tile name, which anyone can read, so an old-blocked user can compute it and list the new tile's
  items, and until the carry-over copies the block also get their PlaybackInfo. The carry-over closes
  PlaybackInfo, not the channel items or stream routes: tile access hides, it does not lock, for any user and
  any media (see "Who can see the channel").

`scripts/verify-jellyfin.sh` selects the channel by exact name (`JELLYWAVE_CHANNEL`, else `LibraryName`
from the plugin configuration, else "Live Radio") and fails when it is missing or listed more than once.

## The station items (card L2)

`GetChannelItems` returns one `ChannelItemInfo` per enabled station that passes its rules
(`StationSources.Publish`), in grid order:

| Field | Value | Why |
|---|---|---|
| `Id` | the station's `Id`: a random 32-hex-digit GUID for every station (card R2a: the page, the starter seed and the 0.4.x migration all make random ones; see "Public station ids re-keyed") | Stable external id, so Jellyfin keeps one `Audio` entity per station across refreshes. Not guessable, because Jellyfin derives the item id from it and the tile name, and streams an item without sign-in |
| `Name` | "<station name> — Live" (`JellywaveChannel.ItemName`); a blank name is the stream URL's host in ASCII form for a plain stream (card L11: `Uri.IdnHost`, an IPv6 literal with its brackets, as the page's `URL.hostname`; "Unnamed stream" without an http(s) host) and "SUB/WAVE" for a SUB/WAVE station | Jellyfin sets it only when it creates the item (`ChannelManager.GetChannelItemEntityAsync`, `ChannelManager.cs:1027-1029`), so the poller writes it after a rename (card L3, see "Station rename"); the same rule names the station on the page, in Health, in the station list route and in the log. Before L11 every blank name was "SUB/WAVE", so a blank plain stream's item is renamed once on the first poll after the upgrade |
| `MediaType` / `ContentType` | Audio / Song | Becomes an `Audio` item, played by the audio player on every client |
| `IsLiveStream` | true | Jellyfin nulls the item's runtime and tags it `livestream`; lists show "Live" |
| `Overview` | a SUB/WAVE station's own now-playing line (card L4), the default text for a station not heard yet; a plain stream's notes (card L8, `JellywaveChannel.StreamOverview`: one line, at most 280 characters, `<` and `>` dropped because jellyfin-web renders an overview as sanitized HTML and Markdown), else the default text | Applied only when the item is created (`ChannelManager.cs:1027-1035`); after that the poller keeps every item's overview: each SUB/WAVE station's own line (the default text after a failure), and each plain stream's notes or the default text (so edited notes reach the item, and an id that was a SUB/WAVE station does not keep its last line) |
| `DateModified` | max(config `LastChangedUtc`, `MediaSourceSchemaDate`) | Jellyfin only re-saves a channel item's media sources when this advances (`ChannelManager.GetChannelItemEntityAsync`) |
| `MediaSources` | one static `MediaSourceInfo` per station | See "Media source: Relay only" |
| `ImageUrl` | never set | Jellyfin fetches it with its own client, outside the card 14 controls, and applies it only to an item without a Primary image (`ChannelManager.cs:1133-1136`). The plugin saves every item's image itself (see "Now playing" and "Station logos") |

**Which stations are listed.** Enabled stations, ordered by `SortOrder` with list order breaking ties.
A station is left out, with one Warning per streak that names the station (never its URL) and the fixed
reason, when: its id is not 1–64 letters, digits, `_` or `-` (`StationClient.IsValidId`; the id becomes an
external id and, from L5, part of station routes); an earlier station has the same id ignoring case; its
kind is unknown; a SUB/WAVE station's base URL and stream path fail the card 14 rules (a refused base URL
says why, as on the configuration page); a plain stream's `StreamUrl` fails the whole-URL rule, or its path (not
its query) ends in `.pls` or `.m3u` (L0: ffmpeg cannot open playlist files; the L3 probe resolves them) or in `.mpd`
(a DASH manifest, not served), ignoring case; its codec is not
`mp3` or `aac`; its container is not `mp3`, `aac` or `hls` (`m3u8` is read as `hls`). A blank codec is
`mp3` and a blank container follows the codec (or is `hls` for a `.m3u8` address); bitrate, sample rate and
channels fall back to 192 kbit/s, 44.1 kHz and 2. The other stations are listed regardless. With no
enabled station, one Warning per streak says so and the tile is empty; when stations are enabled but every one is
refused, a different Warning says that instead.

**Order in the grid.** Jellyfin does not let a channel order its items. `InternalChannelFeatures.DefaultSortFields`
is only reported to clients (`ChannelManager.cs:594`) and the provider is always asked with no sort field
(`ChannelManager.cs:721-728`, `862`); the list itself comes from the library database
(`ChannelManager.GetChannelItemsInternal`, `ChannelManager.cs:778`; `Channel.GetItemsInternal`, `Channel.cs:48-62`)
in the order the client asks for. jellyfin-web 12.4 asks `GET /Items?ParentId=<channel>` with
`SortBy=IsFolder,SortName` by default (`src/apps/legacy/controllers/list.js` 327-345, 1000-1008, 1090), and with no
order the repository sorts by `SortName` (`BaseItemRepository.QueryBuilding.cs:372-375`). `ChannelItemInfo` has no
sort field, and `IndexNumber` (which `Audio.CreateSortName` would prefix, `Audio.cs:94-98`) is applied only when the
item is created and is part of the item's user-data key (`Audio.cs:100-127`). So the poller writes the item's
`ForcedSortName` instead: "<rank, four digits> <station name>", where the rank is the station's place among the
listed items. `SortName` is then derived from it (`BaseItem.cs:556-570`) and stored (`BaseItemMapper.cs:285-286`);
it is not in the user-data key, so a reorder orphans nothing. The poller reconciles it on every poll, like the
overview, so a re-save by Jellyfin's own refresh is repaired within one poll. Until the first poll after an item is
created, and whenever a client picks another order (date added, random), the grid does not follow `SortOrder`. An
admin who opens the metadata editor sees the rank as the item's sort name.

`DataVersion` hashes the tile name, the item date and every field of every enabled station in grid order (a JSON
array, so field boundaries cannot shift). Jellyfin caches channel item lists for three hours keyed by that hash
(`ChannelManager.GetChannelDataCachePath`), so any change to a station, an added, removed, enabled or reordered one,
or a renamed tile lists the items again; a change to a disabled station changes nothing until it is enabled. A test
mutates every public property of `StationDefinition` in turn, so a field added later is covered.

`LiveItemFinder.FindLiveItem(channel, stationId)` finds a station's item by channel id and external id (the
repository compares `ExternalId` with `==`, `BaseItemRepository.TranslateQuery.cs:472-474`); a blank id finds nothing.
The poller looks up every station's item by its own id (card L4); the station routes pass the id of the station they
resolved (card L5), and `Health`'s top-level fields the primary SUB/WAVE station's.

Static media sources only: Jellyfin returns a channel item's saved sources first and then
appends any dynamic ones (`MediaSourceManager.GetPlaybackMediaSources`), so implementing
`IRequiresMediaInfoCallback` as well would show two copies of the stream.

## Media source: Relay only

Every station's `MediaSourceInfo` is `Protocol=Http`, `IsRemote=true`, `IsInfiniteStream=true`,
`SupportsProbing=false`, the nominal runtime below, `Container` and one audio `MediaStream` (codec,
bitrate, sample rate, channels) from the station, and `SupportsDirectPlay=false`, `SupportsDirectStream=false`,
`SupportsTranscoding=true`. The server relays every station with ffmpeg; clients only talk to Jellyfin. `Path` is the
server-side URL and the ffmpeg input: a SUB/WAVE station's `BaseUrl` + `StreamPath` (card 14 rules), a plain
stream's `StreamUrl` (whole-URL rule, query kept). `EncoderPath` and `EncoderProtocol` stay unset, so
`EncodingHelper.AttachMediaSourceInfo` reads `Path` (`EncodingHelper.cs:7505-7512`). AAC is relayed like MP3 (L0).

**Why Relay only (card L10, HI 2026-10-28).** Until L10 a station could be set to Direct: `SupportsDirectPlay=true`,
`Path` an optional client stream URL and `EncoderPath` the server URL. Direct was card 2's fallback in case ffmpeg
could not relay an endless stream. Relay worked everywhere it was used, and Direct added failure modes (mixed content
on https, stations a client cannot reach, AAC decoded on the client, the stream URL handed to every client to fetch)
and a choice a new admin cannot judge. `Delivery` and `ClientStreamUrl` left `StationDefinition` and the 0.4.x flat
fields, and with them the `DeliveryMode` enum. An older file's `<Delivery>` and `<ClientStreamUrl>` elements are
unknown to `XmlSerializer`, which reports and skips them, as it did card 15b's removed grant setting (tests read a
0.4.2.0 file and a pre-L10 Live Radio file, both in Direct, the way Jellyfin's `MyXmlSerializer` does: `new
XmlSerializer(type)` through `XmlReader.Create`), and the next save drops them, so a station
saved in Direct is relayed after the upgrade. A page post that still carries them is read the same way
(`System.Text.Json` skips unknown members). `MediaSourceSchemaDate` moved to 2026-10-28 19:00 UTC, so Jellyfin
re-saves the old direct-play sources. The costs HI accepted:

- One ffmpeg per listener, with its own connection to the station. Jellyfin re-encodes each listener's copy; for the
  web client that is AAC at 256 kbps in HLS segments (host log 2026-10-28: `-acodec libfdk_aac -ab 256000 -f hls`).
  Nothing is throttled; the README gives the cost so a small server can plan. Jellyfin's own per-user limits are
  named in the threat model ("One ffmpeg per listener").
- A small re-encode loss, the HLS start delay, and the station sees the server's address, not the listener's.
- A user whose admin turned off "Allow audio playback that requires transcoding" cannot play stations from a client:
  the admin's choice, honoured (the README says so). Jellyfin uses it to decide what it offers (PlaybackInfo,
  `MediaInfoHelper`; media source lists, `MediaSourceManager`). On `/Audio/{id}/stream` it only makes a signed-in
  user's stream a copy instead of a re-encode (`EncodingHelper.TryStreamCopy`); nothing refuses the request, so it is
  not a lock.

**URLs are escaped.** `Path` is `Uri.AbsoluteUri`, never `Uri.ToString()`. Jellyfin 12.3 and 12.4 put a
non-file input between double quotes without escaping it (`MediaEncoder.GetInputArgument`, `MediaEncoder.cs:480-488`,
calling `EncodingUtils.GetInputArgument`, `EncodingUtils.cs:14-22`), and `ToString()` unescapes `%22` to `"` (and
keeps a literal `"`, `\` or space as typed), so a query such as `?a=%22 -y …` could end the argument and add ffmpeg
options. `AbsoluteUri` keeps `%22`, `%5C` and `%20`; a test checks that no quote or backslash reaches it.

**HLS (decided in L2 from the 12.3 source).** A station is HLS when its container is `hls` (or `m3u8`) or
its URL path ends in `.m3u8`. Jellyfin's own M3U tuner reads the extension of the URL path the same way
(`M3UTunerHost.IsManifest`, `M3UTunerHost.cs:35`, `222-229`), where `.m3u8`, `.m3u` and `.mpd` count as manifests;
Jellywave serves `.m3u8` as HLS and refuses `.m3u` (ffmpeg cannot open the playlist, L0) and `.mpd` (DASH, untested).
Its media source has `Container=hls`, which is what Jellyfin's own probe reports for HLS input. Evidence that the
URL reaches ffmpeg as a plain input:

- The input argument is ` -i "<url>"` (`EncodingHelper.GetInputArgument`, `EncodingHelper.cs:1281-1282`), from
  `Path` since `EncoderPath` is unset (`EncodingHelper.cs:7505-7512`).
- `EncodingHelper.GetInputModifier` forces an input format (`-f <container>`) only for a video request of a
  `VideoType.VideoFile` with hardware acceleration on (`EncodingHelper.cs:7475-7485`); an audio item is never a video
  request, so ffmpeg detects HLS from the playlist itself. Every other input option there comes from server
  settings or the request (analyze duration, probe size, user agent, seek), not from the container.
- The container reaches only output decisions: the output extension falls back to `.ts` for an infinite stream with
  no extension (`StreamingHelpers.cs:168-174`), and `GetAudioBitStreamArguments` adds `aac_adtstoasc` when AAC from
  an `hls`, `ts` or `aac` source is copied into fMP4 segments (`EncodingHelper.cs:1578-1586`). The HLS output's
  `-f hls` comes after the input (`DynamicHlsController.cs:1640-1644`).
- `IsInfiniteStream` and `SupportsProbing=false` change nothing on this path: `SupportsProbing` is read only when a
  live stream is opened (`MediaSourceManager.cs:673`), which a static channel source never is.

L0 showed `jellyfin-ffmpeg -i <m3u8 url>` relaying FIP (AAC LC, 48 kHz) on the test host, so HLS stations are
accepted. (A playlist is not a byte stream a client could fetch for direct play either; Jellyfin's M3U tuner refuses
direct play for manifests for the same reason, `M3UTunerHost.cs:160-167`. Before L10 that made HLS the one kind that
was always relayed.) ffmpeg fetches the playlist's segments itself, from whatever
hosts the playlist names; that is the same boundary as ffmpeg following a redirect (admin-configured URL, accepted
in the threat model). Client playback of HLS stations is checked at the host test stop.

The declared codec matters: Jellyfin may copy the audio instead of re-encoding it when the declared
codec suits the client (`UniversalAudioController` allows stream copy), so a codec that does not match the stream can
fail playback. The configuration page's probe (L3) fills the format from the station.

**What clients still see.** The station URL is part of the item's playback info: it is the media source's `Path`,
which Jellyfin's `Items/{id}/PlaybackInfo` returns to any signed-in user who can see the item (for a channel item that
includes `Channel.IsVisible`) and to any API key; no playback permission is checked. `/Channels/{id}/Items?fields=MediaSources`
returns it to every signed-in user, the tile hidden or not (see "Who can see the channel"). Relay only does not change this:
L10 removed the second (client) URL and no client is told to fetch the station itself, but `Path` still carries the
URL the server reads (`ChannelItemsTests.No_station_is_ever_offered_for_direct_play` asserts it is the station URL,
query included). The plugin's own station routes never return it. SUB/WAVE streams are
public by design; the plugin does not support listener auth. A plain stream's query is disclosed the same way
(HI decision: allowed; keep secrets out of stream URLs; card R2b: the plugin page says so under the Stations heading,
and the station dialog warns about a query parameter named like a secret, see "Configuration surface").

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
`NowPlayingPollSeconds` (clamped 5–300) it fetches the public `GET /api/now-playing` of every listed SUB/WAVE
station someone needs (card L4; card L14, see "Only while someone listens" below; plain streams are not polled, see
"Station logos"). `NowPlayingState` turns the feed into one line, for example
`Now playing: Artist – Title (Album) · DJ Name · Show · Up next: Show with Presenter at time`
(the "Up next" part comes from `context.showHandover`, which the station sends near the end
of a show), a track line `Artist – Title`, and the cover id (`nowPlaying.subsonic_id`,
accepted only when it is 1–64 characters of letters, digits, `_` and `-`). `Update` reports
which of those changed. The service finds each station's item (`InternalItemsQuery` by channel id and external
id = the station's id) and writes:

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
`AlbumArtists` is in the same key and is left alone for the same reason; `Name` is too, and is written only when the
admin renames a station (card L3, see "Station rename"), which leaves one stale row per listener, not one per track. The channel
entity is loaded by its derived id (`LiveItemFinder.Resolve`, see "Channel identity"), not through
the channel manager, because the latter creates channel entities on demand and the poller must not
race Jellyfin's startup channel refresh.

**Every station, two phases (card L4).** A poll first fetches the stations, then writes the items:

1. **Fetch** (network only, no item is touched): every listed SUB/WAVE station, at most `MaxConcurrentStations` (4)
   at a time (`Parallel.ForEachAsync`). Per station: the feed, then `/api/state`, then the cover when the item exists
   (one read-only lookup; a station whose item Jellyfin has not created yet costs no download) and does not show it
   yet. The channel is resolved once before this phase for that lookup. A failing channel or item lookup (a library
   lock during a scan) counts as "unknown", not as a station failure: no cover is downloaded for it this poll, the item
   keeps its image, the station's feed, state and health are kept as fetched, and only a Debug line is logged. Each call is bounded by `StationClient` (8 s for a JSON call, body included; 8 s for the cover's
   headers plus 8 s for its body), so one station costs at most 32 s, and a poll's fetch phase at most
   ceil(stations / 4) × 32 s: one station's worst case for up to four stations, 64 s for eight. Bounded concurrency
   rather than one by one, because with several stations a single slow or black-holed station (8 s per call) would
   otherwise delay every other station's update by that much each poll; the cap of four keeps a long list from
   opening dozens of connections at once, often to one host, and keeps the bound linear. Anything a station's fetch
   throws is that station's failure: a station failure (unreachable, a timeout, a redirect, not JSON, an empty feed)
   is recorded under its category, and an unexpected exception is logged at Error and recorded as "unexpected error;
   see the server log". Only the poll's own token (shutdown) ends the phase early: it rethrows, and nothing is logged,
   recorded or written for it; a cancellation that is not the poll's (a client timeout) is that station's failure.
2. **Write**, one item at a time in grid order (as in L2): each SUB/WAVE item gets its own station's line and the
   artwork its fetch prepared, each plain stream its logo (at most two logo downloads per poll, 2 × 16 s), every item
   its sort name. An exception while writing one item (a database error) is logged and the next item is still
   written; shutdown is checked before each item and rethrown, never logged. An image save itself is never cancelled:
   Jellyfin 12.4's `ImageSaver.SaveImageToLocation` (`ImageSaver.cs:277-303`) opens the file with `FileMode.Create`
   before it observes the token, so a stop in the middle of a save would leave a truncated image. The token is checked
   before a save starts and by the item write after it.

**Only while someone listens (card L14).** HI 2026-10-29: L9 ships the public SUB/WAVE demo to every fresh install, and
the poller asked every SUB/WAVE station every interval whether or not anyone listened. Now `StationDemand` (a
singleton) decides, once per poll (`Decide`), which listed SUB/WAVE stations are needed; the rule covers every
SUB/WAVE station, the admin's own included:

```
 every poll, for each listed SUB/WAVE station
     ├─ a session plays its item (paused counts), or did ≤ 30 s ago ──yes──► ask the station
     ├─ a station route was called for it ≤ 60 s ago (page open) ─────yes──► ask the station
     └─ otherwise idle: nothing leaves the server for that station
 idle → needed (a playback start of its item, its page's first call) ──► wake the poller
```

- **Playing.** `IPlayingItems` (card L6's view of Jellyfin's sessions, `SessionInfo.NowPlayingItem`, which stays set
  while paused) gives the playing item ids; only when something plays does the poller look up the SUB/WAVE stations'
  items (one query each, like the title sampler). Only the sessions of users who can see the tile count (fix round
  S2): Jellyfin's playback report (`POST /Sessions/Playing`) takes an item id from any signed-in user, and nothing shows
  that it checks the user's access to that item (its `SessionManager` is not in the 12.4 NuGet packages; the event
  carries the item and the users, no access decision), so a user blocked from the tile could otherwise keep a station
  asked.
  `StationDemand` applies `StationAccess.SessionUserGate` (the station routes' gate without the claim rules: the user
  exists, is not blocked from an earlier tile name, and `Channel.IsVisible`; a missing or colliding channel refuses
  everyone) to each session's user and added users, and to the users of a playback start (`PlaybackProgressEventArgs.Users`,
  else the session's). The poll passes the channel it resolved for the covers (`SessionUserGate(Channel)`, one session
  pass, the gate built at the first playing session; fix round 2), and the title sampler applies the same gate (see
  "Sampler"). With no channel of ours (not created, or a
  collision) nothing of ours plays; when a lookup fails (a library lock during a scan, or the gate's channel lookup),
  the stations played at the last lookup that worked count as still played for at most `LookupFailureHold` (2 min),
  so a lock never idles a listened station and a lookup that keeps failing does not keep one asked for good.
- **Page open.** `StationRouteAttribute` calls `StationDemand.PageOpened(id)` for every SUB/WAVE station it resolves
  for a caller (after the channel gate: a signed-in user who can see the tile), receipt routes included; the title
  route does not. The station page asks `Feed` every 5 s while it shows, so `PageWindow` (60 s) covers it with room.
  It shows, and asks, only while a station plays in that client (a pause there becomes a stop, L11 P), so a background
  tab whose timers the browser slows to about one a minute cannot flip a station idle and back: that client's playing
  session keeps the station needed on its own, and an audible tab is not slowed (fix round S1, stationPage harness).
  An unlisted id is 404 before this and marks nothing; the maps are bounded by the listed stations (`Decide` drops the
  rest).
- **Grace.** `ListenerGrace` (30 s) after the last poll that saw the item playing: a stop and a new start, or one
  failed lookup, does not flip the item to the idle look and back. With the default 20 s interval an idle station is
  asked at most once or twice after its last listener stops; a page keeps it for 60 s after the last call.
- **Idle look.** HI Q2: the station's own look, never a stale track and not generic text. An idle station's state is
  cleared and its item gets the station's own text, `JellywaveChannel.IdleOverview`: its `Tagline` (the editor's
  "Description"; Check fills it from the station's DJ tagline), else its `Notes`, else the default text, cleaned like a
  plain stream's notes (`<` and `>` dropped, at most 280 characters), and the embedded logo (a SUB/WAVE station has no
  logo field). A failing station keeps the default text. It is not a failure: `PollerHealth.Idle` marks the time it went
  idle (`IdleSinceUtc`) and leaves `Reachable` and the last success and error as they were, no warning streak starts,
  and nothing is logged beyond one Information line when a station stops being needed ("is idle") and one when it is
  needed again ("is in use"); stations idle from the start log nothing.
- **Wake.** `StationDemand` keeps a `SemaphoreSlim(0, 1)`: `PageOpened` for an idle station and a playback start of an
  idle station's item (`IPlayingItems.PlaybackStarted`, passed on from `ISessionManager.PlaybackStart` with the item's
  external id, which is its station id) release it once; further wakes before the poll are kept as one. The loop waits
  `WakeGap` (1 s) after every poll, so a burst of starts or page opens cannot run polls back to back, then on the
  semaphore with the rest of the interval as its timeout; once the interval is up the regular poll runs without
  looking at the semaphore (fix round R1: a stream of wakes, for example a user reporting a start and a stop of an idle
  station's item every second, otherwise replaced every regular poll with woken ones, which skip the stations already
  asked, and froze the others' tracks). A woken poll with no station to ask fetches and writes nothing, unless it sees
  a station go idle that was asked before (fix round 2: its idle look and idle mark are written then, and its next
  playback wakes the loop again; skipping that poll left it marked as asked until the next regular poll). The loop tests
  (`ListenerDemandTests`, a started host and a clock ten times as fast) guard both. A signal rather than an event handler that fetches: the fetch
  and the item writes stay on the one poll loop (no second writer of an item, no race with the write phase), and the
  wake costs nothing while no station changes state. A woken poll asks only the needed stations not asked since they
  became needed; the others keep their place, because a woken poll leaves the interval's timing (the last unwoken
  poll's end and delay) as it was. The page asks an unheard feed again after 2 s (`FEED_WAKE_MS`, at most three times
  per station), so the live track shows within a few seconds of opening the page.
- **First poll after startup.** It follows the rule like any other: with nobody listening and no page open it asks no
  station (the station logos of plain streams are still fetched once per start, see "Station logos").
- **Reload status.** `POST /Jellywave/Health/Check` (admin) runs `StationCheck`: every listed SUB/WAVE station's feed
  once (at most four at a time, one check at a time, none within `StationCheck.Cooldown` (10 s) of the last one's end;
  nothing with "Show what's playing" off; a press that asks nothing answers with the health as it stands), recorded with
  `PollerHealth.Checked`, which sets `Reachable` and the success or error time but keeps an idle station idle. It
  touches no state, item or warning streak, then answers like `GET /Jellywave/Health`.
- **The on-demand routes** (schedule, likes, lyrics, requests, avatars) ask the station when the page calls them, as
  before (card R2a: through `StationCalls`, under the caller's budgets, the shared limiter and the caches, see
  "Station call budget, limiter and caches"), and each call also counts as the page being open. The schedule's "now" block reads the poller's active show;
  the page asks the schedule again when the first heard feed arrives after an unheard one, so a takeover shows once the
  station is heard.

The next poll starts `NowPlayingPollSeconds` after the previous one ends, so a slow poll delays the next one rather
than overlapping it. Worst case for one poll: ceil(stations / 4) × 32 s + 32 s of logos. There is no deadline for a
poll as a whole (each call has its own budget, and a deadline would turn a slow station into a failed one); instead
the fetch phase is timed (card L14: only a regular poll records it, so a woken poll that asked one station neither
sets the time nor ends a slow streak; a regular poll that asked no station clears it, `PollerHealth.NothingAsked`):
Health shows `LastFetchSeconds`, and one Information line per streak says when the fetch
phase alone took longer than the poll interval (the items then update less often than set). The streak ends only when
a fetch phase takes less than 80 % of the interval (`SlowFetchEndRatio`), so a list that jitters around the interval
does not log a line every few polls. With polling off, or with no
SUB/WAVE station listed, the loop checks again every 30 s and the SUB/WAVE items get the default text and the
embedded logo.

**State per station, keyed by (station id, origin) (cards 17 and L4).** Everything cached from a station is kept
under its `StationKey`: the station id and its origin, `StationUrls.Origin(baseUrl)` (the parsed base URL in full,
`baseUrl.AbsoluteUri`). `NowPlayingState`, `PollerHealth`, `ScheduleCache` and `LiveItemArtwork` (applied cover and
missing-cover memory) each hold one entry per station id that carries its origin. A write under the same id with
another origin (the admin changed the base URL) replaces the entry, so the new URL starts afresh exactly as card 17's
single origin did, and a reader asking with the new key gets nothing of the old one. One station's failure, empty
feed, timeout, URL change or removal touches only its own entry. Each poll the poller drops the entries of stations
that are no longer listed SUB/WAVE stations (removed, disabled, refused, or now a plain stream), so every map holds at
most one entry per listed station.

`NowPlayingState.Update(key, feed)` clears the station's `/api/state` data (history, up next, the lyrics flag) when the
origin differs from the station's last one; `UpdateStationState(key, state)` stores a state only under the key of the
station's current feed. A failed `/api/state` fetch therefore keeps the last state of the same station, and none after
a URL change. A failed or empty feed clears that station's entry. The channel's items and the station routes read
the state through `NowPlayingState.Snapshot(key)` (feed, state and derived lines under one lock): an entry under
another origin answers `NowPlayingSnapshot.OtherStation` (nothing, flagged), a station with no entry
`NowPlayingSnapshot.Nothing`. Each station route reads the snapshot of the station it resolved (card L5): after a
station's URL changes, until the poller has fetched the new origin (at most one poll interval), `Feed` answers as if
nothing has been heard (no track, DJ, history, song id or cover tag; the station name and the channel and item ids
still answer) and `Schedule` names no takeover. The item itself keeps the old origin's text and cover until that
poll's write phase writes it, which comes after the whole fetch phase (up to ceil(stations / 4) × 32 s after the new
origin's feed was stored). So `Feed` also withholds the image tag while `LiveItemArtwork` says the item's applied cover
is another origin's than the station's current one (read only; the logo has no origin and never counts); the tag is
back once the write phase has saved the new origin's cover or the logo. A failing save keeps the old cover, and the
tag withheld, until a save works; it is logged once. Within one origin a new track's cover still lags its text by
the fetch phase, as before.

Cover art (`LiveItemArtwork`, config `CoverArtEnabled`, default on): Jellyfin applies a
channel item's `ImageUrl` only when it creates the item, so the poller saves the image
itself with `IProviderManager.SaveImage(item, stream, mimeType, Primary, ...)`. That writes a
file under the item's metadata folder, deletes the previous one, and stamps the image's
`DateModified`, which is what the client-facing image tag hashes, so clients refetch. The
station answers 502 with an empty body when a track has no art; then, and when cover art is
off, polling is off, or the station is unreachable, the logo is saved instead so the item
never shows a blank. A cover the station has no art for is remembered by origin and id for
`MissingCoverRetry` (30 minutes): while that holds, the poller uses the logo without asking, until
the track's cover id or the station's origin changes, so a track without art costs one request instead of
one per poll (180 to 720 an hour). Art the station gains for that track later shows within that
time. Both memories are per station (card L4): the missing cover is remembered per station id with its origin and
cover id (one entry per station, so one station's miss never replaces another's), and the applied cover is kept per
station id with its origin (`Applied(id)`, `AppliedOrigin(id)`), so two stations whose tracks share an id each get
their art, and a station whose URL changed fetches it again; Health shows the bare id. The clock is a
`TimeProvider`, so both rules are unit-tested. Accepted covers are JPEG, PNG, WebP or GIF of at most 4 MB and at most
4096 px a side, identified from their bytes (see "Outbound requests"); anything else is "no
art". The download happens before the item is looked up,
so the item is held for milliseconds. A transport failure (timeout, a chunked body over the
cap) keeps the text update and is retried on the next poll, and when the item's cover is another
origin's (the station's URL changed) the logo is saved meanwhile, so the old art never sits beside the new
station's text; a failure to write the file is
logged once per streak and the text still goes out. Because the item's parent is a `Channel`,
Jellyfin's `UpdateImagesAsync` takes its "live TV" branch and computes neither dimensions
nor a blurhash for the image, so there is no blurred placeholder while it loads. The tile in
the My Media row is the channel's own image and always stays the logo.

`Artists` is never written. Setting it on an `Audio` item would make Jellyfin's artists
validator create a Music-library artist for every track the station plays.

Failure handling: one warning when a station becomes unreachable, one info line when it
answers again; the loop never throws; while unreachable or disabled the item falls back to
the default overview. A feed that parses as JSON `null` counts as a failure (card 17): health
records "the station answered with an empty feed", the state is cleared and the item falls back
the same way, instead of the last track staying up with neither success nor failure recorded.
All of this is per station (card L4): one Warning per station and failure streak, naming the station and the
failure's category ("timed out", "could not connect", "HTTP 503", "answered with a redirect", "did not answer with
the expected JSON", the empty feed), never the URL, the host or the exception's text; one Information line when that
station answers again. The streak is keyed by the station id with its origin, so a base URL changed to another
failing one warns again. Clients show the text when they reload the list or start playback;
on standard clients the playing bar does not update mid-track (the station page's repaint is the
only exception).

## Plain stream titles (card L6)

A plain stream has no feed, but most Icecast and Shoutcast servers send the current title inside the stream when the
client asks with `Icy-MetaData: 1`: the answer carries `icy-metaint: N`, then N audio bytes, one length byte L, L × 16
bytes of text such as `StreamTitle='Artist - Title';StreamUrl='';` (NUL-padded), and again. HI 2026-10-28: show it as
"Now: …" in the plain header and in the playing bar, as sent (no filtering; the admin chose the station).

**Sampler.** `TitleSampler` is a second `IHostedService` (registered beside the poller). With "Show what's playing"
(`NowPlayingEnabled`) off it samples nothing, does not ask the sessions, and switches `StationTitles` off (HI
2026-10-28: the one checkbox covers SUB/WAVE now playing and plain-stream titles). Otherwise every `SampleInterval` (15 s)
it asks `IPlayingItems` (`SessionPlayingItems` over `ISessionManager.Sessions`: each session's `NowPlayingItem`, which
Jellyfin sets on playback start and clears on stop; paused counts) which items play. Any client's playback report sets
it, a crafted `/Sessions/Playing` call included, so a signed-in user can make the server sample a listed plain stream
without listening; only the sessions of users who can see the tile count (card L14 fix round 2:
`StationAccess.SessionUserGate(Channel)` on each session's user and added users, as for the SUB/WAVE stations), so a
user blocked from the tile cannot, and one who can see it learns only what the Title route would tell them anyway (an
admin-listed stream, one sample per stream per round). Nothing playing: no request and no library query. Otherwise it
resolves the channel (`LiveItemFinder`), asks the sessions again with the gate, and keeps the listed plain streams
(`StationSources.Publish`, kind `Stream`) whose live item one of those ids is; a SUB/WAVE station, a disabled or refused
stream and anything else playing are never sampled. A failing lookup (a library lock, a user lookup) samples nothing
that round. For
each played stream, at most `MaxConcurrentSamples` (4) at a time:

- An HLS stream (`StationSource.IsHls`) is `NoTitles` without a request: a playlist carries no ICY metadata.
- Otherwise `StationClient.SampleIcyTitleAsync(ServerUrl)`: one `GET` through the plugin's no-redirect client with
  `Icy-MetaData: 1` and one 8 s budget (`RequestTimeout`) for headers and body together. The URL is the server URL
  `Publish` built through the whole-URL rule (`StationUrls.TryParseStreamUrl`), the same one ffmpeg reads (the media
  source's `Path`; since L10 a station has no other URL). A 3xx is not followed
  (`StationRedirectException`, target not kept), another non-success status throws with its code.
- No `icy-metaint`, or one that is not a whole number from 1 to `IcyMetadata.MaxMetaInt` (64 KiB): `NoTitles`, and no
  body byte is read. Otherwise exactly `icy-metaint` audio bytes (read in 16 KiB chunks and thrown away), the length
  byte and L × 16 bytes are read, never more than `IcyMetadata.ReadCap` = metaint + 1 + 255 × 16, and the response is
  disposed, which closes the connection at once: the station handler has `MaxResponseDrainSize = 0` (RA R1; by default
  .NET reads up to 1 MB, for up to 2 s, of an unread HTTP/1.1 chunked or declared-length body on dispose to reuse the
  connection, which here would be a megabyte of audio and a late close, also on the `NoTitles` path). A stream that ends
  early throws (`EndOfStreamException`). `IcySampleSocketTests` checks the close on a real socket.
- `IcyMetadata.StreamTitle` parses the block: NUL padding trimmed, decoded as strict UTF-8 (a byte order mark dropped)
  or, when the bytes are not valid UTF-8, Latin-1, which older servers send. The `StreamTitle='` field (any case) runs
  to the first `';` that ends the block or starts another field (`Name=`), so quotes inside a title (`Guns N' Roses`,
  `It';s`) survive; without a terminator the rest of the block is the title, a trailing `'` or `;` dropped.
  `StationText.Clean` then turns control characters into spaces, collapses whitespace and cuts at
  `IcyMetadata.MaxTitleLength` (200) with an ellipsis. An empty title, a block without one and a zero-length block are
  `NoTitles`.
- A transport failure (timeout, refused, an error status, a redirect, a stream that broke off) is `Failing`: one
  Warning per stream and streak ("Title sampling of station X failed: HTTP 503; …", the category only, never the URL),
  one Information line when it works again; an unexpected exception is logged at Error once per streak and also counts
  as `Failing`. The loop survives anything; "Title sampler started/stopped" brackets it. No health entry: the page's
  "Now:" line is the status.

Each sample is a short extra listener on the station (well under a second for an MP3 at 128 kbps with the usual 16 000
byte interval, at most 8 s), every 15 s while someone plays it; the station's own listener count sees it.

**State.** `StationTitles` (a singleton, under a lock) keeps per station id the state (`Waiting` when there is no
entry, `Title`, `NoTitles`, `Failing`; every station reads `Off`, and the entries are dropped, while the sampler has
seen "Show what's playing" off), the cleaned title and the time of the last sample that read the stream. Each
round marks the played streams and first drops every entry whose station is no longer a listed plain stream, or that
nobody has played for `IdleIntervalsKept` (3) intervals, so the map is bounded by the listed streams and a stream
nobody plays loses its title within 45 s. A changed stream URL under the same id keeps the old title until the next
sample (at most 15 s).

**Last read (card L13).** Beside the title state, `StationTitles` keeps per station id the outcome of the last read
and its time, for the configuration page's health (`RecordRead`, `TitleReadOutcome`): `Answered` when the stream
answered with a success status and its headers (`StationClient.SampleIcyTitleAsync` calls the sampler back right after
`EnsureSuccessStatusCode`), whether or not its metadata block then arrived in the budget; `Moved` for a redirect
(`StationRedirectException`: the relay follows it and plays, the sampler never does, so the station gets no title
until its address is the target; fix round S2, MI); `NoAnswer` for everything else (not reachable, an error status, a
timeout before the headers, an unexpected failure). It is not dropped when nobody plays the stream or when "Show what's playing" goes off (it says when the
stream last answered, not what it plays), only when the station leaves the list or its stream URL changes
(`KeepReads`, each round). The record is tied to the URL it was read from by the URL's hash (`string.GetHashCode`,
randomized per process), never the URL: `LastRead(id, url)` answers only for the station's current URL, so the page
never shows an outcome of an earlier URL, even before the next round. HLS streams are never read, so they have no
record. Memory only: a restart forgets every outcome, and nothing extra is requested for it.

**Route.** `GET /Jellywave/Title?stationId=` sits behind `[StationAccess]` and `[StationRoute(PlainStream = true)]`:
the filter asks `StationResolver.ResolvePlainStream`, which accepts only a listed plain stream (exact id, no primary
fallback, so a missing id is 404 too), and leaves a `PlainStation` (the id alone) under its own `Items` key, which no
SUB/WAVE route reads; the SUB/WAVE routes still resolve SUB/WAVE stations only and answer 404 for a plain stream. The
action reads `StationTitles.Snapshot` through `CallerPlainStation()` and answers `TitleResponse` (`StationId`, `State`,
`Title` only in the `Title` state, `UpdatedUtc`); it never samples and never makes an entry. No URL in it.

## Station rename (card L3)

Jellyfin applies a channel item's `Name` only when it creates the item (12.3 `ChannelManager.GetChannelItemEntityAsync`,
`ChannelManager.cs:1027-1029`; an existing item keeps its name, only container folders are renamed), so a station renamed
on the configuration page would keep its old name on its card. The poller therefore writes `Name` = "<station name> —
Live" (`JellywaveChannel.ItemName`, the same text `GetChannelItems` gives a new item) on every listed item whose name
differs, as a `MetadataEdit` in the item's one `UpdateItemAsync`, and reconciles it on every poll like the sort name
(a re-save by Jellyfin's own refresh from an older copy is repaired within one poll). One Information line per rename.
It runs while polling is off too (the write phase covers every listed item) and never for a station that is not listed.

`Name` is part of the item's user-data key: `Audio.GetUserDataKeys()` builds its first key from `Name` (`Audio.cs:100-124`,
`songKey += Name` at 111, inserted first at 124) ahead of the external id and the item id (`BaseItem.cs:1766-1778`). What
that costs was checked in the 12.3 source rather than assumed from card 7:

- Rows are per item. `UserDataManager.SaveUserData` (`UserDataManager.cs:50-108`) writes one row per current key under
  the item's id, and keeps the rows under keys the item no longer reports, updating them with the same values
  (`ExecuteUpdate`, 79-92). A rename therefore leaves one row under the old name per user who has data on the station.
- The lookup falls back. `ResolveUserDataRow` (`UserDataManager.cs:386-403`) tries the item's keys in order (new name,
  then the external id, then the item id) and then any row of the item, so after a rename the row under the external id
  (the station id, unchanged) still answers. **A favourite or play state on a renamed station is kept.** The planned
  note "renaming a station resets its favourite" (card L3 brief) does not hold for 12.3 or 12.4; the page says the
  favourite is kept.
- The cost is bounded by renames, not by tracks: card 7's reason to leave `Album` alone was a value that changes every
  track. A station rename is an admin action.

## Station logos (card L2)

Plain streams' items get their Primary image from `StationLogos`, which the poller runs on every poll for every listed
plain stream (also while now playing is off); every SUB/WAVE item gets its cover or the embedded logo from
`LiveItemArtwork` since card L4. The poller also resets a stream's overview to the default text, since Jellyfin applies
a channel item's overview only when it creates it (an id that was a SUB/WAVE station would keep its last line):

- A plain stream with a `LogoUrl` gets that image, downloaded by the plugin through `StationClient.GetLogoAsync`: the
  plugin's no-redirect client, the 8 s budget, 4 MB, a declared type that is missing or `image/*`, and bytes that
  `ImageSniffer` accepts (JPEG, PNG, GIF or WebP, at most 4096 px a side). The sniffed type is what is saved. It is
  saved with `IProviderManager.SaveImage` and written with `UpdateItemAsync(..., ImageUpdate)`, the cover path. Never
  through `ChannelItemInfo.ImageUrl` (see "The station items").
- The logo URL must pass the plain-stream whole-URL rule (`StationUrls.TryParseLogoUrl`: http or https, a host, no
  userinfo, no fragment; a query allowed). A blank or refused one never makes a request; the item gets the embedded
  logo, and a refused one logs one Warning that names the station and the fixed reason.
- A logo that fails (unreachable, a redirect, a non-image type, oversize, unknown bytes) is remembered by its key
  for `FailedLogoRetry` (30 minutes, an injected `TimeProvider`, the card 17 pattern); meanwhile the item shows the
  embedded logo and the logo is not asked for. One Warning per station and streak names the station, never the URL; the
  streak is the logo key plus the kind of problem, so a URL that is fixed and later broken again warns again.
- A downloaded logo whose save fails (metadata folder not writable, disk full) joins the same negative cache, so it is
  not downloaded again every poll; the error is logged once per station and streak.
- One poll starts at most `MaxDownloadsPerPoll` (2) logo downloads; the rest wait for the next poll, and an item with no
  image gets the embedded logo meanwhile, so no card is blank (an applied embedded logo does not stop the later
  download). What is remembered for a station (applied logo, warning and save-error streaks) is dropped once it is no
  longer listed. Each download is
  bounded by the client (8 s for the headers, 8 s for the body), so logos delay the next now-playing poll by at most
  about 32 s however many stations there are. Every SUB/WAVE station's feed and cover are fetched before any logo.
- The key is the station id, its kind and its trimmed logo URL. The poller reads the item first (it may not exist
  yet; no download then), and downloads only when the key differs from the one last applied to that station's item
  or the item has no Primary image (a re-save by Jellyfin dropped it); after a download it reads the item again, so
  the instance written is held for milliseconds. What `StationLogos` remembers is dropped for every id that is not a
  listed plain stream, and what `LiveItemArtwork` remembers for every id that is not a listed SUB/WAVE station, so a
  station that changes kind and back gets the right image each time (neither keeper trusts an "already applied"
  that the other one painted over). After a restart every logo is
  downloaded once more.

## Who can see the channel

`Channel.IsVisible` (Jellyfin) shows a channel to a user when the user has a block list that does
not name it, or, with no block list, when the user has `EnableAllChannels` or the channel in
`EnabledChannels`. Administrators are not exempt. Server-side user creation turns
`EnableAllChannels` on (the `AddDefaultPermissions` extension in `UserEntityExtensions`, called from
`UserManager.CreateUserInternalAsync`), but jellyfin-web 12.4's Add User page
(`src/apps/dashboard/routes/users/add.tsx`) leaves "Enable access to all channels" unticked and then
posts the policy, so a user added in the dashboard cannot see the tile; templates and provisioning
tools often do the same. That per-user Channel Access setting is the only ongoing
authority; the plugin never changes it on its own (card 15b).

**Hidden, not locked (HI decision D6).** Channel access decides what Jellyfin lists, not what it serves; Jellyfin 12.4
works this way for all media. A user the tile is hidden from (unticked, or blocked on its current id) does not see it
in My Media or `/Channels`, and `/Items/{id}` and PlaybackInfo answer 404. But `ChannelsController.GetChannelItems`
checks no visibility, so any signed-in user can list the channel's items with their stream URLs
(`/Channels/{id}/Items?fields=MediaSources`, the channel id derived from the tile name) and stream them, and
`AudioController` has no `[Authorize]`, so `/Audio/{id}/stream` plays an item for anyone who has its id, signed in or
not (listed in jellyfin/jellyfin#5415, closed as a duplicate; still true in 12.4). The plugin cannot close the
signed-in part: answering no items for one user would make Jellyfin's reconciliation delete the items for everyone.
What it does: the `/Jellywave/*` routes check access (403, "Station route authorization"), station ids are random so
item ids cannot be computed from public facts ("Public station ids re-keyed"; any signed-in user can still read them
from the channel items route and pass them on), and the plugin page says all this under Stations (card R2b). Host test
on 12.4, 2026-10-30.

For users who cannot see the tile, the plugin page has a one-shot button, "Add the tile to every
user now", which calls `POST /Jellywave/GrantAccess` (admin only, `RequiresElevation`).
`AccessGranter.GrantAllAsync` resolves the channel through `LibraryChannelLocator`, then walks every
user: `Classify` mirrors `Channel.IsVisible` exactly (plus the rename bridge: a block on an earlier tile
counts as blocked), and for a user who needs it
`AddToEnabledChannels` adds the one channel id to `EnabledChannels`, persisted with
`IUserManager.UpdateUserAsync`. Nothing else in the policy is touched: `EnableAllChannels` is never
set, no id is ever removed, and a user whose block list names the channel is skipped and counted.
The result carries the counts (granted, already visible, blocked), or an `Error` and no writes when
the configuration is not loaded, the channel entity does not exist yet, or its name collides
(card 15). One Information line per press carries the counts, never user names.

**Who can see it, counted (card L13).** `GET /Jellywave/Viewers` (admin only, on the same `RequiresElevation`
controller) runs `AccessGranter.ViewersAsync`: the same channel lookup and the same `Classify` (rename bridge
included) as the grant pass, reading every user and writing none, outside the grant gate (it may be one press behind
a press in progress) and without a log line. The lookup is `LibraryChannelLocator.LocateAsync`, as for the grant and
rename buttons: when the tile's entity does not exist yet and a station is listed, it calls
`IChannelManager.GetChannelsInternalAsync`, which creates the entity of every registered channel (the same work a My
Media load does), so the count works right after the first save or a rename. It writes no user (RA R1, option a). It answers `TileViewers`: `Users` (active users: a disabled user,
`PermissionKind.IsDisabled`, is left out entirely, MI fix round), `Visible` (the users `Classify` calls visible,
which equals a grant pass's "already visible" over the same active users) and `NoAudioTranscoding`, the names of visible users
whose policy has `EnableAudioPlaybackTranscoding` off (sorted, case ignored). Every station is relayed through ffmpeg
(card L10), and with that permission off PlaybackInfo answers 200 with no `ErrorCode` and nothing playable (D3, host
run 2026-10-29), so those users cannot play any station and nothing tells them why. The plugin page shows "N of M
active users can see the tile." above the grant button and, only when the list is not empty, a yellow "⚠ <names> can
see the tile but can't play stations: "Allow audio playback that requires transcoding" is off for them (Dashboard →
Users → <user> → Profile → Media playback)." below it (at most ten names, then "and N more"; "each user" in the path
when there are several); it reads again after the grant and rename buttons and after Save, and only the newest
answer is shown (a numbered read).
The place is jellyfin-web 12.3.0's (read on the test host, read-only, 2026-10-29): the user edit page's tabs are
Profile, Access, Parental Control and Password (`users-edit.*.chunk.js`: `b==y.v.Profile&&(0,r.jsx)(M,{userDto:m})`),
and the Profile component `M` renders a section headed `HeaderPlayback` ("Media playback") with the checkbox
`chkEnableAudioPlaybackTranscoding`, `title:"OptionAllowAudioPlaybackTranscoding"` ("Allow audio playback that
requires transcoding" in `en-us-json.*.chunk.js`). On a collision, a missing tile or an unloaded configuration nothing is counted and `Error` says why.

Nothing else writes a policy: no hosted startup pass, no user event consumer, no setting. Users created
later see the tile when the admin ticks "Enable access to all channels" or the tile on the
dashboard's Add User form, when the provisioning tool's template includes it, or after another press.
An admin who unticks the tile on a user's Access page is not overruled in the background, but the
next press re-adds it: an unticked user looks the same as one never granted. Only a `BlockedChannels`
entry survives a press, and jellyfin-web has no screen for it; the page tells the admin to untick
those users again after pressing. A policy save never touches `BlockedChannels` in Jellyfin 12.3 or 12.4:
`UserManager.UpdatePolicyAsync` writes `EnabledChannels` and the other preference lists but not
`BlockedChannels`, so the policy API can neither set nor clear a block, and jellyfin-web's Access page
(`Access.tsx` posts `BlockedChannels = null`) leaves an existing block in place. Host test 2026-10-28:
posting a policy with `BlockedChannels` set to a channel id, to `null` and to `[]` each answered 204 and
left the stored list unchanged. Blocks come from older Jellyfin versions, from plugins that write the user
entity (this one's "Give them access to the renamed tile"), or from database edits.

Each user is re-read by id immediately before it is written, because `GetUsers` hands out untracked
snapshots and `UpdateUserAsync` saves the whole entity. Presses are serialized (card 15): one
`SemaphoreSlim` in `AccessGranter` (a DI singleton, disposed with the container) covers the channel
lookup, every user read and every save, so a second press waits and then re-reads every user after the
first has saved; the plugin never saves a stale copy over its own write. `UpdateUserAsync` publishes no
event (`IUserStore.SaveAsync` must stay that way: an event consumer that called back into the granter
under the gate would wait on it forever). Accepted (Phase 5 HI decision D3): any change to a user saved
between the plugin's read and its `UpdateUserAsync` (milliseconds, and only while a press is running) is
overwritten, because `UpdateUserAsync` writes the whole user (`SetValues`, `SyncPermissions`) and `IUserManager` has no
partial update. That is not only channel access: a dashboard save, a password change (`ChangePassword`) or a lockout
after failed sign-ins (`IsDisabled`), each written straight to the database, can be undone by the stale copy (an old
password hash put back, a lockout lifted). The admin sees the old value on reload and saves again.

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

**"Give them access to the renamed tile" (rename bridge, card L1; "Carry access over" until card L11).** After a rename the page shows a notice while
`GET /Jellywave/CarryAccess` (admin) counts users whose enabled list or block list names a pending earlier
tile's id without the current one (read-only; on a name collision it reports the collision instead).
Pending means: an earlier name the carry-over has not run for yet (`CarriedChannelNames`), whose id is
not the id of another plugin's registered channel. Such an id would act on that channel's users, so it is
skipped; the route reports how many earlier names were skipped this way and the page says so in one line.
Skipped and carried names stay on the hiding side (`IsEnabledFor`, `StationAccess`), which uses every
earlier name. Its
button calls `POST /Jellywave/CarryAccess`, `AccessGranter.CarryOverAsync`: under the same gate as the
grant, it snapshots the user ids and re-reads each user right before deciding and saving; where an
earlier id is in `EnabledChannels` and the current id is not, it adds the current id there; where an
earlier id is in `BlockedChannels` and the current id is not, it adds the current id there. Adding a
block only narrows access: from then on the user's block list names the current id, so Jellyfin's own
`Channel.IsVisible` refuses the tile (My Media, `/Channels`, `/Items/{id}`, PlaybackInfo), and the bridge no longer
depends on the plugin's hiding alone. It does not lock the tile: the channel items route and `/Audio/{id}/stream`
still answer that user (host test 2026-10-30; "Hidden, not locked" above). Add-only: nothing is removed, `EnableAllChannels` is never set, and nothing is
written when the plugin is not ready, the channel does not exist yet or its name collides. The result
counts carried grants, carried blocks, users already current and users naming no earlier tile; one
Information line carries the counts, never user names. After a run that finished, the names it covered are
added to `CarriedChannelNames` (under one lock with the page save, which keeps that list like the earlier
names) and saved, so the carry-over is one-shot: a second press carries nothing and reads no user, and an
admin who later takes the tile away from such a user is never offered the carry-over again. A run that
fails or does nothing (not ready, missing channel, collision) records nothing. Until the admin presses it,
users granted by name simply do not see the new tile (the safe direction). The grant button's pass counts a
user whose block list names an earlier tile as blocked (the bridge hides the tile from them), not as
already visible.
`PreviousChannelNames` stays after the press, because old blocks keep being honoured for as long as they
exist; a policy save does not remove them (see above).

## Station page (optional, cards 8s–9, 18 and L5)

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
browsers even while the plugin version stays the same), `GET /Jellywave/Stations` (card L5: the channel id, the tile
name and the listed stations in grid order with id, name, live item id and image tag; card L8: every kind, with `Kind`,
`Notes`, and for a plain stream `Genre`, `Website`, `Codec` and `BitrateKbps` (card L10 dropped `Delivery`); never a
stream or station URL) and `GET /Jellywave/Feed?stationId=` to users who can see the channel (see "Station route authorization";
that station's last feed reduced to display fields plus its station id, the channel id, the live item id and its image
tag; never a station URL), `POST /Jellywave/Request {Text, Name, StationId}` and `GET /Jellywave/Request/{id}` for
listener requests, `GET /Jellywave/Title?stationId=` (card L6: a plain stream's sampled title, see "Plain stream
titles") and `GET /Jellywave/WebUi` (admin) for the status line on the configuration page.

The script (`Web/ui.js`) does three things:

1. **The page.** The default (modern) layout has no user drawer, so the station page is the tile's own library page,
   one click from the tile or the top bar. Both layouts render a channel through the legacy list controller, whose
   `.itemsContainer` carries `data-parentid`; when that equals the channel id (from `GET /Jellywave/Stations`), a header
   is inserted above the toolbar row **only while a listed station's item is the current item, and for that station**
   (card L5b, HI 2026-10-28, after the host test; it replaces card L5's picker; card L8, HI 2026-10-28: plain streams
   too, so the header is a normal sight). Nothing playing, or an item of another library or a station not listed: no
   header, just Jellyfin's grid. There is no picker and nothing is kept in `localStorage`. The header's station is
   `playingStation()`; `followPlayback()` compares it with the header's (`state.selected`) on every playback event
   (`playbackstart` after the list is read again; `pause`, `unpause`, `statechange`, `playbackstop` 50 ms later), on
   every 5 s tick (so a change no event reported is caught), on `viewshow` and on the DOM-change pass. On a change it
   resets the station state (`setStation` → `resetStation`, which bumps the epoch, so late replies for the old station
   are dropped) and removes every header; `ensureHeader` then builds the new station's header if the tile's page is
   shown. A change is another station id or the same id under another kind (`state.selectedPlain`), so a station an
   admin re-checked into the other kind gets the other header on the next list read. So: another station starts → the
   header switches; a stop → the header goes and the poll stops at the next tick when nothing else needs it; a station
   played elsewhere → its header shows the moment the tile's page is shown. A SUB/WAVE header has cover (the live item's
   own Primary image, by tag), track, DJ with tagline, show, what's next, listener count, **Stop**
   (`playbackManager.stop()`; no Play, which could never apply), the station's notes under the track line when set (card
   L8), and the request form. Every call the header makes (feed, like, lyrics, schedule, portrait, request) names its
   station. What the station turns off stays hidden: the heart when `Like.Enabled` is false, the lyrics panel when the
   feed's `LyricsEnabled` is false, the schedule panel when the station publishes none (`Available` false; the schedule
   is asked once when the header is built, then every five minutes while the panel is open or while the last answer had
   none; a failed request hides the panel too), Recently played when empty. In the legacy layout a drawer link named
   after the tile opens the same page via `appRouter.showItem`; it shows whenever a station is listed (card L8: plain
   streams count), playing or not. The grid cards get no SUB/WAVE badge (the optional ✦ of the mockup is left out).

   **A plain stream's header (card L8)** is its own fixed markup (`jellywave-header-plain`, cover and body only): the
   item's image by tag (its logo), the name, the genre, the notes, a format line ("MP3 · 128 kbps", from the
   list's `Codec` and `BitrateKbps`; HLS shows as HLS; no delivery since L10), the website and Stop. It is
   drawn from the station list and asks only for its title (card L6): no feed, like, lyrics, schedule, portrait or
   request call (the SUB/WAVE routes would answer 404 for its id anyway). While it shows, the 5 s tick follows playback,
   redraws it from the list, which is read again at most once a minute, so an edited notes or website line shows within
   a minute, and asks `Title?stationId=` (when the header is built and on every tick; one request at a time). A plain
   stream started off the tile's page starts no timer and asks no title; a plain header left in a cached view keeps the
   tick going (the list at most once a minute, the title every 5 s) until playback stops or moves. Once the header has
   left the document (jellyfin-web evicted its view), the title is still asked every tick while its stream plays,
   provided this page painted that stream's title into the bar during this playback (`state.playing.plainId`, reset on
   every `playbackstart`), so the bar does not freeze on an old title (RA R2); a page that never painted asks nothing.

   **The "Now:" line (card L6)** sits under the notes, its text through `textContent` only. `state.titleSince` is set
   when the page selects the station (`resetStation`, on the playback start it sees), and the line reads:
   "Querying upstream…" while there is no answer or the server says `Waiting`, for `TITLE_WAIT_MS` (30 s) from then;
   "Delayed response: is the stream up?" after that, at once while the server says `Failing`, and when the title route
   itself fails for 30 s; "Not supplied by the station" (`NOW_NOT_SUPPLIED`, one constant) at once for `NoTitles`, and
   for a stream the list labels `HLS`, which is then not asked at all; the title as sent for `Title`; no line for
   `Off`. A one-shot timer redraws the line when the 30 s pass, so the switch is on time even when
   the 5 s tick has another phase. A reply is kept only under the same epoch and when it names the station asked for; a
   403 refuses the page, a 404 (the stream left the list or is no longer plain) drops the header and reads the list at
   once, as a feed 404 does. Only a station the list calls `Stream` asks for a title; a missing or unknown kind gets the
   plain header without the line and asks nothing.
   The website becomes the link's `href` only after the page's own check (`safeWebsite`: `http://` or `https://`, an
   ASCII host or an IPv6 literal, an optional port, then printable ASCII only, so no whitespace, control character or
   userinfo (`@` before the path), at most 2048 characters; the server applied the same rule and serves an
   international host as punycode), with `target="_blank"` and `rel="noopener noreferrer"`; its text is the host
   without `www.` and an arrow, so a look-alike host shows as `xn--…`. Anything else leaves the link hidden and
   without `href`. Only a station the list calls `SubWave` gets the live header and the SUB/WAVE routes; a missing or
   unknown `Kind` gets the plain header, which asks the server nothing.

   **Paused counts as playing (fact, jellyfin-web 12.3.0 as installed on the test host, read 2026-10-28).**
   `playbackManager.isPlaying(player)` in `main.jellyfin.bundle.js` returns `player.isPlaying()` when the player has
   that method, and `player.currentSrc() != null` otherwise. The HTML audio and video players
   (`htmlAudioPlayer-plugin.*.chunk.js`, `htmlVideoPlayer-plugin.*.chunk.js`) have no `isPlaying`; their `pause()`
   only pauses the media element, and `_currentSrc` is cleared only in the media helper's stop/ended path (the
   function that triggers `stopped`). The Chromecast player's `isPlaying()` is true while `NowPlayingItem` is set.
   So `isPlaying` stays true while paused, and the script's existing `isPlaying` + `currentItem` check keeps a
   paused station's header without a `paused()` special case. Since card L11 a station is paused for half a second at
   most (below).

   **Stop instead of Pause (card L11, HI 2026-10-29).** A live stream has no "resume where you left off": a pause kept
   ffmpeg relaying and a resume played from the buffer, behind live. Paused no longer lasts for a station: while a
   listed station's item (either kind) is the current item, `document.body` carries `jellywave-station-current` and
   CSS the script injects once (one `<style>` in `head`) hides every `.nowPlayingBar .playPauseButton` and shows
   jellyfin-web's own `.nowPlayingBarCenter .stopButton` (wired to `playbackManager.stop`); the class goes on a stop or
   any other item (`followPlayback` and `forgetPage` set it from `playingStation()`). A pause of the current station
   item becomes a stop: on a player's `pause` the script notes the listed station and the player, and after
   `PAUSE_SETTLE_MS` (500 ms) calls `playbackManager.stop(player)` only if that player is still current, the same station
   is still its current item and `paused(player)` is true. Every other item keeps Jellyfin's pause. Facts behind it
   (jellyfin-web 12.3.0, commit c06cb17b27, as installed on the test host, read 2026-10-29):
   - The bar (`14245.*.chunk.js`) builds a centre group `previousTrackButton`, `playPauseButton`, `stopButton`
     (`t.querySelector(".stopButton").addEventListener("click",(function(){S&&p.f.stop(S)}))`), `nextTrackButton`, and
     a second `playPauseButton` in `.nowPlayingBarRight`; it never hides the Stop button by class. Its CSS
     (`14245.*.css`): `@media (max-width:80em){….nowPlayingBarCenter .stopButton{display:none!important}}` and
     `@media (max-width:56em){.nowPlayingBarCenter{display:none!important}}`; `.layout-mobile .nowPlayingBarRight
     button:not(.playPauseButton):not(.nextTrackButton){display:none}`. So below 56em (phones, the Android app) the
     right play/pause is the bar's only control and jellyfin-web's Stop cannot show. **DA choice (the card assumed the
     Stop could show at every width):** there the script adds one Stop of its own to the right group (before its
     play/pause; jellyfin-web's `paper-icon-button-light mediaButton` classes, the `material-icons stop` icon, "Stop" as
     title and label, calling `playbackManager.stop()`), hidden by the injected CSS unless a station is current and the
     centre group is hidden. jellyfin-web builds the bar once and keeps it, so the button is added once.
   - Width alone does not decide that (12.4, found on a tablet, 2026-10-30): when `browser.mobile` is true the bar
     also does `.nowPlayingBarCenter.classList.add("hide")`, at every width, and it then has a `nextTrackButton` in the
     right group in place of the centre one. So a mobile browser wider than 56em (the Android app on a tablet in
     landscape, a large phone in landscape) has no centre group either. The injected CSS covers it with
     `.nowPlayingBarCenter.hide~.nowPlayingBarRight`; the two groups are siblings.
   - `playbackManager` does not pass a player's `pause` on: it listens `on(player,"pause",Te)` with
     `function Te(){Qe(this,"pause")}`, and `Qe` only reports playback progress (`main.jellyfin.bundle.js`); it fires
     `playbackstart`, `playbackstop` and `playerchange` itself. The bar listens on the current player and rebinds on
     `playerchange`; the script does the same (`bindPlayer`, also on `playbackstart`). (The script's older listeners for
     `pause`, `unpause` and `statechange` on `playbackManager` therefore never fire in 12.3 or 12.4; they stay for other
     versions.)
   - A switch to another item stops the old one first: `(e===t?e.stop(!1):e.stop(!0)).then((function(){U(e).streamInfo
     =null,…,p.A.trigger(o,"playbackstop",[{…nextItem:n…}])}))`. The HTML audio player's `stop` calls the media
     element's `pause()` and its `stopped` path (which clears `_currentSrc`) synchronously
     (`htmlAudioPlayer-plugin.*.chunk.js`: `t.stop=function(e){…n.pause(),c.X9(t,n,_)…}`), and the element's `pause`
     event is a task that arrives after the promise above cleared the stream info, so `currentItem(player)` is null and
     `isPlaying(player)` false when that pause is heard: no station, nothing scheduled. The settle and the same-station,
     same-player and still-paused checks cover a slower switch.
   - The cast player fires `pause` on every play-state change, not only on a pause
     (`chromecastPlayer-plugin.*.chunk.js`: `d.A.on(e._castPlayer,"playstatechange",(function(t,a){…d.A.trigger(e,"pause",
     [n])}))`), and its `paused()` is the receiver's last report (`lastPlayerData.PlayState.IsPaused`). On a cast
     receiver only the settle and the still-paused check keep a station from being stopped while the receiver loads
     the next one, so a station switch on cast is a host step ((h)19), not a harness case.
   - `playbackManager.paused(player)` is `player.paused()`; the HTML audio player's is the media element's `paused`.
     Media keys, the lock screen and the Media Session pause call `playbackManager.pause(player)` → `player.pause()`, so
     they arrive as the player's `pause` like the bar's. The space bar is not one of them: jellyfin-web 12.3's key map
     (`main.jellyfin.bundle.js`, module 74550) has no Space entry, so outside the video player Space activates the
     focused button, often the grid's play button, which restarts the item (HI host run 2026-10-29).
2. **Repainting while a SUB/WAVE station plays.** Every client copies the playing item at playback start and never
   re-reads it (`playbackmanager.js` `getNowPlayingItemForReporting`), so while a listed SUB/WAVE station's item is the
   current item the script repaints the bar's thumbnail and text, that item's card in any visible grid, and the browser
   `MediaSession` metadata (lock-screen art) every five seconds from **that station's** feed: the header's own feed when
   the header shows it, otherwise (off the tile's page) it asks `Feed?stationId=` for the playing station itself and
   drops a reply once playback has moved to another item. An item of another library is not in the station list, so
   nothing is repainted for it (`playingSubWave()`). A plain stream (card L6) is repainted only from its sampled title,
   while its header exists or, after the header left the document, while this page keeps the bar it painted: when the last answer is `Title`, the bar's first line is "Artist – Title" (the
   title split at the first `" - "`, both sides trimmed; without one, or with an empty side, the whole string), the
   second the station's name, and the media session gets the title and artist (the station's name when there is no
   artist); no thumbnail or card change. Without a title the bar keeps what it shows (Jellyfin's own text, or the
   last title), as a SUB/WAVE bar keeps its last track after a failure.
   `playbackManager.currentItem(player)` throws when no player exists, so the script asks `getCurrentPlayer()` first and
   guards each step.
3. **Requests.** The form posts text and a free-text name (prefilled with the Jellyfin user name;
   the listener can change it; the plugin collapses its whitespace, cuts it at 40 characters and
   otherwise passes it on unchanged) to the
   plugin, which forwards them to the station's `POST /api/request` and maps the receipt (202) or
   the refusal (400, 429 with `retryAfter`, 503) into one outcome object; the page then polls
   `GET /Jellywave/Request/{id}` every three seconds until the station resolves it and shows the
   DJ's acknowledgement, the queued track and its position. The station limits by caller IP, and
   every Jellyfin user is one caller to it. Card R2a (HI decision D10) adds the plugin's own per-user cap in front of
   it: one request a minute and four an hour per user across all stations, and 60 station calls a minute of every
   kind; the station's own lower limit still wins (see "Station call budget, limiter and caches"). The `RequestId`
   the page gets and polls with is the server's own receipt token, never the station's id. The server keeps the real
   user id with each accepted request (see "Station route authorization").

   **No "requests off" state on the page (card L5b decision).** SUB/WAVE publishes no requests on/off flag (checked
   2026-10-28 on two stations' `/api/state` and `/api/now-playing`), so the form shows for every SUB/WAVE header. A
   refusal is reported as `refused` (empty or too long text, 429 rate limit, any other 4xx) or `unavailable` (the
   station's 503, which SUB/WAVE uses both for "requests disabled" and for "autopilot", plus a network failure or a
   success without a receipt). No outcome means only "this station does not take requests": `unavailable` mixes a
   station switch with a passing autopilot or outage. So the page does not hide the form after a refusal; it shows
   the station's own message (`Message`, with `RetryAfterSeconds` when given) and the form stays ready.

Extras (card 8d), each an endpoint on `JellywaveWebController` behind the same access filter and a block in the header:

| Feature | Server side | Page |
|---|---|---|
| DJ portrait | `GET /Jellywave/Avatar/{id}?stationId=` proxies that station's `/api/persona-avatar/{id}` (id validated, 4 MB cap, one still image only, one hour private cache; card R2a: kept 10 minutes on the server); the id comes from the feed's `dj.avatar` path | `<img>` next to the DJ line; the bytes are fetched with the session through `ApiClient.ajax` and shown as a blob URL (revoked on change), because Jellyfin honours `?api_key=` only under `EnableLegacyAuthorization` and an `<img>` cannot send the header |
| Up next, recently played | the poller also fetches `/api/state` each cycle (a failure keeps the same station's last state, see "State per station" under "Now playing"); `Feed` carries `UpNext` and up to 8 `Recent` lines ("· requested by X"), skipping null entries | one line and a collapsible list |
| Like | `GET /Jellywave/Like?stationId=` and `POST /Jellywave/Like {SongId, StationId}` map that station's `/api/like` (403 off, 409 track ended, 429 too fast) into `LikeOutcome`; card R2a: the status is kept 5 s per song on air, and a like costs one of the caller's ten a minute | heart with the count, per track |
| Lyrics | `GET /Jellywave/Lyrics?songId=&stationId=` maps that station's `/api/lyrics/current` (403 off, 401 private station, 409 ended, 503 music server) into `ok/none/pending/off/unavailable/ended` (null lines skipped); shown only when `state.lyrics.enabled`; card R2a: kept 10 minutes per song (15 s while pending or failing) | collapsible panel, one fetch per track while open, plain lines (no timing-based highlight: the stream's buffering makes a client-side clock unreliable) |
| Today's programme | `GET /Jellywave/Schedule?stationId=`: `ScheduleCache` holds `/api/schedule` for five minutes per station, keyed by station id and origin (card L4; fetches serialized per station since card L5, so a station that does not answer holds up only its own schedule; stale copy of the same station on failure, retried after one minute; a new station URL drops the copy first, so a failing new station shows no schedule rather than the old one, and without a copy a failure is not retried for one minute either) and `BuildToday` turns the Sunday-first weekly grid into today's blocks in the station's time zone with the current one marked (unit-tested, falls back to UTC on an unknown zone; a repeated show or persona id keeps the first, null entries are skipped) | collapsible panel with hour ranges, show and host, shown only when the station publishes a schedule (card L5b); asked when the header is built, refetched every five minutes while open so the current block moves |

Layout (card 9, HI's option A, "fill the width"): the header is a CSS grid with four named areas,
`cover | body | request` on the first row and `panels` spanning the second, and each direct child
(cover, body, the request form, the panels block) is pinned to its area, so the markup order does not
matter. The columns are `11em`, `minmax(0, 1fr)` and `minmax(16em, 24em)`; the panels block is its
own `repeat(auto-fit, minmax(16em, 1fr))` grid, so Recently played, Today on the station and Lyrics
share a row when there is room. Two breakpoints only re-map the areas: at 60em the request box
drops under cover and body (`"cover body" "request request" "panels panels"`), at 40em everything
stacks and the cover becomes a full-width square. Inside the form the text field spans both of its
columns and the name and Send sit on one row (`minmax(0, 1fr) auto`).

Lifecycle (card 18):

- **Boot.** `boot()` runs once even if the plugin manager constructs the plugin twice: one observer, one
  `viewshow` listener, one set of `playbackManager` listeners.
- **Finding the page.** jellyfin-web 12.4 dispatches a bubbling `viewshow` on every view it shows
  (`viewManager.js` for legacy views, `Page.tsx` for React ones; `appRouter` itself listens for it on
  `document`), and the list controller sets `data-parentid` in its constructor, before that event. The
  script listens on `document` and looks for the channel's container inside the shown view. A
  `MutationObserver` on `body` stays as the fallback for what `viewshow` does not cover (the legacy
  drawer, late renders). Its callback does no document query: it arms one pending timer
  (`DOM_DEBOUNCE_MS`, 250 ms) and returns. The pass that timer runs first checks the session key (no DOM
  access), then reads the length of two live collections (`itemsContainer`, `customMenuOptions`) and
  stops when both are empty, so a page without a list or a drawer costs one length check per window.
  The header goes into the container whose `data-parentid` is the channel, not the first container
  found, because cached views keep other libraries' containers in the document. A document-wide search
  (the fallback pass, and the one after a feed reply) skips cached views, which `viewContainer.js` hides
  with `.hide`. The search after a feed reply is document-wide because the user may have moved to the
  channel view while the fetch was in flight, and that view's `viewshow` returned early. When the page
  already has a header (a cached channel view shown again), `state.header` is pointed back at it and
  repainted.
- **State key (cards L5, L5b, L8).** The header's state belongs to the session key below plus the header's station id
  and kind, which are the playing station's. When playback moves to another station the header is rebuilt (new element,
  new request form, fresh references), the feed, like, lyrics and schedule state go and the epoch is bumped, so a reply
  asked for the previous station is dropped; the feed reply must also name the station asked for, and a tick whose feed
  request was sent before the change stops there (the new header's own tick takes over). The station list belongs to
  the session alone and is read again at most once a minute while the header shows or a station plays, and on every
  playback start. When the playing station leaves the list (disabled, removed, refused), its next feed answers 404 (at
  most five seconds later): the header goes and the list is read again at once, without the back-off and without
  waiting for a DOM change; the header comes back only if the list still names the station and it still plays (a
  second 404 within 15 s is a refusal). A list refresh that no longer names it removes the header too; a plain stream's
  header leaves with its next title answer, a 404 (card L6; before L6 it left only with the list read, up to a minute
  later). An empty list removes the header. The title state (last answer, 30 s start, pending request, boundary timer)
  is reset with the rest.
- **Session key.** The state belongs to `ApiClient.serverId()` plus `ApiClient.getCurrentUserId()`
  (jellyfin-apiclient 1.11.0, which 12.3 and 12.4 bundle). The key is null when `accessToken()` is empty, which
  is how a sign-out shows (`getCurrentUserId()` is null then too; `serverId()` is not). Each tick, feed
  request, DOM pass, request submit and request poll compares that key with the stored one. On a change, or a sign-out (no access
  token), the script drops the station list, the selected station, the feed, like, lyrics, schedule, cover tags and
  back-offs, removes every header and drawer link, and revokes the portrait's blob URL.
- **Stale replies.** Each reset bumps an epoch counter. The feed, like, lyrics, schedule and portrait
  requests capture it, and a reply that comes back under another epoch is dropped. The request form
  captures the epoch its header was built in: a submit or a status poll checks the session first and
  stops when the epoch has changed, so a poll for one user's request never goes out under the next
  user. `fetchLike` also captures the song id it asked about, hides the previous track's heart until
  the new status arrives, and drops a reply once the feed's `SongId` or `likeSongId` has moved on, or
  when the reply names another song (the station answers about the track on air now). `sendLike` posts the
  captured id and ignores a reply once the shown heart is for another song, or when the reply names
  another song.

Tests: `Jellyfin.Plugin.Jellywave.Tests/js/stationPage.test.js` runs the script with plain `node` (no dependencies)
against a fake DOM, a fake `ApiClient` whose answers stay pending until a scenario resolves them, a fake
`playbackManager`, `events`, timers and clock: no header while idle or while an unlisted item plays, the header on a
station's playback start, a switch to another station with stale replies dropped, a pause becoming a stop after the
settle (card L11; never during a switch, never for another item or player, never for a non-station item), the bar class
and CSS, our narrow-width Stop, the player rebinding on `playerchange`, stop and an
unlisted item removing it, a plain stream's header (its shape, no SUB/WAVE route called, `javascript:` and `data:`
websites never becoming an `href`, markup in notes shown literally, switching SUB/WAVE ↔ plain both ways, a kind change
under one id, plain streams only), the "Now:" line (card L6: querying, the 30 s boundary from playback start with the
tick out of phase, the delayed text, the not-supplied text at once, failing samples and a failing route, markup in a
title shown literally, the bar's split at the first " - " and its repaint after jellyfin-web rebuilds it, no title
call for a SUB/WAVE station or an unknown kind or without a header, one request at a time, polling that stops on stop,
a switch or a stop and restart dropping a late title, a 403 refusing the page and a 404 re-reading the list, the bar
following the title after the header's view is evicted, an HLS stream, "Show what's playing" off), the header on opening the tile's page while a station plays, features the station
turns off staying hidden, a refused request keeping the form, the repaint for the playing station and none for a plain
stream, cached and hidden views of the tile (a stop removes every header, a switch leaves one, a hidden view gets none,
`viewshow` re-points to a cached view's header), 403 and 404, the state key per user, text only for station names, the
boot guard and the debounce, and card R2b's refusals (429 and 503 with and without `Retry-After`, an HTTP date, absurd
and unreadable values, a rejection without headers; the request text kept and Send freed; each poll retried after the
wait and not before, and twice as long when refused again in a row, up to 60 s; an uncertain request outcome never
re-sent; the portrait back after a 503, and a new DJ's refused portrait hiding the previous one; the heart's line; a
refused last poll ending the poll; no retry after a station, user or song change, or for a header that left the
document, and the armed timers cleared). A flag silences the fake `MutationObserver` so the event and tick paths are proved without
the DOM-change fallback. CI runs it. `StationPageScriptTests` checks the embedded script: markup comes only from three
fixed strings (the two headers and the drawer link), the one `href` set from station data sits behind `safeWebsite`, and
every station call names its station.

Known limits: the DOM selectors (`.itemsContainer[data-parentid]`, `.nowPlayingBar`, `.card`) are jellyfin-web
internals and can move between releases; when one moves, the page is meant to do nothing rather than break the client. The
Android app wraps the web client and needs a force-close to refetch `index.html` after the plugin is installed or
updated (an update to 0.5.0.0 brings the header that follows playback); until then it runs the script it cached, which
the absent-`stationId` rule keeps working (see "Station per route"). The header follows `isPlaying`, so any moment
when `isPlaying` is false for the playing station's item (a switch between items, or a transcoding retry after a
playback error) removes the header and builds a new one; the new one starts empty and an in-flight request poll's
status for the old one is dropped (the request itself still reaches the DJ). That is consistent with "the header goes
on stop".

## Station route authorization (card 13)

Jellyfin's default policy (`DefaultAuthorizationHandler`, 12.3) lets any API key through unrestricted and
never looks at channel visibility, so plain `[Authorize]` is not enough for the station routes. Every
user route on `JellywaveWebController` (Stations, Feed, Avatar, Like GET and POST, Schedule, Lyrics, Request POST,
Request/{id}) also carries `[StationAccess]`, an `IAuthorizationFilter` attribute. It runs after
Jellyfin's authentication and before model binding, so a refused caller's body is never read, and asks
the `StationAccess` singleton, which refuses in this order:

1. `Jellyfin-IsApiKey` is anything but `False` (Jellyfin sets it with `bool.ToString` on every request).
2. `Jellyfin-UserId` is missing, unreadable or empty (API keys carry an empty id), or names no user
   (`IUserStore.GetUser`).
3. The user's block list names an earlier tile name's id (`PreviousChannelBlocked`, the rename bridge
   in "Channel identity").
4. The channel name collides with another plugin's channel (`NameCollision`, see "Channel identity"), or
   the channel entity is missing (`LiveItemFinder.Resolve`; a fresh install, or a rename before the
   channel refresh): fail closed with **404** instead of 403, so the page keeps its short retry.
5. Jellyfin's own `Channel.IsVisible(user)` is false: block list, enabled list or `EnableAllChannels`,
   then the parental check (tags and rating). Administrators are not exempt, same as in Jellyfin.

Every other refusal is a plain 403 with no `X-Application-Error-Code` (jellyfin-web logs a user out on a
403 that carries `ParentalControl`); each refusal writes one Debug line with the rule. An allowed caller's
user id and the resolved `Channel` are left in `HttpContext.Items` for the action, so `Feed` does not
look the channel up a second time. The user entity is not passed on. The controller reads the bag only through
`CallerId()`, `CallerChannel()` and `CallerStation()`, each of which throws when its filter did not run. The assets stay anonymous and `WebUi` stays admin-only; a reflection
test fails when an action is neither anonymous, admin-only nor filtered.

### Station per route (card L5)

One gate for the whole tile (HI): `[StationAccess]` stays the channel gate and decides first, exactly as above. Every
station route but the list then serves **one listed SUB/WAVE station**, found in one place:

- `[StationRoute]` (`StationRouteAttribute`) is an action filter, so MVC runs it after every authorization filter
  (`[StationAccess]` included) and after model binding: a caller the gate refused gets its 403 or 404 before any body
  is read or any station id looked at. It reads the bound `stationId` query parameter (GET routes) or the
  `IStationSubmission` body's `StationId` (POST: Like, Request) and asks `StationResolver`, the one service that turns
  an id into a station. It leaves the resolved `StationSource` in `HttpContext.Items`; the actions read it only through
  `CallerStation()`, which throws when the filter did not run, and never pick a station themselves. The controller does
  not hold the resolver: the station list and the image tags come from `StationList`, which hands out ids, names, item
  ids and tags only, so the filter's station is the controller's only way to a station URL.
- **Only a listed SUB/WAVE station resolves**: `StationSources.Publish` (enabled, passing its rules, first of a
  duplicate id), kind `SubWave`, compared ordinally (exact case, no trimming). An unknown, disabled, refused,
  duplicate-shadowed or plain-stream id answers **404**, and no station is asked. With no configuration (the plugin not
  ready) every route answers 404.
- **An absent `stationId` means the primary station** (`StationSources.Primary`: the first enabled SUB/WAVE station by
  `SortOrder`, only while listed, no fallback). This keeps an older cached `ui.js` working: the Android app keeps
  `index.html` until a force-close and browsers cache the assets for a day, and those pages send no station id. With no
  listed primary, such a request is 404 (the old page then removes its header, card 18). ASP.NET Core binds an empty
  or blank query value as absent (`ConvertEmptyStringToNull`), so `?stationId=` is the primary station too; in a JSON
  body an empty string stays text and is 404. A POST route reads the body only; a `stationId` in its query is not bound.
- `GET /Jellywave/Request/{id}` carries `[StationRoute(FromReceipt = true)]` and takes no station id: the station
  comes from the receipt (below), never from the caller.
- `GET /Jellywave/Title?stationId=` (card L6) carries `[StationRoute(PlainStream = true)]`, the plain-stream variant:
  only a listed plain stream resolves (`StationResolver.ResolvePlainStream`; a SUB/WAVE, disabled, refused, unknown or
  missing id is 404), and the filter leaves its id alone (`PlainStation`) under `PlainStationItem`, read only by
  `CallerPlainStation()`. It is the only route with that flag (`StationRouteCoverageTests`).
- `GET /Jellywave/Stations` is the one station route without `[StationRoute]`: it takes nothing from the caller and
  lists every listed station in grid order (card L8: both kinds, through `StationResolver.ListedStations`, which no
  route resolves with) with id, name, kind, live item id and image tag (a SUB/WAVE station's withheld under the L4
  rule: while the station's state or the item's applied cover is another origin's; a plain stream's is its logo's),
  its notes, and for a plain stream its genre, website and format. Never a stream or station URL (a test serializes
  it); a plain stream's id still answers 404 on every other station route. `StationList` reads genre, notes and the
  website only through `StationText`, which cuts them to 60 and 280 characters and applies the website rule
  (`StationUrls.WebsiteLink`: absolute http or https with a host that IDNA accepts, served as punycode
  (`Uri.IdnHost`), no userinfo (`@` before the path), at most 2048 characters as typed and as escaped; a refused
  website is dropped and the station stays listed). The server never requests a website.
- The reflection test (`StationRouteCoverageTests`) requires: every filtered route but `Stations` carries
  `[StationRoute]` and `[StationAccess]`; GET routes take a `[FromQuery] string stationId`, POST routes one
  `[FromBody]` `IStationSubmission` and no query id; only `RequestStatus` reads the receipt and takes no station id;
  `StationRoute` is an action filter, not an authorization or resource filter; no constructor parameter or field of
  the controller is, or hands out through a public property, field, method result or `out` parameter (transitively,
  every method followed), a `StationSource`, `StationDefinition`, `PluginConfiguration`, `Uri` or the resolver (card
  R2a: no path is listed any more, since the controller holds `StationCalls` instead of `StationClient`, the probe's
  redirect target is out of its reach); and the controller source
  names none of the old primary-station or static configuration paths. `StationRouteIlTests` reads the IL of the
  controller and of its compiler-generated nested types (async state machines, lambdas): it references no `Plugin`,
  `PluginConfiguration`, `StationDefinition`, `StationSources`, `StationUrls`, resolver, `Uri`/`UriBuilder`, service
  locator or `RequestServices`, `ProbeStreamAsync`, `StationKey.Origin` or reflection; only `CallerStation`,
  `BaseUrlOf`, `KeyOf` and `NameOf` open a `StationSource`; only the parameterless, value-returning `CallerId`,
  `CallerStation`, `CallerPlainStation` (card L6) and `CallerChannel` touch `HttpContext.Items`: reading it,
  replacing it (`set_Items`), or any token,
  generic argument or constructor of `IItemsFeature`/`ItemsFeature` (so no new item feature can be set either).
  Elsewhere in the controller a station handed out as `object` therefore cannot be written into the bag and picked up
  by `CallerStation` (card L7, the L5 review's S-a). `StationList`
  reads station ids, names and keys, never a URL. A control class in the test holds one bypass per method and the scan must flag each.
  Card S-b adds an allow-list on top of these deny rules (`StationRouteAllowList`, checked by `PluginCalls`; 42 data
  types and 27 members since card R2a replaced the six `StationClient` station calls with the six `StationCalls` ones,
  added the data type `StationCall<>` and replaced `RequestReceipts.Record` with `Reserve`, `Complete`, `Cancel` and
  `StationRequestIdOf`). Every plugin method, constructor or field the controller, its nested types and `StationList`
  reference must be on a member line with a one-line reason, or be an instance auto-property accessor (proven by IL to
  touch nothing but its own backing field) or the parameterless constructor of a listed data type; otherwise the test
  names the member and the calling method. Every plugin type the framework builds or reads for that code must be a
  listed data type: one that appears there as a type token (`typeof`, `isinst`, `box`), as a generic argument
  (`Deserialize<X>`, `ActionResult<X>`) or as a parameter or return type of a scanned method (constructors are exempt:
  their parameters are the DI services the reflection test walks). Each listed member is followed through the plugin's
  code fully transitively: calls and method references, the methods of compiler types it touches (lambdas, closures,
  async state machines, iterators), static constructors of the plugin types it touches, every plugin implementation or
  override of a plugin interface or virtual method it calls (generic types included), the virtual methods of each
  plugin type it constructs, and the constructors, virtual methods and accessors of each plugin type it hands to other
  code as a type token or generic argument (a plugin value type counts as handed whenever it is touched). Each listed
  data type is walked whole (every constructor, the static constructor, every method and accessor, also of plugin base
  types), so a property initializer or a getter only the serializer calls is read. The walk stops at framework code and
  at the scanned types themselves (the controller's calls into `StationList` are covered by the `StationList` rule).
  Sinks are `Plugin`, `PluginConfiguration`, `StationResolver`, `StationSources`, a service locator, `HttpContext.Items`
  or the item feature, `ProbeStreamAsync`, reflection (`MemberInfo` and its kinds, type and assembly enumeration,
  `Delegate.CreateDelegate`, `RunClassConstructor`, compiled expressions), dynamic code, a member of `StationDefinition`
  or `StationSource` (labelled per member, so `StationText.Notes` names exactly `StationDefinition.get_Notes`), either
  type as a token, result or generic argument, and a static field whose type is or holds a sink type (a cache that could
  hand back another station). A member line may reach only the sinks it names (`Type.*` names all of a station type's
  members; today only `StationResolver.ListedStations`, `StationList`'s source, uses it); a data type may reach its own
  members and only the sinks its line names (`StationSource` names its definition and the definition's id). A line
  nothing uses, or a named sink no longer reached, fails too. Controls prove an unlisted static, extension and instance
  helper, constructor and field; a data type's hand-written getter and static auto-property; a plugin type used as a
  type token, generic argument, parameter or return type; a listed data type whose initializer or unused computed getter
  reads the configuration; and a listed helper that reaches the configuration two calls down, through a plugin
  interface, through a generic implementation or override, through a constructed object's or a struct's `ToString`,
  keeps stations in a static field, reads a station field its line does not name, uses reflection through
  `CreateDelegate` or the type list, or writes the item bag. Not covered: see THREAT-MODEL "Limits of the IL scan". A real MVC pipeline on loopback (`StationRoutePipelineTests`) proves the order: an API
  key or a hidden user gets 403 whatever station id or unreadable body it sends, an allowed caller's unlisted id is
  404, and no station is asked for any of them.

Listener requests: the station sees this server as one caller, so the plugin keeps the user binding
itself. `RequestReceipts` (a singleton, in memory, under a lock) holds one receipt per accepted request. Card R2a: the
receipt is the server's own opaque token (128 random bits as 32 hex digits, `RandomNumberGenerator`), never the
station's id; it maps to the submitting user, the station's key (id and origin, since card L5) and the station's request
id, so two stations that hand out the same request id, to two users or to one, never meet. The page gets the token as
`RequestId` and polls `GET /Jellywave/Request/{token}`; the station's id stays on the server. A slot is reserved before
the request is sent (`Reserve`, a placeholder that polls as unknown and lapses after a minute) and filled when the
station accepts (`Complete`, with a valid station request id) or given back (`Cancel`: a refusal, an answer without a
receipt, a failure, the limiter's refusal). The slot is reserved only after the caller's budget has room (fix round: the
route hands `StationCalls.SubmitRequestAsync` a callback that reserves it between the budget and the limiter), so an
over-budget send is a plain 429 and never evicts the caller's own oldest receipt. Receipts live 30 minutes; at most 20 per user, and 1000 in all.
A user at 20, or any user while the store holds 1000, gives up their own oldest accepted receipt; a user who owns none
when the store is full is refused (503, `Retry-After: 60`) before anything is sent to the station, with the whole budget
reservation given back, and another user's receipt is never evicted. Expired receipts are pruned on every reserve. `GET /Jellywave/Request/{token}` answers 403
when the receipt belongs to someone else, and "unknown" ("The station lost track of that request.") without calling any
station when there is no live receipt, or when the receipt's station is no longer listed or now has another origin (its
base URL changed under the same id). The owner's poll goes to the receipt's station, with the station's request id
behind the token (`StationRequestIdOf`, the owner and the receipt's station key only), even after the primary station
changed. Receipts do not survive a restart: a request pending across one shows that same message. The free-text name
still goes to the station unchanged (HI decision); accounting is the receipt plus the Information line with the
station's request id, the receipt token, the user id and (since card L5) the station id, never the name or the text.

A 403 versus "unknown" reveals whether a token exists here; it is 128 random bits, so a caller can only learn about
tokens it already holds.

### Station call budget, limiter and caches (card R2a)

Every user-driven station call (like status and like, lyrics, portrait, request and request status) goes through
`StationCalls`, the one service the station routes call for a station on a user's behalf; the controller holds no
`StationClient` (the allow-list names the `StationCalls` members instead). Each call is checked, in this order:

1. **The caller's budgets** (`UserBudgets`, HI decision D10), per Jellyfin user in sliding windows: 60 station calls a
   minute of every kind together; song requests one a minute and four an hour across all stations; likes ten a minute.
   A call is checked against every window it counts in and reserved in all of them in one step under one lock, before
   the station is called; over any window the route answers **429** with `Retry-After` (whole seconds until the fullest
   window frees a slot); `[ApiController]`'s client-error mapping adds a ProblemDetails body (`status`, `title`,
   `traceId`), which the page does not need. What is given back (fix round, MI decision):
   - **everything** for a call that never left (no connection, no name, before anything was sent), for the limiter's
     own 503 and for a full receipt store;
   - **only the kind's own windows** (request: one a minute and four an hour; like: ten a minute) for a station's own
     refusal (its 4xx, its 429 included, or its 503); the general 60-a-minute slot stays spent. SUB/WAVE answers 409 to
     lyrics for any song id not on air and to a like for a stale song id, so before the fix round one user could loop
     made-up ids with no budget at all;
   - **nothing** for an uncertain outcome or any other answer (a timeout, a connection lost after sending, a 2xx, 3xx
     or other 5xx).

   The station's own lower limit still wins: it refuses on its own. Counts are in memory and reset on restart (accepted); users with nothing left in any window are dropped every
   ten minutes.
2. **The shared limiter** (`StationCallLimiter`, fix round: raised from 2 and 8): at most 4 calls in flight per station
   and 16 in all, and of those at most 4 portrait fetches (each may buffer up to 4 MB, so portraits are the memory
   peak). Nothing waits: a call over any limit is refused at once (**503**, `Retry-After: 2`, the same ProblemDetails
   body), and its budget reservation is given back in full. The station slot is checked first and the total (and
   portrait) slot taken only with it, under one lock, so callers stuck on one slow station hold at most its four slots
   of the total and never block another station. A station is counted by `StationKey.Origin`, its whole parsed base
   URL: two station ids with the same base URL share its slots, two base URLs on one host do not. The permit is held until the client has read the answer in full
   and disposed it (`StationClient` reads every body before it returns) and is given back on every exit: an answer, the
   caller's cancellation, any exception. The poller, the title sampler, the schedule and "Reload status" keep their own
   paths and bounds.
3. **The station**, through `StationClient` as before. A timeout or a failed connection is "no answer", which the
   routes report as before (like status off, "The station did not answer."); the caller's own cancellation propagates.

**Caches** (`StationCache<T>`, inside `StationCalls`, so they are served only after the route's access gate and station
resolution, with the filter's station key). Each answer is keyed by the station key (id and origin: a base URL changed
under the same id never serves the old station's answer) and the item, and bounded by entries in all, entries per
station and total bytes (an estimate per answer; the entry that expires soonest goes first, the oldest among equals, so
short-lived error entries go before kept answers; an answer larger than the whole budget is not kept). Concurrent misses for one key share one fetch: the first caller is admitted (budget, permit) and starts it, the
others wait for its answer with no budget or permit of their own; a waiter that gives up stops waiting, and when every
waiter has given up the fetch is cancelled. A refusal at admission goes to that caller only and is never kept. Only a
miss costs the caller a budget slot.

| Cache | Key | Kept | Bounds |
|---|---|---|---|
| Lyrics | station, song id | 10 min for lines or "none"; 15 s for "pending", any other status and no answer; a 409 ("not on air", which the station answers for any made-up song id) is not kept at all | 64 per station, 512 and 4 MB in all |
| Portraits | station, persona id | 10 min; 15 s for no usable image or no answer | 32 and 8 MB in all |
| Like status | station, the song id the poller last heard there | 5 s; an answer for another song (the station moved on) is not kept; without a heard song it is asked every time | 4 per station, 256 and 256 KB in all; the station's other songs are dropped when the song changes, and the song's entry after a like the station took |
| Request status | station, receipt token | 8 s (any answer) | one per receipt: 1000 and 1 MB in all |

The like status is shared by every user on purpose: SUB/WAVE keeps likes per caller IP
(`controller/src/routes/likes.ts`), and the whole server is one caller, so every user sees the same status anyway.

**The page's use.** The station page's busiest minute (measured in `stationPage.test.js`, "R2a a busy minute of page
use": lyrics panel open with lyrics "pending" all minute, a song change, one like, one request "pending" all minute) is
35 page requests: 21 request status polls (every 3 s), 9 lyrics (every 8 s while pending, and the new song), 2 like
statuses, 1 like, 1 request, 1 portrait. Through the caches that is 17 station calls for that user
(`StationCallsCacheTests`): 7 request status, 5 lyrics, 2 like status, 1 like, 1 request, 1 portrait, under a third of
the 60 a minute. A minute of plain listening is one like status and one lyrics call per song. The request status
cache's 8 s is what keeps it there; at 5 s the same minute would be 21 (measured).

The page: a 403 or a 404 from `Jellywave/Stations`, or a 403 from `Jellywave/Feed` (ApiClient rejects with the fetch
`Response`, so the script reads its `status`), drops the station list, the feed, every header and the drawer link
(card 18 added the 404 case, so a renamed channel does not leave a stale header). After a 403 the next list request
waits `FEED_FORBIDDEN_RETRY_MS` (five minutes) instead of the usual 15 seconds; a 404 (no channel yet) keeps the 15
seconds. A 404 from `Jellywave/Feed` (card L5: the header's station is no longer listed) drops the header and reads the
list again at once, skipping the back-off; only the list's own 403 or 404 refuses the page. A second feed 404 within
15 seconds (the list still names a station whose feed is gone) counts as a refusal, so the two cannot loop. A refused
page arms one retry for the end of its back-off (a page pass that asks for the list), so an idle page (no DOM change,
nothing playing) comes back once allowed; refused again, it retries once per back-off. The other routes are only called once a feed exists; a 403 on a request submit or poll
says "You don't have access to this station." instead of a connection error.

**This server's refusals (card R2b).** The station routes the page calls for the user (like status and like, lyrics,
portrait, request and request status) can answer 429 (over the user's budget) or 503 (busy, or the receipts are full)
with `Retry-After` (see "Station call budget, limiter and caches"). Nothing reached the station then. jellyfin-web 12.4
bundles jellyfin-apiclient 1.11.0, which rejects with the fetch `Response` for any status from 400 (it sets
`enableAutomaticNetworking` off, so nothing, a POST least of all, is sent again by ApiClient itself, and
`appRouter.onRequestFail` acts only on a 403 for parental control), so the page reads `status` and
`headers.get('Retry-After')`: delta seconds (what this server sends) or an HTTP date (a proxy's); missing, unreadable
or negative means `RETRY_AFTER_DEFAULT_S` (10 s), and the wait is clamped to 1 s..3600 s (the request budget's hour is
the longest the server sends); from 120 s it is said in minutes. Jellyfin 12.4's CORS policy exposes no headers, so
`Retry-After` is readable only from the server's own origin, which is where `ui.js` runs; elsewhere the 10 s default
applies. What the user acted on says "Too many requests." or "The server is busy." and "Try again in N s.": the
request box (the typed text stays, Send is free at once) and the heart (free at once, its old status kept; the text
is its title and, for touch, TV and the Android app, a line under the actions, both gone when the wait ends or the
station answers a later like). What the page polls is asked again after the wait and not before: the like status
(its `likeSongId` stays, so ticks do not ask sooner), the lyrics (the panel says "Asking again in N s."; reopening it
does not ask sooner), the portrait (not marked failed: `data-src` stays until the wait ends, then the same portrait is
asked again, as often as it is refused; meanwhile it is hidden and a previous DJ's blob is revoked, so no old face sits
next to the new name; any other failure still hides it) and the request status poll, which goes on ("Checking again in
N s.", Send stays busy; a refused poll counts as one of its 30 tries, and a refusal at the last try ends it as an
answer would) instead of stopping with "Lost contact while waiting.". Fix round: the same thing refused again in a row
(a song's like status or lyrics, a portrait URL, a request) waits at least twice its last wait, that doubling capped at
`RETRY_BACKOFF_MAX_S` (60 s), so a busy 503's 2 s becomes 2, 4, 8 … 60 s; Retry-After itself is never shortened, and
an answer, another song, portrait or request, or a reset starts afresh (`state.backoff`). A song request is never sent
again by itself: a 429 or 503 means nothing was sent, so the listener may press Send after the wait; without a clear
outcome the page says it may have arrived: "Could not reach the server. The request may have been sent; it is not sent
again by itself." when nothing answered (a network failure, a timeout), "No clear answer from the server. The request
may have reached the station; it is not sent again by itself." for another 5xx (the server or a proxy answered), and
"The server did not take the request." for another 4xx. Every retry is armed through `later(epoch, …)`, only for the
current epoch: its timer is kept in `state.retries`, which `resetStation` clears (another station, user, server or a
sign-out), and when it fires it checks the session and the epoch again (a user change between ticks), then the song or
portrait it was for (still the one shown, its header still in the document); a song change in between asks for the
new song instead. The request status poll's 3 s timer goes through the same path. The texts go in through
`textContent`.

## Outbound requests (card 14)

Every request the plugin itself makes to a station or for one (feed, state, schedule, likes, requests,
lyrics, covers, avatars, plain-stream logos, plain-stream title samples, the add-station probe) goes through
`StationClient` (the user-driven ones first through `StationCalls`, card R2a: budgets, limiter, caches), and every
`StationClient` call goes through one
private `CreateClient`, which asks for the plugin's own named client `Jellywave` and
sets the 8 s budget and the body cap (256 KB for JSON, 4 MB for images).

**Own client, redirects refused.** Jellyfin's `NamedClient.Default` keeps `SocketsHttpHandler`'s
default `AllowAutoRedirect = true`, so a station answer could send the server to any host or port
(SSRF). `PluginServiceRegistrator` registers `Jellywave` with `AllowAutoRedirect = false`,
`AutomaticDecompression = All`, `MaxResponseDrainSize = 0` (card L6: an unread body is never drained, the connection is
closed; every call reads its body in full or does not want it) and the User-Agent `Jellywave/<assembly version>
(+https://github.com/b3p3k0/jellywave)` (card L9: a contact URL, as Wikimedia's User-Agent policy asks; a starter logo is
on Commons). ffmpeg's relay is Jellyfin's and sends its own User-Agent. `AddHttpClient`
comes from the ASP.NET Core shared framework the server already loads, so no package is added and
the zip stays the DLL, `meta.json` and `thumb.png`. Jellyfin's own clients also set its dual-stack
`ConnectCallback` (`HttpClientExtension.OnConnect`, which honours the server's IPv6 setting); it lives
in a server assembly the plugin does not reference, so the plugin uses the default connect. The
SUB/WAVE station is usually a LAN host; a name that resolves to an unreachable IPv6 address first costs part of the
8 s budget before IPv4 is tried. A 3xx is the station's answer, not a hop, for those requests. "Redirects refused"
covers this client only. Playback does not go through it, and follows redirects: ffmpeg fetches the stream for
client playback (its http input follows up to 8, `max_redirects` in libavformat's `http.c`, which Jellyfin does not
set), and a static request (`/Audio/{id}/stream?static=true`) goes through Jellyfin's `NamedClient.Default`
(`AudioHelper` → `FileStreamResponseHelpers.GetStaticRemoteStreamResult`). The title sample (card L6) does
go through this client: a stream URL that redirects plays but shows the delayed text, and its Warning names the
redirect. That is an accepted risk
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
- The admin probe (`POST /Jellywave/Probe`, card L3, see "Add-station probe") answers a 3xx with "The address
  redirected to <target>; use that address" and the target in `Redirect`, which the page's "Use it" button puts into
  the URL field. It is suggested only when it passes the whole-URL rule the pasted address had to pass (http or https,
  a host, no userinfo, no fragment, not `.mpd`); otherwise the probe says the address redirected "to an address the
  plugin does not accept" and shows none of it. The probe never follows it. Only this admin-only route shows it. (Until
  L3 the probe was `GET /Jellywave/Station?baseUrl=`, which suggested a base URL; it is gone.)

**URL rules** (`StationUrls`, pure functions; a station whose URL is rejected is not listed, and a refused non-empty
base URL says why, see below; the plain-stream rule is under "Configuration surface"):

- Base URL: absolute http or https, with no userinfo (not even an empty `@`), no query and no
  fragment. The value is checked as typed, before the trailing slash is added (otherwise
  `http://host/radio?x=1` would turn into a query of `?x=1/`). The station table shows a refused value as
  the station's not-listed reason.
- A refused non-empty base URL (an install saved before card 14 as `http://host:7700/?`, or with
  `user:pass@`) is not shown as "not configured". `StationUrls.BaseUrlRefusal` returns a fixed reason
  (not an absolute URL, not http(s), credentials, query, fragment) that never repeats any part of the
  value. `GET /Jellywave/Health` carries it as `StationUrlRefusal` and `StationUrlStatus` ("the SUB/WAVE
  station URL is set but not allowed: <reason>"; "no SUB/WAVE station URL configured" without one, also with plain
  streams only) for the primary station, and every station's `NotListedReason` carries it as
  "its base URL is not allowed: <reason>". The configuration page shows it in the station's status cell, on load and
  after each save; the save itself is not blocked. The channel's listing warning names the reason too.
- Stream path: leading slashes are trimmed, so `//x` stays a path under the base. A path that still
  parses as an absolute URI (it has a scheme) is refused. Then the built URL must equal the base on
  scheme, host, effective port and userinfo (`Uri.Compare(..., SchemeAndServer | UserInfo, ...)`)
  and stay under the base's path, with no `..` segment. The authority check is load-bearing, not a
  backstop: on Linux `\/radio.example:8000/stream.mp3` is not an absolute URI (a `file:` host cannot
  carry a port), yet `Uri` resolves it against the base as a network path to the same host on
  another port. Only the `Uri.Compare` step refuses it, and a host-only compare would let it through
  (a test fails when the compare is reverted). A query or fragment in the stream path stays allowed.
- Plain HTTP stays allowed: SUB/WAVE normally runs on plain HTTP on the LAN, and many radio streams are plain HTTP.

**Image checks** (`ImageSniffer` and `ImageStructure`, covers, avatars and plain-stream logos). The declared content type must be missing
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

**Animated images refused** (card R2a, HI decision D9). Only the first frame's header is sized above, so before R2a a
GIF, an animated PNG or an animated WebP within 4096 × 4096 and 4 MB could still carry many frames, each one decoded by
Jellyfin's image processor (a small file can hold hundreds). An accepted image must now also be one still image with a
well-formed structure, checked by `ImageStructure` with a bounded walk of the structure, not by counting byte
patterns: it reads block and chunk headers only, never pixel data, every step moves forward by at least one byte and
every read is bounds-checked, so the walk is linear in the bytes (at most 4 MB).

- **GIF:** the logical screen descriptor, its global colour table, then every block to the trailer: extensions and
  image data skipped sub-block by sub-block; exactly one image descriptor (a second one is animation), inside the
  logical screen (a frame the screen size does not bound is refused), with an LZW code size of 2 to 8. No trailer, a
  sub-block running past the end or an unknown block is refused.
- **PNG:** every chunk from the signature to `IEND`: `IHDR` first, once, 13 bytes; at least one `IDAT`; any `acTL`
  (before `IDAT` it makes an APNG; anywhere else it contradicts a still image), `fcTL` or `fdAT` is refused; chunk names
  must be letters, and a chunk running past the end or no `IEND` is refused. Bytes after `IEND` are not read.
- **WebP:** every RIFF chunk inside the declared RIFF size (a size past the end of the data is refused, every chunk and
  its pad byte must fit). A simple file (`VP8 ` or `VP8L` first) holds that one image chunk and nothing of the extended
  format (`VP8X`, `ALPH`, a second image); an extended file (`VP8X` first) must have the animation flag clear, no `ANIM`
  or `ANMF` chunk, and exactly one image chunk whose bitstream size is the canvas size.
- **JPEG** (fix round): a progressive JPEG is decoded once per scan, the same cost as a frame. After SOI the markers
  are walked: segments by their length, and after each start of scan (SOS) the entropy-coded data byte by byte up to
  the next marker (`FF 00` is a stuffed byte, `FF D0`–`FF D7` a restart marker, `FF FF` a fill byte). More than
  `ImageStructure.MaxJpegScans` (100) scans is refused (real progressive JPEGs have about ten), and so is a segment
  length below 2. What decoders tolerate stays accepted: a file cut short (its scans so far are counted) and stray
  bytes between segments after the frame header (the header sniff still refuses them before it, as before).

A refused image counts as "no art", like an unknown type. The byte and dimension limits above are unchanged.

## Add-station probe (card L3)

The configuration page's "Check" button posts `{ "Url": "<pasted address>" }` (plus `"BaseUrl"`, the stored base, when it
re-checks a SUB/WAVE station) to `POST /Jellywave/Probe` (admin only:
the controller's `RequiresElevation`; a POST keeps a stream URL's query out of access logs). `StationProbe` answers
HTTP 200 with `StationProbeResult`: `Ok` (a stream the plugin can serve was found), `Kind` (`SubWave` or `Stream`), the
checked `StreamUrl`, a SUB/WAVE station's `BaseUrl` and `StreamPath`, the format (`Codec`, `Container`, `BitrateKbps`,
`SampleRate`, `Channels`), `Name`, `Tagline`, `Redirect`, and `Lines` (level `ok`, `warn`, `error` or `redirect`, and a
text). PLAN diagram 10, step by step:

1. **URL rule.** `StationUrls.TryParseStreamUrl` (http or https, a host, no userinfo, no fragment, a query allowed),
   and not a `.mpd` path. A refused address gets "URL not allowed: <fixed reason>" and no request. A `.pls` or `.m3u`
   address is allowed here; the probe resolves it.
2. **One headers-only request.** `StationClient.ProbeStreamAsync`: `GET` with `Icy-MetaData: 1` through the plugin's
   no-redirect client (8 s to the headers), `HttpCompletionOption.ResponseHeadersRead`. The body of anything that is
   not a playlist is never touched: the response is disposed, which closes the connection (no drain since card L6), so
   an endless stream costs one round trip. Tests: every audio answer in the probe tests has a body that throws when read or buffered,
   and a socket test serves an endless MP3 stream through the registered client and must finish in seconds.
3. **What came back.**
   - 3xx: the redirect suggestion above ("Outbound requests"); nothing else runs.
   - Non-2xx: "not reachable: HTTP <n>".
   - HLS first (a `.m3u8` path, or `application/vnd.apple.mpegurl` / `application/x-mpegurl`): a stream, codec assumed
     AAC, container `hls`, with a warning that the codec is assumed. Its playlist is not read.
   - Playlist (a `.pls` or `.m3u` path, ignoring case, or `audio/x-scpls`, `audio/x-mpegurl`, `audio/mpegurl`): the body
     is read, at most `StationClient.MaxPlaylistBytes` (256 KB; a declared larger length is not read at all, a chunked
     one stops one byte past the cap) under its own 8 s, and a UTF-8 byte order mark is dropped. The first absolute http(s) entry wins: blank lines, `#`
     comments, `[section]` lines, relative entries and `.pls` keys other than `FileN=` are skipped. The entry must pass
     the same rule as step 1 (it is then shown: "Playlist read → <entry>"); it is checked once with the same
     headers-only request, and if that answer is a playlist too the probe stops ("the playlist points to another
     playlist"). No second body is read.
   - `audio/mpeg` → mp3; `audio/aac`, `audio/aacp` → aac (no warning since L10: every station is relayed, L0).
   - Anything else is not audio: perhaps a SUB/WAVE station's own address (its web player) was pasted, see step 4;
     otherwise "not an audio stream".
   - `icy-br` (leading digits, 8–1536), `icy-sr` (8000–192000) and `icy-name` are the station's text: control
     characters dropped, whitespace collapsed, cut at 120 characters (`StationProbe.Clean`, the now-playing rule).
   - Card L8: `icy-genre` and `icy-description` are cleaned the same way and cut at 60 and 280 characters
     (`StationText.Clean`), and come back as `Genre` and `Notes`. `icy-url` comes back as `Website`: cleaned, `http://`
     put in front when it has no `://` (stations often send a bare host), then the website rule
     (`StationUrls.WebsiteFromIcyUrl`); a longer value than 2048 characters is refused, never cut into another link.
     The probe never requests it. The dialog fills each field like the name.
4. **SUB/WAVE detection** through `StationClient.GetStateAsync` (the card 14 JSON read: 8 s, 256 KB): at the stored base
   URL of the station being edited (only when it passes the base URL rule and the address lies under it: same scheme,
   host and port, path below; so a stream path nested deeper than its directory stays SUB/WAVE on a re-check), at the
   stream's origin, then the directory it sits in, and, for an address that was not audio, the address itself as a base URL.
   The first `/api/state` that answers with a `station` block makes the station `SubWave`: `BaseUrl` is that candidate
   (no trailing slash), `StreamPath` the stream's path and query under it, verified by building it back through
   `StationUrls.TryGetServerStreamUrl` (the card 14 rule); name from `/api/state`, tagline and format from
   `/api/now-playing`'s `dj` and `stream` blocks. For a pasted base URL the feed's
   `stream.mount` (default `/stream.mp3` when blank) is the stream, built under the base by the same rule, refused when it
   fails the whole-URL rule (a `#fragment`, `.mpd`), and checked once, headers only. If that stream redirects, the line
   names the hop ("the station's stream at <stream> redirected to <target>", the target only when it passes the
   whole-URL rule) and offers no "Use it": the station chose it, not the admin. A mount that is not audio is "its stream
   address is not usable". The format and its warnings are written after the feed has replaced what the headers said,
   so a stream whose headers say AAC but whose feed says MP3 shows MP3 and no AAC warning. A redirect, an error status,
   non-JSON or JSON without `station` at a candidate means "not here". Listener auth on
   the station is a warning. A stream whose SUB/WAVE station cannot hold it under its base stays a plain stream.
   Card L11: the line for a plain stream is "Radio stream." (SUB/WAVE is named only when one is found, or when its check
   ran out of time or found a station that cannot hold the stream; no line says "added", since nothing is added before
   "Add to list" and Save).
5. **Failures are categories**, never exception text: "not reachable: timed out", "not reachable: could not connect",
   "not reachable: the host name was not found", "not reachable: the https connection could not be secured", "the
   server closed the connection before it answered", "not reachable: HTTP <n>", "the answer could not be read; the
   connection broke", "unexpected error; see the server log" (logged at Error). A Shoutcast v1 server answers `ICY 200 OK`, which .NET 10's client refuses with
   `HttpRequestError.InvalidResponse` and the message "Received an invalid status line: 'ICY 200 OK'." (checked against
   a socket; a socket test pins it). The probe names it ("the server answered in the old Shoutcast format…; try the
   station's Shoutcast v2 or Icecast http:// address") when the message carries `'ICY `; any other invalid response is
   "the server's answer is not valid HTTP".
6. **What the response may carry:** the admin's own address (and parts of it: the origin as a base URL), a redirect
   target or playlist entry that passed the whole-URL rule, a stream path the card 14 rule built, and cleaned station
   names. A test serialises results for refused redirects, refused entries and connection errors and checks that no
   credential or exception text appears.

A probe makes at most eight requests (address, playlist entry, four `/api/state`, the feed, the mount), each bounded
as above; `StationProbe.ProbeBudget` (30 s) cancels the rest, which then reads "timed out" (or, when only the SUB/WAVE
check ran out, a plain stream with a note). A cancelled page request stops the probe and is not logged.

## Poller health (cards 10 and L4)

`PollerHealth` is a singleton the poller writes and `GET /Jellywave/Health` (admin) reads: whether the loop runs, and
per polled SUB/WAVE station (keyed like the state, see "Now playing") whether it was reached, the last success, and
the last failure with its time and category. Each station's fields are read as one snapshot, so the endpoint cannot
pair a `Reachable` from one poll with a `LastErrorUtc` from the next. Failures are recorded as fixed categories
(`StationFailures`), never as exception text or a URL; an unexpected error is "unexpected error; see the server log".
An exception outside any station's work (the channel lookup) is logged at Error and not recorded per station.

`LastFetchSeconds` (top level) is how long the last regular poll's fetch phase took, one decimal, null when it asked
no station (card L14); above the poll interval the
items update less often than set (see "Now playing"). The configuration page colours its "Now playing updates" line (card L11; "Poller" before) green up to half the poll
interval, yellow above half and red above the whole interval (card L5c); with no time and every listed SUB/WAVE station
idle it reads "✓ running. No SUB/WAVE station is in use (nobody plays one or has its page open), so none is asked.".

The response keeps the 0.4.x top-level fields for the primary SUB/WAVE station (`Reachable`, `LastSuccessUtc`,
`LastErrorUtc`, `LastError`, `IdleSinceUtc` (card L14 fix round), `Description`, `Cover`, `LiveItemExists`, the URL status) beside the channel status,
`ConfigurationWarning` (with `ConfigurationUnsaved` when it is about a failed save, card L9, and, after a failed load,
`ConfigurationUnreadableCopy` or `ConfigurationSaveRefusal`, card R1), `PollingEnabled`, `Running` and `LastFetchSeconds`, which the configuration page reads. `Stations` (card L4) lists every configured station in grid
order (`SortOrder`, list order breaking ties), disabled and refused ones included, for card L3's station table:

| Field | Value |
|---|---|
| `Id`, `Name`, `Kind`, `Enabled` | from the configuration; `Kind` is `SubWave` or `Stream` |
| `Listed`, `NotListedReason` | whether the tile lists it, else the fixed reason from `StationSources` ("it is disabled" for a disabled one); never part of a URL |
| `Reachable`, `LastSuccessUtc`, `LastErrorUtc`, `LastError` | a listed SUB/WAVE station's poll health under its current key (empty right after its URL changed); card L14: "Reload status" records here too |
| `IdleSinceUtc` | card L14: since when the poller has left a listed SUB/WAVE station idle (nobody plays it or has its page open), null while it is asked; a success or error after it came from "Reload status" |
| `Cover` | a listed SUB/WAVE station's applied image: a cover id, `logo`, or null |
| `Logo` | a listed plain stream's logo: `own` (applied), `embedded` (no logo URL), `deferred` (waiting for its download), `failed` (a refused URL, or a failed download or save waiting `FailedLogoRetry`) |
| `LastReadOutcome`, `LastReadUtc` | card L13: a listed plain stream's last title read, from `StationTitles.LastRead` (memory, since the last restart): `Answered`, `Moved` or `NoAnswer`, and when; null when it was not read (nobody played it with "Show what's playing" on, an HLS stream, a URL changed since) |

A station refused as a duplicate gets no poll fields, so it never shows the health of the station that holds its id.
No field carries a URL, a credential or exception text, and no host except one: a plain stream with a blank name is
named after its stream URL's host (card L11), host only, in `Name` here and in `GET /Jellywave/Stations` (route tests
with a port, a path and a query check that nothing else of the URL appears). A test serialises the response and checks. The
route resolves a channel-name collision itself when read (card 15). The configuration page (card L3) renders `Stations`
as the station table's status column (ok and the last success, the failure category, the not-listed reason, the image
or logo state; card L13: for a plain stream "✓ answered N min ago", "↪ moved N min ago: Edit → Check → Use it to read
titles" (yellow) or "⚠ no answer N min ago" from the last title read, else "✓ in the tile"; card L14: an idle SUB/WAVE
station reads "idle: nobody listening" (grey, no symbol, like "disabled"), never an old "on air" time, and after Reload
status "✓ answered when checked N s ago · idle: nobody listening" or "⚠ failing: <category> when checked N s ago · idle:
nobody listening"; times in s, min, h, then d
from 48 h on, a future time "0 s ago"), the channel status as a banner at the top only when it collides (card L13) and `LastFetchSeconds` under
Advanced, with a "Reload status" button ("Refresh status" before card L11; card L14: it posts `Health/Check`, so it
asks each SUB/WAVE station once and waits for the answers), so an admin never has to read the server log to know
whether a station is being reached.

## Configuration surface

`PluginConfiguration` (schema 1, 0.5.0.0) holds:

- `LibraryName`, the tile name ("Live Radio" by default and after the upgrade), `PreviousChannelNames`, the rename
  bridge's earlier tile names, and `CarriedChannelNames`, the earlier names "Give them access to the renamed tile" has run for.
- `Stations`, a list of `StationDefinition`: `Id` (the item's external id), `Name`, `Kind` (`SubWave` or `Stream`),
  `Enabled`, `SortOrder`; for `SubWave` the `BaseUrl` and `StreamPath` under the card 14 rules; for `Stream` a whole
  `StreamUrl` and an optional `LogoUrl`; `Tagline`; card L8: `Genre`, `Notes` and
  `Website` for either kind (empty by default and for a migrated station; in the `DataVersion` hash; the server enforces
  the limits where it reads them: see "Station per route"); and the format (`Codec`, `Container`, `BitrateKbps`,
  `SampleRate`, `Channels`, filled by the station dialog's "Check" button through the admin-only `POST
  /Jellywave/Probe`, see "Add-station probe").
- Global: the now-playing toggle and interval, the cover-art toggle, and `LastChangedUtc`, stamped by the
  server on every page save (card R1; the page's value is ignored).
- `SchemaVersion` (0 for a 0.4.x file) and the 0.4.x flat fields (`StationBaseUrl`, `StreamPath`,
  `StationName`, `StationTagline`, `StreamBitrateKbps`, `StreamSampleRate`, `StreamChannels`). Only the migration
  reads the flat fields; they are never cleared, so 0.4.2.0 still runs after a downgrade (with the values as they were
  at the upgrade; once 0.4.x is withdrawn with the 0.5.0.0 release, D4, only from a kept zip). The 0.4.x
  `ClientStreamUrl` and `Delivery` went in card L10: a downgrade reads 0.4.x's defaults
  for them, Relay and no client URL.

The collections have public setters on purpose: Jellyfin saves the page with `System.Text.Json`, which
does not fill get-only collections (`XmlSerializer` would). Both round-trips are tested.

**The configuration page (card L3; laid out again in card L13)** starts with a one-line intro naming the saved tile, then
the notices about the tile (below), the stations, who can see it (one gate for the whole tile; "Add the tile to every
user now"), the optional live panel ("Live panel", "Station page" before the L13 fix round), and a closed "Advanced" fold (`<details id="advancedSettings">`) holding the tile
name, "Show what's playing", the cover art switch, "Update every (seconds)" and the "Now playing updates" line. Same
fields, ids and defaults as before card L13; only their place changed. The L13 fix round took the approved sketch's
texts (MI): the table's "On" column and no Format column (Edit → Advanced shows the format), "Live panel", the tile
name help "Shown in My Media. Renaming makes a new tile; see README first.", a SUB/WAVE row "✓ on air · 12 s ago". Save posts the whole
configuration through Jellyfin's plugin-configuration route, so `Plugin.UpdateConfiguration` still merges
`PreviousChannelNames` and `CarriedChannelNames` (a test posts a page-shaped body with the list emptied).

- **Card L13, notices and the second Save.** The notices are banners at the top (`.jwBanner`, a coloured left edge
  repeating the head's colour), each shown only while it applies: the tile name taken by another plugin's tile (red
  "✕ Another plugin's tile is also called "<saved name>". Jellywave does nothing until you rename it (Advanced → Tile
  name).", which also opens the fold; yellow "⚠ Couldn't check the tile" when Health fails), the rename notice with
  its button (yellow "⚠ Tile renamed: N users could see "<old>" but not "<new>" yet." while users wait, green with the
  outcome after the press), the "earlier tile name now belongs to another plugin's tile" line (plain) and the settings
  file warning (yellow "⚠ Jellywave could not read its settings file and runs on defaults.", followed by the kept
  copy's file name, card R1; red "✕ … could not keep a copy of it." with "Save is refused until the copy works" when
  the copy failed; after a fresh install's
  failed first save, card L9, "⚠ Jellywave could not save its settings file. The stations are kept in memory until a
  save works (see the server log)."). A tile that is fine shows no line (the green "Tile status: ✓
  ok." went). A second Save (`btnSaveNear`, "Unsaved changes. [Save]") sits beside the table's buttons, shown only
  while the station list differs from the last load (the same test as the leave guard, so the tile name and the
  now-playing fields do not show it); it runs the same save as the bottom button, and a save note (refused while the
  dialog is open, not saved, could not load) shows beside both. The note asking to finish the dialog goes when the
  dialog closes; a good load clears both. Fix round S1: the form is `novalidate` and `save()` checks the interval field
  itself (`checkValidity`; when it fails, it opens Advanced and calls `reportValidity`), so both Saves refuse a value
  the field does not allow (the browser's own check could not show its bubble on a field inside a closed fold).
- The station table lists every station in grid order: name, type, an "On" box (Enabled), the status from
  `Health.Stations`, ↑/↓ buttons (keyboard buttons that keep the focus on the moved station; no drag), Edit and Remove
  (which asks in the row before it removes). Reordering rewrites `SortOrder` as the row index on save. A row added or
  edited since the last save says "not saved yet" instead of a status. Removing the station whose dialog is open closes
  that dialog with a note (Apply would otherwise put it back); Apply keeps the row's Enabled box as it is now.
- The Add/Edit dialog is a panel under the table (not a modal: jellyfin-web's dialog helper is not part of the plugin
  page contract). The stream URL and "Check" run the probe; its lines are shown as text, a redirect with "Use it". The
  kind comes from the probe and is shown, not chosen: a SUB/WAVE station stores `BaseUrl` and `StreamPath`, a plain
  stream `StreamUrl` (after a playlist, the entry). Name and tagline are filled from the probe (the name only while the
  field is empty or still holds the last filled value; card L8: genre, notes and website the same way), the logo URL,
  the genre and the website are offered for plain streams only (a SUB/WAVE station keeps any it has; only its notes show
  in its header), a Notes textarea (`maxlength` 280, with a character count) for both kinds, and "Advanced" holds the format
  fields (card L10 removed the Delivery choice and the client stream URL). Edit opens with
  the stored values; saving without a new check keeps the stored kind and URLs. A check's answer is applied only to the
  dialog, the check and the URL it was asked for (a numbered check; Enter does nothing while one runs; opening or
  closing the dialog drops any answer still on its way), so a slow answer never lands on another station or on a URL
  typed since. Re-checking a SUB/WAVE station sends its stored base along. Saving a SUB/WAVE station as a plain stream
  (a check found one, or its URL changed without a check) needs a second press, and the warning says what stops working
  (card L7: before, an unchecked URL got the generic warning); a new check or a typed URL asks again. A URL that was not
  checked successfully needs a second press and is saved as a plain stream with the format as entered, so a station that
  is down for the moment can still be added. Hints mirror the server's rules (id, URL rules, playlist and DASH paths,
  the website rule); they never block, and `StationSources` stays the authority: after Save the table
  shows its reason.
- **Card L11, first-run fixes and plain words.** The page says "tile", never "channel", except once under "Who can see
  it" ("where Jellyfin calls the tile a channel", since the Access page lists it under Channels); the server texts it
  shows follow ("another plugin's tile has the same name", "the tile does not exist yet (add a station and save)").
  The table's "Type" column reads "SUB/WAVE" or "Stream"; a status reads "in the tile" or "left out of the tile:
  <reason>"; "waiting for the first update" is grey (normal right after a save); "Update every (seconds)" and "Now
  playing updates" replace the poll words; "Reload status", "Give them access to the renamed tile" (its counts say
  "blocks copied"), and "Add to list" / "Apply to list" in the dialog. The grant result starts empty (the stale "Save
  the station settings first." went; the server's "Nothing done: …" covers a missing tile). File Transformation not
  installed is grey ("Not installed: add the repository https://www.iamparadox.dev/jellyfin/plugins/manifest.json
  (Dashboard → Plugins → Repositories), install File Transformation, then restart."; card U1: its catalog offers 3.0.x
  for 12.0–12.4, so the line names the steps instead of the 12.3 catalog gap),
  red stays for a registration that failed. A blank name is named as the server names it (see "The station items"),
  and the dialog warns about it. An Add box shows no hints until its URL field is typed in or Check is pressed (Edit
  shows them at once).
- **Card L11, save and leave.** A failed Save (the settings read or the post) hides the spinner and shows "✕ Not saved:
  <reason>" in red next to Save; the page keeps every change. Card R1: it then reads Health, and while saves are
  refused the reason is `ConfigurationSaveRefusal` instead of the HTTP status. A failed load hides the spinner too, says so there, and
  Save then posts nothing (the page's empty list would otherwise replace the server's stations). While the station
  list differs from the last load (a copy of each station without the page-only `_unsaved` key, in table order), a tab
  close or reload asks first (`beforeunload`). jellyfin-web 12.4's own navigation cannot be stopped:
  `viewContainer.loadView` calls the view manager's before-change hook and ignores its result, and that hook dispatches
  `viewbeforehide` without looking at the dispatch result (`main.jellyfin.bundle.js`, 12.3.0 commit c06cb17b27:
  `setOnBeforeChange((function(e,t,n){r&&p(r,null,"viewbeforehide",!0);…`, and `i&&i(v,!1,e)` in `loadView`), so the
  "Unsaved changes." note beside the second Save stays (card L13; "Unsaved changes: press Save at the bottom of the
  page." before). jellyfin-web keeps up to three views in the
  document; it dispatches `viewhide` (and `pagehide` for a page like this one) when another view is shown, and
  `viewdestroy` when it drops one: the guard asks only while this page is the view shown, and `viewdestroy` removes
  its listener.
- **Card R2b, warnings and the rename question** (HI decisions C and D2, texts HI-approved). Under the Stations heading a
  fixed yellow banner (`exposureWarning`, always shown) says that tile access only hides the tile: on Jellyfin 12.4 any
  signed-in user can read every station's address and play it through Jellyfin's own API, and anyone with an item id
  can play it without signing in (see "Who can see the channel" and "What clients still see"). The station dialog adds
  "⚠ Every signed-in user can read this address, even users the tile is hidden from. Leave secrets out of it." to its
  hints when the URL as typed, or the stream a check found (a playlist's entry), has a query parameter named like a
  secret, case-insensitive: a word of its name (split at punctuation, camelCase, an acronym before a word and digits:
  `api_key`, `accessToken`, `XAuth`, `token2`) is token, key, auth, sig, signature, secret, pass, password, session or
  sid; or a word or the whole name is a joined name (apikey, accesskey, accesstoken, authtoken, authkey, passkey,
  secretkey, streamkey, sessionid, sessid, phpsessid, pwd, passwd, credential, hdnts: `APIKEY`, `PHP_SESS_ID`); or a
  word ends in token, secret, password, signature or credential (`mytoken`). `?key=` warns, `?monkey=`, `?keyboard=`,
  `?passive=` and `?sidebar=` do not, nor a secret in the path or in a value. It is a warning only: Add and Save work as
  before. Save with a tile name other than the saved one, both read as the server reads them (trimmed, blank meaning
  "Live Radio"), first asks, beside both Saves, "Renaming makes a new tile. Users who had "<old>" ticked by name will
  not see "<new>" until you press Give them access to the renamed tile. Rename?" with Rename and Cancel buttons, the
  way the table asks before Remove. Rename saves the whole page and puts the focus back on the Save that was pressed;
  Cancel saves nothing, leaves the page as it is and puts the focus on the tile name; typing in the tile name drops the
  question, Rename asks again when the name changed since it was asked, and a Health answer for an earlier failed save
  does not replace the question. The open-dialog note and the interval check still come first.
- New stations get a GUID in "N" format from `crypto.getRandomValues` (`crypto.randomUUID` needs a secure context and
  Jellyfin is often served over plain http); since card R2a the server gives the starters and the migrated station random
  ids of the same shape too.
- Every station-supplied or probe-supplied string reaches the page through `textContent` or an input's `value`; a test
  fails when the embedded page contains `innerHTML`, `outerHTML`, `insertAdjacentHTML`, `document.write`, `eval(`,
  `new Function`, `setHTML`, `setHTMLUnsafe`, `parseHTMLUnsafe`, `createContextualFragment`, `parseFromString`, `srcdoc`
  or `.html(`.
- `Jellyfin.Plugin.Jellywave.Tests/js/configPage.test.js` runs the page's script with plain `node` (no dependencies)
  against a fake DOM and a fake `ApiClient` whose probe answers stay pending until a scenario resolves them: the table,
  reorder, remove, add, edit, save, and the late-answer and open-dialog cases; card R2b: the two warning texts word for
  word, the secret-parameter names (positive and negative, a checked playlist entry, Add still working) and the rename
  question (cancel, accept, a blank name, a name typed since, names as text). CI runs it.

**Primary station.** The tile lists every station (card L2), the poller polls every SUB/WAVE station (card L4) and
each station route serves the station its `stationId` names (card L5). `StationUrls`' configuration overloads, a station
route called without a station id (an older cached page) and Health's top-level fields read
`PluginConfiguration.PrimaryStation()` (`StationSources.Primary` for its key). That is the candidate
(`PrimaryCandidate()`, the first enabled `SubWave` station by `SortOrder`, list order breaking ties), and only while the tile lists it: a candidate refused by its rules,
a duplicate id included, is no primary station at all, so no feed, cover, station page or request is served for a
station that is not published, and its now-playing line and cover never land on the item of another station that
holds its id. There is no fallback to the next SUB/WAVE station, so the status line stays about one station: Health
reads the candidate to say why (a refused base URL as before, otherwise "the SUB/WAVE station is not listed:
<reason>"). Which station is primary no longer decides which item gets a now-playing line or a cover (card L4: every
SUB/WAVE item has its own); a reorder only moves an old page (one that sends no station id) to another station's state,
which the poller has kept all along. The channel's description and home page are the primary station's too.

**Loading and saving (card R1).** `BasePlugin<T>` (12.4) loads the configuration lazily on the first read of
`Configuration`; its `LoadConfiguration` writes defaults over a file it cannot read, and its `UpdateConfiguration`
assigns before it saves. The plugin uses neither. Its constructor checks `File.Exists(ConfigurationFilePath)` before
anything reads `Configuration`: with a file, `Plugin.LoadExisting` deserializes it with the plugin's own
`XmlSerializer` and assigns it with the protected setter, so the base loader never runs. A file that cannot be read is
copied aside as `<file>.unreadable-<UTC yyyyMMddTHHmmssfffZ>.xml` (`ConfigurationFile.KeepUnreadable`: a new file each
time, `-2`, `-3`… on a clash, so no copy overwrites earlier evidence; a half copy is deleted), and the plugin runs on
defaults in memory (`Plugin.RunningOnDefaults`) and saves nothing on its own. `Health` names the copy
(`ConfigurationUnreadableCopy`, the file name only) once it is written. When the copy fails,
`Plugin.PreservationFailed` makes every save refuse (`Plugin.SaveRefusedReason`, also sent as
`Health.ConfigurationSaveRefusal`, because Jellyfin 12.4 answers a plugin exception on the page's POST with a 500 and
"Error processing request.") and each save attempt tries the copy again first, so no restart is needed once Jellyfin
can read the file and write to its folder. The copy gets the file's Unix permissions.

Every save goes through `Plugin.SaveConfiguration` (page saves, carried names, the migration, the fresh install and any
call from Jellyfin): the configuration is serialized whole, written to a new uniquely named file in the same folder
(`FileMode.CreateNew`, given the old file's Unix permissions, flushed to disk, closed), then swapped in with
`File.Replace` (keeping one `<file>.previous`) or `File.Move` when there is no file yet (`ConfigurationFile.Write`).
While running on defaults the swap makes no new `.previous`: the file replaced is the unreadable one, already kept as
its copy, and the `.previous` there (usually the last file the plugin wrote) survives. A failure before the swap deletes
the new file and throws, leaving the old file. A swap that stops after moving the old file to `.previous` (Windows
`ReplaceFile` can, with ERROR_UNABLE_TO_MOVE_REPLACEMENT_2) is finished: the complete new file is renamed into place and
the save counts as done, or, if that rename fails too, `.previous` is renamed back and the save fails. So after any
save, failed or not, the settings file exists and is the old or the new complete one; only a failed rename back as well
(both files then stay on disk) could leave it missing. A swap that reports a failure after it happened leaves the new
file on disk and the old configuration in memory until the next start. The folder is not flushed after the rename, so a
power loss right after a save may bring back the previous complete file. The settings file must be a regular file in a
folder Jellyfin can write to: a symlink is replaced by a regular file, and a single-file bind mount makes every save
fail (logged, and "Not saved" on the page). `UpdateConfiguration` prepares a copy of the posted
body (`ConfigMigration.PrepareSave`), stamps `LastChangedUtc` itself (now, but at least 1 s after the item date the
previous configuration gave, `max(LastChangedUtc, MediaSourceSchemaDate)`, so the items' `DateModified` always
advances; the page's value is ignored), writes it, and only then assigns it and clears `RunningOnDefaults` and
`RunningUnsaved`; then it runs each `ConfigurationChanged` handler, logging one that throws (the save stands). A failed
write throws and changes nothing in memory. `RecordCarriedNames` also changes a copy, writes it, then assigns it; while
the configuration is in memory only, the names wait there for the next page save.

**Migration.** `Plugin`'s constructor runs `ConfigMigration.Migrate` on a loaded file when `SchemaVersion` is below 1,
saves, and only then publishes `Plugin.Instance`, so no service sees the flat shape. Jellyfin builds plugins in
`ApplicationHost.FindParts` after `SetStaticProperties` has resolved `ILibraryManager` (which the
migration needs for the id rule), so taking it in the constructor changes no startup order. The
constructor never throws, because an exception there marks the plugin Malfunctioned
(`PluginManager.CreatePluginInstance`): a failed save is logged and the migrated copy is used from memory;
a failed load runs on defaults as above, and the page warns until the next successful save (a failed save leaves the
warning up); a failed migration is logged at Error and the configuration runs as loaded,
unmigrated. `Migrate` derives every id before it changes anything, so a failing id rule cannot leave a
half-migrated configuration (tile renamed, no bridge). In every case the next start tries again from the
untouched flat fields. The rules (diagram 9 in `docs/dev/PLAN.md`): a non-empty `StationBaseUrl` becomes a station with
a random id (card R2a; `live` before, `ConfigMigration.LegacyMigratedStationId`) (`SubWave`, enabled, sort 0, name = the 0.4.x tile name, every station field copied verbatim, also a
URL card 14 now refuses, which the page and Health keep reporting); the 0.4.x tile name (`StationName`,
"SUB/WAVE" when blank) goes to `PreviousChannelNames` unless it derives the new id; `LibraryName` becomes
"Live Radio"; `SchemaVersion` becomes 1. A second run changes nothing; an existing `Stations` list is
kept, and the station is not added when the list already holds one with the id `live` or a SUB/WAVE station with the
same base URL. While the folder refuses the migrated save, each start migrates again and so gives the station a new
random id (its item changes each start until a save works; D1: no 0.4.x installs besides HI's). A posted body below schema 1 is migrated the same way before it is
saved only when it carries nothing of the new shape (no station, default tile name); otherwise its tile
name and stations are kept, it is marked schema 1, and it still gets the name union.

**Starter stations (card L9).** `StarterStations.Create()` holds the stations a fresh install starts with (the SUB/WAVE
demo, KEXP, WRUW, WJCU and XCSB; SomaFM's Groove Salad was approved but left out over SomaFM's terms, see PLAN L9), with
random `N`-format GUID ids made on each seed (card R2a: fixed ids made every install's item ids the same and computable),
`SortOrder` 0, 1, … and the format each stream sends. The seed is saved, so the ids hold across restarts. A fresh install is "no configuration
file when the plugin starts": `Plugin`'s constructor checks `File.Exists(ConfigurationFilePath)` before the first
read of `Configuration`, because Jellyfin's `LoadConfiguration` writes a default file when there is none, after which
a fresh install looks like any upgrade (a 0.4.x install that never had a URL has the same values). With no file,
`Plugin.StartFresh` builds the defaults, runs `ConfigMigration.Migrate` on them as Jellyfin's default file would have
gone (schema 1, "Live Radio", "SUB/WAVE" as an earlier name), adds the starters (`StarterStations.Seed`, which also
refuses a non-empty list), sets `Configuration` and saves. With a file, nothing is seeded, ever: an upgrade, a migrated
0.4.x file, a list with one starter removed and an empty list all load as saved, so a removed station is never put
back and an emptied list stays empty across restarts. A reinstall keeps Jellyfin's configuration file, so it gets no
starters either. The starters are never put into `PluginConfiguration`'s constructor: `XmlSerializer` appends a saved
list to the one the constructor made, so every start would add them again (tested against hand-written files and
real round trips). Failures: a failed save logs an Error, keeps the starters in memory and reports
`Plugin.RunningUnsaved` (not `RunningOnDefaults`: nothing failed to load and there is no old tile), so `Health` sends
`UnsavedWarning` with `ConfigurationUnsaved` and the page says "⚠ Jellywave could not save its settings file." until a
page save works; carried tile names wait in memory for that save, as on defaults. A failed first save leaves no file
(the writer renames its new file into place only when it is complete, card R1), so the next start is fresh again. A
failed migration (the id rule) is logged and the file is saved at schema 0 with the starters, so the next start migrates it
and keeps them. The starters pass the same rules as an admin's station (`StationSources.Refusal`, `StationUrls`; a
test runs every one through them and checks that the format fields agree with each other and with the stream
address). Their logos come through the plain-stream logo path ("Station logos") at runtime; nothing is bundled.
Consequence for a fresh install: the SUB/WAVE demo is polled every update interval like any SUB/WAVE station, and each
logo host is asked once per start, whether or not anyone listens.

**Public station ids re-keyed (card R2a, HI decision B).** Jellyfin derives a channel item's id as
`MD5(UTF-16LE(type full name + external id + channel name + "16"))` (`ChannelManager.GetIdToHash`), the external id
being the station id, and its `/Audio/{id}/stream` answers without sign-in (no `[Authorize]` on `AudioController`). A
station id that is the same on every install (the 0.4.x migration's `live`, the five fixed ids the starters had before
0.5.0.0) therefore lets anyone who knows the tile name compute a playable item id. `StationIds.KnownPublicIds` lists those
six. Once, at startup, after the load, the migration or the fresh install and before `Plugin.Instance` is published,
`Plugin.RekeyPublicIds` gives every station whose id is in the list a new random id (`StationIds.NewId`; stations that
shared one public id, a refused duplicate, share its one new id, so the duplicate stays refused). Like every change it
is written before it takes effect (card R1): a copy is re-keyed, its change time stamped, saved through the settings
writer, then put in effect. While the plugin may not save on its own (`RunningOnDefaults`, `RunningUnsaved`) the copy is
put in effect in memory only and the next page save writes it (today neither state can hold a public id: defaults have
no stations and a fresh seed has random ids). A failed save is logged and changes nothing; the next start tries again. A
re-keyed list holds no public id, so it is never re-keyed again, and a list without one is not saved at all. A page
save re-keys too (fix round: `UpdateConfiguration` applies `StationIds.Rekey` to the candidate before it is written), so a
configuration page opened before the upgrade restart and saved after it cannot write a public id back. The new
ids make new items (`DataVersion` changes once): favourites and play state on the old items do not carry over
(accepted by HI). Jellyfin's own channel reconciliation removes the old items.

All SUB/WAVE URL handling lives in `StationUrls`, which resolves the stream path under the base URL and
rejects anything that escapes it (another scheme, host, port or userinfo, an absolute path, `..`); see
"Outbound requests". Plain streams get their own rule there, `TryParseStreamUrl` (the item's URL from card L2;
`TryParseLogoUrl` applies the same rule to logos):
an absolute http or https URL with a host, no userinfo (not even an empty `@`), no fragment (not even a
bare `#`); a query is allowed (HI decision), so a stream URL is disclosed with its query through
PlaybackInfo like any stream URL.

No environment-specific value lives in source: the only station addresses in it are the public starter stations
(card L9), and an admin who removes them is left with no station; the channel publishes no items without one.

## Packaging

`build.yaml` is the single source of plugin metadata; `scripts/build.sh` publishes the
assembly, writes `meta.json` and zips the three artifacts. `manifest.json` is written
only by the release workflow, from the zip it publishes (`UPDATE_MANIFEST=1 scripts/build.sh`
refreshes it locally on purpose). `scripts/check-manifest.sh` asserts the GUID, version and ABI agree
across `Plugin.cs`, the config page, `build.yaml`, `manifest.json`,
`Directory.Build.props` and the staged `meta.json`. The release workflow builds the
tagged commit and commits the manifest to `main`.

`targetAbi` (12.4.0.0 from 0.5.0.0 on; 0.4.x had 12.3.0.0) is the oldest server Jellyfin offers a
build to, and the runtime binds an assembly reference only to a loaded assembly of the same or a newer
version. A build compiled against the 12.4.0 packages references 12.4.0.0 assemblies, which a 12.3 or
older server cannot load, so `targetAbi` must name the packages the plugin is compiled against. Two
checks keep them together: `check-manifest.sh` compares the `Jellyfin.Controller` and `Jellyfin.Model`
versions in both csproj files with `targetAbi`, and `AbiTests` fails when any `Jellyfin.*` or
`MediaBrowser.*` reference in the built assembly is newer than `targetAbi`. Releases up to v0.3.4.0 claimed 12.0.0.0 while
referencing 12.3.0.0. They and every other release before 0.5.0.0 (0.1.0.0 to 0.4.2.0) are withdrawn with the
0.5.0.0 release (HI decision D4: catalog entries first, then the downloads; git tags stay).

To cut a release, bump `<Version>` in `Directory.Build.props` and the `changelog` line in `build.yaml`, commit, tag that
commit `v<version>` and push the tag. The workflow fails on purpose if the tag version does not match
`Directory.Build.props`. If it stops part way, run it again for the same tag (Actions > Release > Run workflow).

The release workflow (R3) resolves the tag to one commit SHA, runs the shared checks in `verify.yml` on that SHA, and
builds the same SHA. One release run goes at a time, and a waiting run queues rather than being cancelled
(workflow-level `concurrency` with `queue: max`). `build` (token `contents: read`, checkout without stored credentials)
runs `build.sh` and `check-manifest.sh` and uploads the zip and `meta.json` as an artifact. `publish`
(`contents: write`) downloads that artifact and runs main's `scripts/publish-release.sh`; while the write token is
present only `gh`, `jq`, `git`, `unzip`, `md5sum` and main's scripts run: no package restore, analyzer or build. The
script:

- refuses a tag that is not `v<a>.<b>.<c>.<d>`, and a tag that no longer points at the verified and built commit;
- creates the release as a draft and attaches the zip; a zip already attached is reused instead of this run's build,
  after checking that it holds exactly the DLL, `meta.json` and `thumb.png` and that its `meta.json` names this
  plugin, version and `targetAbi`; an attached zip is never replaced, except one whose upload never finished on a
  draft (deleted and uploaded again; on a published release that stops the run);
- treats only gh's "release not found" as no release, so a network or server error cannot create a second draft;
- writes and checks the manifest entry before publishing: checksum from the attached zip's bytes, changelog, timestamp
  and `targetAbi` from that zip's own `meta.json`; it stops, with the release still a draft, when the manifest already
  lists the version with another checksum or the written entry does not describe the zip (main's script predating the
  release, so tag only after the release's branch is merged);
- publishes the draft (the order GitHub's immutable releases need), then pushes the entry, rebasing on `main` when it
  moved.

A run that stops part way leaves a draft with or without the zip, a published release without a manifest entry, or a
rejected manifest push, and a re-run finishes each; a manifest that disagrees with a release's zip, or a published
release with an unfinished zip, needs a fix by hand and says so.
`Jellyfin.Plugin.Jellywave.Tests/sh/publishRelease.test.sh` runs these cases, failures before and after gh's side
effect, `main` moving during a run, wrong zips and a moved tag, against a stand-in `gh` and a local git remote.
A manual run (`workflow_dispatch`) takes `verify.yml` and `publish-release.sh` from `main`, not from the tag; the tag's
commit is still what is checked and built. `ci.yml` runs the same `verify.yml` (build, tests,
`dotnet format --verify-no-changes`, `node --check`, both page tests, `bash -n`, the publishing test and
`check-manifest.sh`) on every push and pull request with a read-only token. Every action is pinned to a full commit
SHA; `.github/dependabot.yml` proposes updates for the actions and the NuGet packages (not the Jellyfin packages, which
move with `targetAbi`).
