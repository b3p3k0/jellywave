# Jellywave: SUB/WAVE radio as a "My Media" entry in Jellyfin 12.x

Status: cards 1–11 accepted (1–4: 3ebcd03, c63aa14, b096685, 9953caf; 5 docs: ec8efde; 6: c370a24; 7: 2026-10-27, commits 0228d43 and 8edfa75; 8s/8a/8b/8c: 2026-10-27, commits d95f759 through 13b9ebf; 8d: 2026-10-27, commits a923124 through 4895824; 9: 2026-10-27, commits 4d67e6f and c6ea146; 10: 2026-10-27, commit 0505d94; 11: 2026-10-27, commit cf9d672, see the phase 2 closeout table). v1 is closed: release v0.1.0.0 is published and HI confirmed installing from the manifest URL in the dashboard catalog on 2026-10-27. Open: the lyrics panel shows a sentence instead of lines on HI's station (station side, not a blocker); v0.3.3.0 is released (manifest 0612866, zip matches the entry) and v0.3.4.0 is built from cf9d672 and unreleased. Original plan approved 2026-10-26; sections marked "corrected"/"changed" record what implementation taught. Role at writing: PA. Track: full process.
Research performed 2026-10-26 against Jellyfin v12.3 source, jellyfin-web, jellyfin-roku, jellyfin-androidtv, the plugin template, the SUB/WAVE repo, and live probes of the Jellyfin test host and the station host.

## Context

Kevin runs Jellyfin 12.3.0 on the Jellyfin test host and a SUB/WAVE internet-radio station ("IHateRadio") on the station host. Goal: a tile in Jellyfin's My Media row, alongside Live TV, that plays the station with transport controls on web, Android, and (bonus) Roku / Android TV. Stream audio is the must-have; the SUB/WAVE web player is a later phase.

Decisions already made by HI (2026-10-26):

| Decision | Choice |
|---|---|
| Network | A public domain fronts Jellyfin; some clients cannot reach the station host. Default delivery = relay through Jellyfin, with Direct as a config option. |
| v1 scope | Stream tile only. Embedded web player page is phase 2. |
| Repo / name | `~/DEV/jellywave`, plugin "Jellywave", assembly `Jellyfin.Plugin.Jellywave`. `git init` now, commits on `main`, HI owns push and any GitHub remote. |
| Now playing | Best effort in v1: poll the station and update the item's metadata. |
| URL visibility | Accepted: signed-in users can see the station URL in playback info (README documents it). |
| Phase 2 | Custom in-Jellyfin station page (top-bar tab with a mini-player). No upstream Jellyfin PR for now. Decided after v1 cards 3–5 close. |

## Verified facts the design rests on

- `UserViewManager.GetUserViews` adds every registered `IChannel` as its own top-level view (`list.AddRange(channels)`), right before the Live TV folder. A channel plugin is therefore a My Media tile with no client changes. (`Emby.Server.Implementations/Library/UserViewManager.cs`, v12.3)
- **Registration (corrected in card 2 review):** Jellyfin 12 does not discover `IChannel` exports by reflection (no `GetExports<IChannel>` since 10.9). `ChannelManager` takes `IEnumerable<IChannel>` from DI, so the plugin must register the channel in its `IPluginServiceRegistrator` with `AddSingleton<IChannel, JellywaveChannel>()`.
- Channel plugin surface: `MediaBrowser.Controller.Channels.IChannel`, `ChannelItemInfo` (has `IsLiveStream`, `MediaSources`, `DateModified`), `InternalChannelFeatures`. Audio items become `Audio` entities; `IsLiveStream` nulls `RunTimeTicks` and tags "livestream". Channel logo comes from `GetChannelImage` via `ChannelImageProvider`. (`src/Jellyfin.LiveTv/Channels/*.cs`)
- Media sources: `BaseItem.GetMediaSources` returns the saved static sources for channel items; `MediaSourceManager.GetPlaybackMediaSources` then appends dynamic-provider results. Implementing `IRequiresMediaInfoCallback` as well would duplicate sources. Use static sources only. Static sources are re-saved when `info.DateModified` is newer than the stored item.
- Channel item list is cached 3 h on disk, keyed by a hash of `IChannel.DataVersion`. Changing `DataVersion` invalidates it.
- Playback of a remote HTTP audio source: `MediaInfoHelper.SetDeviceSpecificData` forces `EnableDirectStream=false` ("direct-stream http streaming is currently broken"). So: `SupportsDirectPlay=true` lets the client fetch the URL itself (web checks `IsRemote` then uses `mediaSource.Path`); `SupportsDirectPlay=false` makes the server transcode via ffmpeg (`AudioHelper`, progressive). The built-in per-user permission `ForceRemoteSourceTranscoding` does the same thing per user.
- Clients: jellyfin-web opens a channel through the legacy list controller (`src/apps/legacy/controllers/list.js`); Roku `HomeItem.bs` accepts item type `channel` and `MainEventHandlers.bs` opens it as a generic library and plays `audio` items; Android TV `GenericFolderFragment.kt` handles `CHANNEL`; Android mobile wraps jellyfin-web.
- Channel visibility per user: `Channel.IsVisible` requires `EnableAllChannels` permission or the channel in `EnabledChannels`. Admin users have it; verify for other users during testing.
- Toolchain: local `dotnet` SDK 10.0.113; NuGet `Jellyfin.Controller` and `Jellyfin.Model` 12.3.0 exist; Jellyfin 12 targets `net10.0`; the official plugin template is still on 10.11.5 / net9.0 and must be bumped. Official webhook plugin (12.x) is the reference for `IPluginServiceRegistrator` + `net10.0` csproj.
- the Jellyfin test host: Jellyfin 12.3.0 as systemd service, plugins in `/var/lib/jellyfin/plugins/<Name>_<ver>/` owned `jellyfin:jellyfin`, `kevin` has sudo with password, ffmpeg at `/usr/lib/jellyfin-ffmpeg/ffmpeg`. Existing `meta.json` shape: see SeerrFin's (`targetAbi` "12.0.0.0", `assemblies` list, `status` Active).
- Station: SUB/WAVE 1.26.0: station "IHateRadio", public (no listener auth, no private player), MP3 192 kbps 44.1 kHz stereo at `http://<station-host>:7700/stream.mp3`, CORS `*`, ICY headers present. Public JSON: `GET /api/now-playing` (`nowPlaying.title/artist/album/subsonic_id/duration`, `context.activeShow.name`), `GET /api/state` (`station.name`, `privacy`), `GET /api/health` (`{"status":"on-air"}`). Tune-in files at `/listen.pls`, `/listen.m3u`.
- Access: `kevin@<station-host>` works (docker group). `claude@<station-host>` needs `~/.ssh/claude_framework`, absent on this machine. Not needed for v1; SUB/WAVE is not modified.

## Architecture

One plugin assembly, server-side only. No jellyfin-web changes.

```
~/DEV/jellywave
├── Jellyfin.Plugin.Jellywave.slnx
├── Directory.Build.props            # version 0.1.0.0
├── build.yaml                       # jprm-style metadata (name, guid, targetAbi 12.0.0.0, framework net10.0)
├── README.md, LICENSE (MIT), .gitignore, .editorconfig   # from template
├── manifest.json                    # plugin repository manifest, served raw from GitHub main (see Manifest)
├── .github/workflows/release.yml    # on tag v*: build, zip, attach to GitHub release, update manifest.json
├── Jellyfin.Plugin.Jellywave/
│   ├── Jellyfin.Plugin.Jellywave.csproj   # net10.0; Jellyfin.Controller + Jellyfin.Model 12.3.0, ExcludeAssets runtime
│   ├── Plugin.cs                          # BasePlugin<PluginConfiguration>, IHasWebPages; new GUID
│   ├── PluginServiceRegistrator.cs        # registers IChannel (card 2) and NowPlayingService as IHostedService (card 4)
│   ├── Configuration/PluginConfiguration.cs
│   ├── Configuration/configPage.html
│   ├── Channels/JellywaveChannel.cs       # IChannel
│   ├── Services/StationClient.cs          # typed HTTP client for /api/state and /api/now-playing (landed in card 3 for the probe)
│   ├── Services/Station/*.cs              # one DTO per file (StyleCop SA1402)
│   ├── Api/JellywaveController.cs         # admin-only GET /Jellywave/Station?baseUrl= used by the config page
│   ├── Services/NowPlayingService.cs      # IHostedService poller
│   └── Assets/logo.png                    # embedded; channel Primary image (temporary mark; swap for station art later)
├── scripts/
│   ├── build.sh                           # dotnet publish -c Release → dist/Jellywave_<ver>/{dll, meta.json, thumb.png} + zip
│   ├── update-manifest.sh                 # writes the version entry (checksum, sourceUrl, timestamp) into manifest.json
│   └── (untracked) local/deploy-jellyfin.sh  # dev helper HI runs on the test host; local/ is gitignored, not a deliverable
└── docs/
    ├── PLAN.md                            # this plan, committed to the repo on approval
    └── DESIGN.md                          # behaviour doc, closed out with the code (Charter: docs in same wave)
```

### Configuration (`PluginConfiguration`)

| Field | Default | Notes |
|---|---|---|
| `StationBaseUrl` | empty | Used by the server for API calls and, in Relay mode, as the stream origin. Empty means the channel publishes no items (Development SOP: no hostnames in source; the test value lives in the deploy notes). |
| `StreamPath` | `/stream.mp3` | Appended to the base URL. |
| `ClientStreamUrl` | empty | Direct mode only: URL as clients see it. Empty means base + path. |
| `StationName` | `SUB/WAVE` | Tile and channel name. Config page has a "Fetch from station" button that reads `/api/state`. Changing the name changes the internal channel id (server derives it from the name), so the doc warns that renaming creates a new tile. |
| `Delivery` | `Relay` | `Relay` sets `SupportsDirectPlay=false` (server ffmpeg). `Direct` sets it true (client fetches the URL). |
| `NowPlayingEnabled` | `true` | |
| `CoverArtEnabled` | `true` | Card 7: the poller saves the current track's cover as the live item's Primary image; logo when off or when the station has no art. |
| `NowPlayingPollSeconds` | `20` | Clamp 5..300 (`EffectivePollSeconds`). |
| `StationTagline` | empty | Channel description; filled by "Fetch from station" from the on-air DJ tagline. (Added in card 3.) |
| `StreamBitrateKbps` / `StreamSampleRate` / `StreamChannels` | 192 / 44100 / 2 | Describe the stream to clients; filled by "Fetch from station" from `/api/now-playing`'s `stream` block. (Added in card 3.) |
| `LastChangedUtc` | Unix epoch until first save | Drives `ChannelItemInfo.DateModified` and `DataVersion`, so config edits re-save media sources and bust the 3 h cache. |

### `JellywaveChannel : IChannel`

- `Name` = `StationName`; `Description` = station tagline or "Live radio from SUB/WAVE"; `DataVersion` = short hash of (base URL, stream path, client URL, delivery, name, LastChangedUtc); `HomePageUrl` = base URL + `/listen`; `ParentalRating` General.
- `GetChannelFeatures`: `MediaTypes=[Audio]`, `ContentTypes=[Song]`, `MaxPageSize=10`, `AutoRefreshLevels=1`.
- `IsEnabledFor` returns true. (Per-user hiding stays with Jellyfin's own channel permissions.)
- `GetChannelItems`: ignores folder id; returns exactly one `ChannelItemInfo`:
  - `Id="live"`, `Type=Media`, `MediaType=Audio`, `ContentType=Song`, `IsLiveStream=true`
  - `Name=$"{StationName} — Live"`, `Overview` = current now-playing line if the poller has one. **Changed in card 4:** `Artists`/`AlbumArtists` are never set (see the NowPlayingService section).
  - `DateModified` = max(`LastChangedUtc`, `MediaSourceSchemaDate`) (**changed in card 2** so an upgraded plugin re-saves its media sources), `ImageUrl` null (inherits channel image)
  - one `MediaSourceInfo`: `Path`=stream URL (base+path, or `ClientStreamUrl` in Direct mode), `Protocol=Http`, `IsRemote=true`, `IsInfiniteStream=true`, `Container="mp3"`, `Bitrate=192000`, `SupportsDirectPlay=(Delivery==Direct)`, `SupportsDirectStream=false`, `SupportsTranscoding=true`, `SupportsProbing=false`, `RunTimeTicks=null`, `MediaStreams=[audio: Codec "mp3", Channels 2, SampleRate 44100, BitRate 192000, Index 0]`. Bitrate, sample rate and channels come from the config (filled from `/api/now-playing`'s `stream` block by the config page), else these defaults.
- `GetChannelImage(Primary)`: returns the embedded PNG as a `DynamicImageResponse` (`HasImage=true`, `Format=Png`, `Stream`). `GetSupportedChannelImages` = `[Primary]`.

### `NowPlayingService : IHostedService`

- Registered by `PluginServiceRegistrator` (`serviceCollection.AddHostedService<NowPlayingService>()`), uses `StationClient` (over `IHttpClientFactory`), `NowPlayingState`, `ILibraryManager`, `IServerApplicationHost`, `ILogger`.
- Loop: every `NowPlayingPollSeconds`, GET `/api/now-playing`. The last snapshot lives in `NowPlayingState`, a DI singleton both the poller and the channel receive. The first poll waits for `IServerApplicationHost.CoreStartupHasCompleted` so it does not race Jellyfin's own channel refresh.
- On track change: locate the Audio item with `_libraryManager.GetItemList(new InternalItemsQuery { ChannelIds=[channel.Id], ExternalId="live", IncludeItemTypes=[BaseItemKind.Audio] })`, where `channel` comes from `IChannelManager.GetChannelsInternalAsync(new ChannelQuery())` filtered by `Name`. Set only `Overview = "Now playing: {artist} – {title} ({album}) · DJ {dj} · {show}"`. **Changed in card 4:** `Artists`/`AlbumArtists` are deliberately not written, because Jellyfin's artists validator derives Music-library artist entries from `Audio.Artists` and every played track would create one. Persist with `UpdateItemAsync(item, channel, ItemUpdateType.MetadataEdit, ct)`. **Corrected in card 4 review:** Jellyfin skips the item-updated event for channel items (`LibraryManager.UpdateItemsAsync` only raises it for `SourceType.Library`), so nothing is pushed to clients; the text shows on the next list load.
- Failure handling: log at Debug on transient errors, Warning once per state change (on-air → unreachable). Never throw out of the loop. Respect cancellation on shutdown.
- Known limit (documented): clients show the updated text when they reload the list or start playback; the playing bar does not update mid-track. That is the "best effort" HI accepted.

### Manifest (plugin repository) — in from the start

Jellyfin installs third-party plugins from a repository URL that returns a JSON array of plugins, each with a `versions` list. Format (same as the SeerrFin test stack's `repackage.sh` produces, and as Jellyfin's own `manifest.json`):

```json
[
  {
    "guid": "<new GUID, same as Plugin.Id and build.yaml>",
    "name": "Jellywave",
    "description": "SUB/WAVE internet radio as a My Media entry.",
    "overview": "Adds a SUB/WAVE radio station tile to My Media on every Jellyfin client.",
    "owner": "b3p3k0",
    "category": "General",
    "imageUrl": "https://raw.githubusercontent.com/b3p3k0/jellywave/main/Jellyfin.Plugin.Jellywave/Assets/logo.png",
    "versions": [
      {
        "version": "0.1.0.0",
        "changelog": "Initial release.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://github.com/b3p3k0/jellywave/releases/download/v0.1.0.0/Jellywave_0.1.0.0.zip",
        "checksum": "<uppercase MD5 of the zip>",
        "timestamp": "2026-10-26T00:00:00.0000000Z"
      }
    ]
  }
]
```

- Zip layout is flat: `Jellyfin.Plugin.Jellywave.dll`, `meta.json`, `thumb.png`. Jellyfin names the install folder `<name>_<version>` from `meta.json`.
- Repository URL users add in Dashboard → Plugins → Repositories: `https://raw.githubusercontent.com/b3p3k0/jellywave/main/manifest.json`. Assumption: GitHub repo `b3p3k0/jellywave`; HI confirms the slug when creating the remote (Hard Stop: remote and push are HI's).
- `scripts/update-manifest.sh <version> <changelog>`: computes the zip MD5, inserts or replaces the version entry (newest first), sets the timestamp. Pure `jq`, no network. Kept idempotent so re-running after a rebuild is safe.
- `.github/workflows/release.yml`: on tag `v*`, `dotnet publish`, zip, create the GitHub release with the zip attached, run `update-manifest.sh`, commit `manifest.json` back to `main`. Modeled on the template's `publish.yaml` but self-contained (no jprm, no jellyfin-bot). Not exercised until HI pushes; the local scripts do the same job for dev.
- `meta.json` carries the same `guid`, `version`, `targetAbi`, `timestamp` as the manifest entry, generated from `build.yaml` by `build.sh` so the three never drift. A `scripts/check-manifest.sh` asserts `Plugin.Id == build.yaml guid == manifest guid` and that the manifest's top version equals `Directory.Build.props`; card acceptance runs it.
- Until the first push, the Jellyfin test host cannot fetch the manifest, so the dev path below stays. Once `main` is on GitHub, the acceptance test for card 5 is installing Jellywave on the Jellyfin test host from the repository URL in the dashboard.

### Deployment

`scripts/build.sh` (run here): `dotnet publish Jellyfin.Plugin.Jellywave -c Release -o dist/stage`, then assemble `dist/Jellywave_<version>/` with `Jellyfin.Plugin.Jellywave.dll`, `meta.json` (generated from `build.yaml` + version + timestamp, same field set as SeerrFin's), `thumb.png`, and zip it as `dist/Jellywave_<version>.zip`. **Changed 2026-10-27:** it no longer touches `manifest.json`; the release workflow writes the entry from the zip it publishes, so a local build cannot overwrite a published checksum (`UPDATE_MANIFEST=1` opts in).

`local/deploy-jellyfin.sh` (untracked dev helper; HI runs it on the Jellyfin test host): modeled on `~/DEV/seerrfin-teststack/deploy/setup.sh`. Steps: confirm systemd Jellyfin is active; show what it found; `sudo systemctl stop jellyfin`; back up any existing `Jellywave_*` folder to `~/jellywave-plugin-backup-<ts>.tar.gz`; remove old `Jellywave_*`; copy the staged folder in; `chown -R jellyfin:jellyfin`; start; tail the log for `Jellywave` and `Loaded plugin`. `--rollback` restores the latest backup. MI copies `dist/Jellywave_<ver>/` and the script to `<jellyfin-host>:~/jellywave-dist/` with `scp`; HI runs `bash ~/jellywave-dist/deploy-jellyfin.sh`.

## Task cards (DA implements one at a time; RA accepts each)

1. **Scaffold + build + manifest.** Copy the template, rename to Jellywave, bump to net10.0 / 12.3.0, new GUID, `git init`, first `wip:` commit. Add `manifest.json`, `build.yaml`, `scripts/build.sh`, `scripts/update-manifest.sh`, `scripts/check-manifest.sh`, and `.github/workflows/release.yml`. Exit: `dotnet build -c Release` clean with warnings-as-errors; `scripts/build.sh` produces `dist/Jellywave_0.1.0.0/` and `dist/Jellywave_0.1.0.0.zip`; `manifest.json` carries the zip's MD5; `check-manifest.sh` passes.
2. **Channel + stream.** `JellywaveChannel`, embedded logo, config class with defaults (no page yet). Exit: deployed on the Jellyfin test host; `GET /Channels` lists it; `GET /UserViews` includes it; web My Media shows the tile with logo; click → one item → play → audio in browser, Relay mode (ffmpeg job visible in dashboard / log).
3. **Config page.** `configPage.html` with the fields above and "Fetch from station"; `LastChangedUtc` on save. Exit: switching Delivery to Direct and back changes `PlaybackInfo` output (`SupportsDirectPlay`, `TranscodingUrl`) after cache bust.
4. **Now-playing service.** `NowPlayingState`, `NowPlayingService`, registrator (`StationClient` already landed with card 3). Exit: after a track change on the station host, reopening the tile shows the new "Now playing" overview within one poll interval; log shows no errors over 30 min.
5. **Docs, release, closeout.** `README.md` (install from the repository URL, configuration, limitations), `docs/DESIGN.md`, `docs/PLAN.md` current, `CLAUDE.md` for agents. The deploy helper stays untracked (HI decision 2026-10-26: users install through the plugin repository). Exit: docs match behaviour; `scripts/check-manifest.sh` passes. Once HI has pushed `main` and tagged `v0.1.0.0`: the release workflow succeeds, and installing from the repository URL in the test host's dashboard works (this last check is after push, so it closes when HI reports it).

Cross-client checks after card 4 (HI performs, MI supplies the checklist): Android app (web wrapper) plays; Roku and Android TV open the tile and play. Roku/Android TV are bonus and may need a follow-up card.

## Verification (end to end)

```bash
# build
cd ~/DEV/jellywave && scripts/build.sh && ls dist/Jellywave_0.1.0.0/
# ship
scp -r dist/Jellywave_0.1.0.0 local/deploy-jellyfin.sh <jellyfin-host>:~/jellywave-dist/   # deploy helper is untracked
# HI on the Jellyfin test host
bash ~/jellywave-dist/deploy-jellyfin.sh
# API checks (token from a browser session or an API key HI creates)
curl -s -H "Authorization: MediaBrowser Token=$TOKEN" http://<jellyfin-host>:8096/Channels | jq '.Items[].Name'
curl -s -H "Authorization: MediaBrowser Token=$TOKEN" "http://<jellyfin-host>:8096/UserViews?userId=$USERID" | jq '.Items[] | {Name, Type}'
curl -s -H "Authorization: MediaBrowser Token=$TOKEN" "http://<jellyfin-host>:8096/Channels/$CHANNELID/Items?userId=$USERID" | jq '.Items[] | {Name, Type, RunTimeTicks, Overview}'
# playback info shows Relay (no SupportsDirectPlay, has TranscodingUrl)
curl -s -X POST -H "Authorization: MediaBrowser Token=$TOKEN" -H 'Content-Type: application/json' \
  "http://<jellyfin-host>:8096/Items/$ITEMID/PlaybackInfo?userId=$USERID" -d '{}' | jq '.MediaSources[] | {Path, SupportsDirectPlay, SupportsTranscoding, TranscodingUrl, IsInfiniteStream}'
# server log
grep -i 'jellywave\|channel' "$(ls -t /var/log/jellyfin/log_*.log /var/log/jellyfin/jellyfin*.log 2>/dev/null | head -1)" | tail -50
```

Manual: web My Media → tile → item → play; confirm audio, pause/stop work, no seek bar. Repeat in the Android app. Roku / Android TV: open tile, play item.

## Risks and open items

| Item | Severity | Handling |
|---|---|---|
| ffmpeg progressive transcode of an infinite ICY MP3 (Relay) | Required to verify | Card 2 exit criterion. Fallback: Direct mode, or a plugin-hosted proxy endpoint in phase 2. |
| Roku / Android TV behaviour inferred from source, untested | Suggested | Bonus scope; test after card 4. |
| ICY in-stream titles are dropped by relay | Nit | Now-playing poller covers it. |
| Renaming the station changes the channel id and orphans the old entry | Suggested | Document; Jellyfin's "Refresh Internet Channels" task cleans uninstalled channels. |
| Non-admin users need `EnableAllChannels` or the channel enabled | Suggested | Note in README; check on the Jellyfin test host test users. Closed by card 6 (`GrantAccessToAllUsers`). |
| Private station mode (listener auth) on SUB/WAVE | Out of scope v1 | Would need `?auth=` on the stream URL and a secret in config → Hard Stop review when it comes. |

## Hard Stops in this wave

None expected in code. Restarting Jellyfin on the Jellyfin test host is done by HI via the script. No auth, schema, or credential changes. `git push`, GitHub remote creation, and tagging the first release remain HI's; the manifest's `sourceUrl` and `imageUrl` assume the slug `b3p3k0/jellywave` until HI says otherwise.

## v1 closeout status (2026-10-27)

| Card | Accepted | Runtime evidence |
|---|---|---|
| 1 scaffold, build, manifest | 3ebcd03 | plugin loads on Jellyfin 12.3.0 |
| 2 channel + stream | c63aa14 | tile in My Media, Relay playback in web; "fatal playback error" fixed by the nominal runtime |
| 3 config page + probe | b096685 | "Fetch from station" filled tagline and stream format on the test host |
| 4 now-playing poller | 9953caf | 24 overview updates in 64 min, 0 errors |
| 5 docs, release | docs ec8efde; closed 2026-10-27 | GitHub release v0.1.0.0 published with `Jellywave_0.1.0.0.zip` on 2026-10-27; manifest commit step fixed (21da132); HI confirmed on 2026-10-27 that installing Jellywave from the repository manifest URL in the dashboard catalog works |
| 6 visible to every user | c370a24 (2026-10-27) | plugin 0.2.0.0 on the test host: first pass 39 granted, 2 already visible, 0 blocked; pass after the next restart 0 granted, 41 already visible, 0 blocked; no ERR/WRN from the plugin; HI confirmed in the admin interface and as a regular user on another device |
| 7 richer live item (phase 2) | accepted 2026-10-27 (0228d43 implementation, 8edfa75 review fixes) | plugin 0.3.0.0 on the test host, loaded 13:09:58 UTC: "Live item cover updated" at 13:10:03, 13:12:44 and 13:15:24 (one per track), 2 "Live item overview updated" lines in the same window (the first poll's text matched the pre-restart text), 0 ERR/WRN mentioning Jellywave, cover or ImageSaver, 0 "Deleting previous image" lines (all covers JPEG, overwritten in place); HI: cover changes with the track after a list reload. Open: the mini-player thumbnail keeps its playback-start snapshot (documented; the card 8 page script will refresh it) |

Found during closeout: users need channel access (`EnableAllChannels` or the channel in `EnabledChannels`); 39 of 41 test-host users had neither. The interim bulk script was removed once card 6 (in-plugin grant) landed; HI wanted no scripts or manual passes for end users.

## Phase 2 closeout status (2026-10-27)

Test host: Jellyfin 12.3 with File Transformation 3.0.0.0 and SeerrFin installed; plugin 0.3.0.0 built from 13b9ebf (installed dll md5 895ad751 equals the local build). Review: one RA code review on d95f759..b1f9fe9 plus 929992e found no Blocker and five Suggested items, all addressed in c920130 and 13b9ebf; the acceptance review re-verified each fix in the final code.

| Card | Accepted | Runtime evidence |
|---|---|---|
| 8s spike: page injection | d95f759, 4f92c23 (decision A by HI: File Transformation as an optional extra; without it the tile keeps working and the config page says why) | "Station page registered with File Transformation 3.0.0.0" once per restart (6 lines on 2026-10-27), 0 File Transformation errors; the first cut's injection never ran because File Transformation runs one pipeline per pattern (fixed by joining the plain-name pipelines in 4f92c23) |
| 8a feed endpoints | d95f759, b1f9fe9, c920130 | `Feed`, `Request`, `Request/{id}` behind `[Authorize]`, `WebUi` behind `RequiresElevation`, assets anonymous; no station URL in any response; one Information line per accepted request: `Listener request "3fe3e37b-..." accepted for user "692533d8..."` at 14:41:18 UTC; 0 ERR/WRN mentioning Jellywave all day |
| 8b station page | d95f759, 4f92c23, 916d53d, 929992e, c920130, 13b9ebf | HI: header renders on the channel page with cover, track, DJ, show, listeners; cover, bar thumbnail and grid card rotate on track change ("loading immediately at or even slightly before the new track"); Firefox and Chrome on Ubuntu 26.04 (Intel and AMD machines) and the Jellyfin Android app 2.7.3 (Pixel 9, GrapheneOS); after 13b9ebf Play/Stop reverts after the bar's stop and pause/resume keeps the bar text; 32 "Live item cover updated" lines that day. Bugs HI found and DA fixed: injection never ran (4f92c23), refresh loop died on "player cannot be null" (916d53d), Stop button did not revert (13b9ebf) |
| 8c requests | b1f9fe9, c920130 | HI: a request sent from the page reached the station ("watching it come thru on the subwave UI") and the DJ's answer came back in Jellyfin; "all smoke tests are clear" |
| 8d extras: portrait, up next, recent, like, schedule, lyrics | a923124, 427ced9, af6e8cc, 4895824 (accepted 2026-10-27) | built from 4895824 (installed dll md5 c84266c0 equals the local build); after 15:44:01 UTC 0 ERR/WRN mentioning Jellywave, page registered, covers updating. HI: portrait next to the DJ line, "Up next" line, Recently played and Today panels render; heart: first tap fills and shows 1, second tap stays filled with no increment ("acts as expected, no double count"). Bugs HI found and DA fixed in 4895824: avatar 401 (Jellyfin honours `?api_key=` only under `EnableLegacyAuthorization`; the portrait is now fetched with the session and shown as a blob URL) and "h is not defined" in `sendLike` (the panel reveals were spliced into the wrong function, which also hid the Lyrics and schedule panels). Open: the lyrics panel shows a sentence instead of lines although lyrics are on at the station; the station's payload shape matches the plugin's parser, so the sentence names the cause (switched off 403, private station 401, track no longer on air 409, still looking, no lyrics, music server 503); HI investigates on the station side |
| 9 station page layout and 8d polish | 4d67e6f, c6ea146 (decision A by HI 2026-10-27: "fill the width"; accepted 2026-10-27) | built from c6ea146 (installed dll md5 bfbcd5bd equals the local build); after 16:12:08 UTC 0 ERR/WRN mentioning Jellywave. HI: "looks a lot better" after 4d67e6f; "all pass" after c6ea146 on desktop and narrow widths (request box drops under the text, then everything stacks; name and Send on one row). Heart double tap gives no feedback and no error, by design: the second tap while the first is in flight is ignored, afterwards the station answers `alreadyLiked` with the same count |
| 10 poller health on the configuration page, card 9 nits | 0505d94 (accepted 2026-10-27) | built from 0505d94 (installed dll md5 2e523493 equals the local build), loaded 16:22:42 UTC, 0 ERR/WRN mentioning Jellywave since; v0.3.2.0 released the same day and its zip matches the manifest entry. HI: the plugin page's "Poller status" line shows and "Refresh status" works ("success!"), the radio page's Today panel "lgtm", and wrote "ACCEPTED" |
| 11 card 10 nits: health snapshot, unknown reachability before the first poll, takeover by show id | cf9d672 (accepted 2026-10-27) | built from cf9d672 (installed dll md5 75eb9da2 equals the local build), loaded 16:34:14 UTC, 0 ERR/WRN mentioning Jellywave since; v0.3.3.0 released the same day and its zip matches the manifest entry. HI checked the plugin page status line and the radio page's Today panel: "looks good! lets send it" |

Verification at acceptance (13b9ebf): `dotnet build -c Release` 0 errors; `dotnet test --no-build` 96/96; `scripts/check-manifest.sh` 10 ok lines; `node --check Web/ui.js` clean; host-name grep empty; `git ls-files local/` empty.

Verification at acceptance (4895824, card 8d): `dotnet build -c Release` 0 errors; `dotnet test --no-build` 99/99; `scripts/check-manifest.sh` only ok lines (props 0.3.1.0 ahead of the manifest, unreleased); `node --check Web/ui.js` clean; host-name grep empty; `git ls-files local/` empty. Review found no Blocker. Suggested, not required for acceptance: a like refusal (429 after the 2 s cooldown, 409, 500) replaces the page's like status with `Liked=false, Count=0`, so the heart unfills until the next track (keep the previous status and show only the message); the schedule panel refetches only on toggle, so the "now" mark goes stale while it stays open past the hour, and `BuildToday` ignores the station's timed takeover (`override`); `ScheduleCache` retries the station on every call while stale, and a first-ever fetch that throws `NotSupportedException` (non-JSON content type) is not in the `Schedule` action's catch list; a failed `fetchLike` is not retried until the next track; the DJ portrait's blob URL is not revoked when the DJ line is hidden. Card 9 (4d67e6f) addressed the like refusal, the schedule refetch, the `ScheduleCache` retry and `NotSupportedException`, the `fetchLike` retry, and a failed portrait (kept hidden until the DJ changes); still open as Nits: `BuildToday` ignores the station's timed takeover (`override`) and the blob URL is not revoked when the DJ line hides.

Verification at acceptance (c6ea146, card 9): `dotnet build -c Release` 0 errors; `dotnet test --no-build` 99/99; `scripts/check-manifest.sh` only ok lines (props 0.3.2.0 ahead of the manifest, unreleased); `node --check Web/ui.js` clean; header markup balanced (34 opening, 34 closing tags) and every `.jellywave-*` selector the script queries inside the header exists in it; host-name grep empty; `git ls-files local/` empty. Review found no Blocker; the only changes are CSS, the page script, the schedule cache's retry timestamp, one extra exception type in the `Schedule` catch, and the version bump; no endpoint, authorization or station-bound input changed.

Verification at acceptance (0505d94, card 10): `dotnet build -c Release` 0 errors; `dotnet test --no-build` 100/100; `scripts/check-manifest.sh` only ok lines (props 0.3.3.0 ahead of the manifest 0.3.2.0, unreleased); `node --check Web/ui.js` clean; host-name grep empty. Review found no Blocker: `GET /Jellywave/Health` sits under the controller's class-level `RequiresElevation` with no method override and returns only the poller's own state (`LastError` is the station call's exception message: the station client sends no credentials and the configured base URL carries none, so at most the station's host and port appear, on an admin page); every `PollerHealth` field is read and written under one lock; the loop sets `Running` after the startup wait and clears it on exit, and records `Failure` on both the station-failure path and the unexpected-exception catch; `BuildToday` collapses whitespace on both sides and compares show names case-insensitively, so a grid block is "now" only when the hour matches and the station's active show has the same name, and `Takeover` is set only when the station names a show and no block is current (test: "the ville" keeps the block, "Pop-up Special" unmarks it); the schedule panel compares `JSON.stringify` of the response with the one it last rendered, so the five-minute refetch rebuilds the DOM only when the data changed (`Hour` changes hourly, which moves the mark); the portrait's blob URL is revoked and `data-src` cleared when the DJ line hides, which also drops an in-flight fetch. Nits, not required: the controller reads the five health fields one lock at a time rather than as one snapshot; `Reachable` starts true, so for the seconds between "poller started" and the first poll the line reads "station reachable · last success never"; the `fetchSchedule` comment says ten minutes where the code uses five; the schedule's takeover rule trusts that the station's active-show name equals the grid's show name, so a station that reports a differently spelt name during a normal block would show it as a takeover. The two card 9 Nits (takeover, blob revoke) are closed.

Verification at acceptance (cf9d672, card 11): `dotnet build -c Release` 0 errors; `dotnet test --no-build` 100/100 (the takeover test now covers the same id, a renamed show in its own slot, a takeover by another id, and an unnamed takeover shown by its id; the feed fixture asserts `Context.ActiveShow.Id`); `scripts/check-manifest.sh` only ok lines (props 0.3.4.0 ahead of the manifest 0.3.3.0, unreleased); `node --check Web/ui.js` clean; host-name grep empty; `git ls-files local/` empty. Review found no Blocker: the four card 10 Nits are closed (`PollerHealth.Snapshot()` reads the five fields under the one lock and the controller reads that snapshot; `Reachable` is `bool?`, null until the first `Success` or `Failure`, and the page says "waiting for the first poll" while it is null or undefined; the `fetchSchedule` comment says five minutes, matching `ScheduleCache.Lifetime` and the page's 300000 ms guard; `BuildToday` takes the active show's id from `context.activeShow.id`, which the station publishes by spreading its context into `/api/now-playing` while the top-level `activeShow` is reshaped without the id, and compares it ordinally with the grid's show ids, so a block is "now" only when the hour matches and the ids agree, and `Takeover` is the station's name, or its id when it names none). Nothing security-relevant changed: `GET /Jellywave/Health` keeps the class-level `RequiresElevation`, only `Reachable` became nullable; `ScheduleBlock.ShowId` adds the station's show key to an `[Authorize]` response, which carries no URL or credential; no new input reaches the station. Nits, not required: when the station's feed carries no `context.activeShow` (a station older than that field, or nothing on air) `activeShowId` is empty, so the grid alone decides "now" and no takeover can be named (the name-only rule is gone, by design); `SetRunning(true)` on a restart keeps the previous poll's `Reachable`, so the line reads the last result rather than "waiting" until the next poll.

Open after card 8d: the lyrics panel on HI's station (see the 8d row) and the poller-health item on the configuration page (closed by card 10); the Android app needs a force-close to refetch `index.html` after the plugin is first installed (README notes it); the page's DOM selectors (`.itemsContainer[data-parentid]`, `.nowPlayingBar`, `.card`) are jellyfin-web internals and may move between releases, degrading to "nothing happens" (DESIGN notes it).

## Post-approval housekeeping (MI)

- Commit this plan into the repo as `docs/PLAN.md` on the first checkpoint commit.
- Save memory notes: the Agent Charter URL and response contract; host roles (the Jellyfin test host = Jellyfin 12.3 test server, user access + sudo password; the station host = SUB/WAVE, `kevin@<station-host>` works, `claude@<station-host>` needs `~/.ssh/claude_framework`).

## Phase 2: live station page (decided 2026-10-27)

HI's ideas: current track, artist, album, show, DJ and album art in Jellyfin, plus a box to use the
station's listener-request feature. Research facts (2026-10-27, SUB/WAVE 1.28.0 source and Jellyfin 12.3
source) that shape the cards:

- Every Jellyfin client copies the playing item when playback starts and never re-reads it, so the
  playing bar cannot change mid-stream whatever the server writes (`playbackmanager.js`
  `getNowPlayingItemForReporting`; `nowPlayingBar.js` only re-reads user data). Live information
  therefore lives on a custom page; standard clients get the state at the moment a list loads or
  playback starts.
- Channel items keep a remote image URL only when first created (`ChannelManager.GetChannelItemEntityAsync`
  sets `ImageUrl` just when the item has no Primary), but a plugin can save a new Primary any time with
  `IProviderManager.SaveImage(item, stream, mime, Primary, null, ct)` and persist with
  `UpdateItemAsync(..., ItemUpdateType.ImageUpdate)`. The image tag is `MD5(item.Path + image.DateModified)`,
  and `ImageSaver` sets `DateModified` from the new file, so clients refetch on the next list load.
- SUB/WAVE: art is `GET /api/cover/{subsonic_id}` (public, 512 px, `Cache-Control` one day, **502 with an
  empty body when there is no art**); DJ portrait `GET /api/persona-avatar/{id}` (1x1 PNG when unset);
  requests `POST /api/request {text<=280, name?<=40}` -> 202 `{requestId}` then `GET /api/request/{id}`
  for the outcome; refusals are 503 (disabled or autopilot), 429 with `Retry-After` (per-IP cooldown
  60 s, 8 per hour per IP, 30 per hour global, one pending per IP); no login, no dedication field, no
  listener-side library search. Schedule `GET /api/schedule`, next track and history in `GET /api/state`
  (`upcoming[0]`, `history`), listeners in now-playing. No push channel; the station's own player polls
  every 5 s. `X-Frame-Options SAMEORIGIN` from Caddy: the station page cannot be framed.
- `privatePlayer` only locks the station's own web pages; `listenerAuth` needs `?auth=` on the stream
  URL (a secret in plugin config: Hard Stop review when it comes).

HI decisions 2026-10-27:

| Decision | Choice |
|---|---|
| Station access from the page | Everything goes through plugin endpoints on the Jellyfin server (feed, cover, avatar, request). Lets HI close SUB/WAVE to the world later while Jellyfin users keep access. |
| Request limits | Honour the station's own limits (all Jellyfin users share the server's per-IP budget) and explain them in our UI. No extra limiter in the plugin. |
| Home carousel tile | Stays the station logo (consistent library images). The live item inside the library shows the current cover. |
| Order | MI's call: card 7 first (every client benefits), then the page. |

### Cards

7. **Richer live item.** Poller downloads the current cover (`/api/cover/{id}`) on track change and saves it
   as the live item's Primary; falls back to the embedded logo when the station has none (today the item
   has no image at all). Overview gains "Up next: show with presenter at time" from
   `context.showHandover`. **Dropped in review:** writing `Album` = "Artist – Title" for the playing bar,
   because `Album` is part of `Audio.GetUserDataKeys()` and `SaveUserData` keeps a row per key, so every
   track would leave orphan user-data rows. `Artists` stays untouched. Config: `CoverArtEnabled` (default on).
   Exit: on the test host, opening the tile shows the current cover and the longer line; after a track
   change and a list reload the cover changes; log shows one "cover updated" line per track, 0 errors
   over 30 min; a track without art shows the logo.
8s. **Spike: page injection.** How a plugin puts a user-facing page and a top-bar entry into jellyfin-web
   12.3 for signed-in non-admin users: File Transformation plugin (SeerrFin precedent) versus any native
   hook. Output: a one-page decision in DESIGN.md and a throwaway proof on the test host. Exit: a
   non-admin user reaches a "Hello" page from the top bar in web and in the Android app.
8a. **Feed endpoints.** Signed-in-user (not admin) endpoints under `/Jellywave/`: `Feed` (now playing,
   DJ, show, next show, listeners, the channel and live item ids and the item's image tag, never the
   station URL), `Request` (POST) and `Request/{id}`. **Corrected in implementation:** no `Cover`/`Avatar`
   byte proxies; the page draws the cover through Jellyfin's own image API from the live item's Primary
   (card 7 already saves it), and the DJ portrait is deferred. `POST Request` always answers 200 with
   one mapped outcome object (`Status`, the station's message, `RetryAfterSeconds`) rather than the
   station's status code, so the page shows the station's words without handling HTTP errors.
8b. **Station page.** Inside the Jellyfin web app (so the Android app gets it): cover, track, DJ with
   tagline, show and what's next, listener count; play/stop drives Jellyfin's own player on the live
   item so the bar, remote control and lock-screen controls keep working; polls `Feed` every 5 s while
   visible. **Corrected in implementation:** the page is the channel's own library page (header above the
   grid), not a drawer tab, because the default layout has no user drawer; the legacy layout gets a
   drawer link to it. While the station plays the script repaints the bar, the grid card and the
   browser media session. DJ portrait and recent tracks are deferred to 8d.
8c. **Requests.** Text box, optional name, submit, outcome from the status endpoint ("queued, position
   3", "no match", "cooldown, try in 40 s"); helper text explains the shared budget.
10. **Poller health and the card 9 nits** (2026-10-27): `PollerHealth` snapshot plus `GET /Jellywave/Health`
   and a status line with "Refresh status" on the configuration page; `BuildToday` takes the station's active
   show so a timed takeover unmarks the grid and is named on the page; the schedule panel re-renders only
   when its data changed; the portrait's blob URL is released when the DJ line hides; inert flex CSS removed.
   Version 0.3.3.0.
11. **Card 10 nits** (2026-10-27): health read as one snapshot; reachability unknown until the first poll (the page
   says "waiting for the first poll"); takeover detection by the station's show id (`context.activeShow.id`) rather
   than the name, so a renamed show in its own slot is not a takeover; stale comment. Version 0.3.4.0.
8d. **Small extras on the page** (after 8c, each its own `wip:`): DJ portrait (`/api/persona-avatar/{id}`
   proxied), recent tracks (`/api/state.history`), like button (`/api/like`), today's schedule strip
   (`/api/schedule`), lyrics when the station enables them. **Implemented 2026-10-27** in a923124
   (portrait, up next, recent, like) and 427ced9 (schedule, lyrics); lyrics are shown as plain lines
   without a timing highlight (stream buffering makes a client-side clock unreliable). Accepted
   2026-10-27 after the fixes in 4895824 (see the phase 2 closeout table).
9. **Station page layout and 8d polish.** HI decision 2026-10-27, option A ("fill the width"): the header
   is a grid, cover | now playing, DJ line, Play/Stop | request box, with the three collapsible panels
   (Recently played, Today on the station, Lyrics) in an auto-fit row underneath; under 60em the request
   box drops under the text, under 40em everything stacks; name and Send share one row (c6ea146). Folds
   in the 8d review's Suggested items: a refused like keeps the heart and count and only shows the
   message; the schedule panel refetches every 5 min while open; `ScheduleCache` waits `RetryDelay`
   (1 min) before retrying a failed refresh and the action also catches `NotSupportedException`; a failed
   like status is retried next tick and lyrics show "Loading…" on a track change; a failed portrait stays
   hidden until the DJ changes. Implemented in 4d67e6f and c6ea146 (version 0.3.2.0); accepted 2026-10-27.

Also queued, not on the page: poller health on the config page (last successful poll, last error).

### Risks

| Item | Severity | Handling |
|---|---|---|
| No native user-page hook in jellyfin-web; File Transformation adds a second plugin for admins to install | Required to verify | Spike 8s before 8b; README documents the dependency if it stays. |
| Shared request budget through the proxy | Accepted by HI | Helper text; HI can raise limits on the station side. |
| Cover download every track adds a file write under the item's metadata folder | Nit | `ImageSaver` deletes the previous file; one write per ~3 min. |
| `listenerAuth` mode needs a secret in config | Hard Stop later | Out of scope until HI closes the station. |

## Phase 2 candidates (not in this plan)

- ~~Custom in-Jellyfin station page~~ (planned above as phase 2, cards 7–8d).
- ~~Plugin-side "make visible to all users" control, and a hook for newly created users~~ (shipped as wave 2, card 6).
- Plugin-hosted byte-for-byte stream proxy endpoint (keeps ICY, avoids ffmpeg).
- Multi-station support (SUB/WAVE profiles) as multiple channels.

## Wave 2, card 6: visible to every user (decided 2026-10-27)

HI decision: no scripts, no manual pass. Install from the repository, configure in the plugin UI, and the
station is visible to everyone. Elevated risk (authorization): full process, security pass, HI review before
acceptance.

### Behaviour

- Config `GrantAccessToAllUsers` (bool). When on, the plugin makes sure every user can see the channel by
  adding the channel's id to that user's `EnabledChannels` preference when the user has neither
  `EnableAllChannels` nor the id already. Nothing else in the policy is touched: `EnableAllChannels` is never
  set, no other channel is added or removed, `BlockedChannels` is left alone (a user explicitly blocked from
  the channel stays blocked, which is what the admin asked for).
- Runs: after core startup (same hosted-service pattern as the poller), on every config save (the config
  page calls an admin endpoint `POST /Jellywave/GrantAccess` that also reports counts), and on
  `UserCreatedEventArgs` via a plugin-registered `IEventConsumer<UserCreatedEventArgs>` so provisioning
  tools (JFA-GO and friends) cannot leave new users out. **Added in review:** also on `UserUpdatedEventArgs`,
  because creation flows post a policy right after creating the user, which replaces the enabled list.
- HI decisions 2026-10-27: **off by default** (the admin ticks it once; the page explains what it does); turning it off **stops future grants and revokes nothing**.
- The config page shows the result of the last run: granted now / already visible / blocked by an explicit block list.
- Channel id: the `Channel` entity looked up by name (`GetItemList{IncludeItemTypes=[Channel]}`), created
  on first use through `IChannelManager.GetChannelsInternalAsync` once core startup has completed.

### Mechanics (Jellyfin 12.3, verified)

- `User.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels)` / `User.SetPreference<Guid>(...)`
  (`Jellyfin.Data/UserEntityExtensions.cs`), persisted with `IUserManager.UpdateUserAsync(user)`
  (`UserManager.cs:213`, locks per user, re-reads the tracked entity).
- `UserManager.CreateUserAsync` publishes `UserCreatedEventArgs` (`UserManager.cs:365`); plugins register
  `IEventConsumer<T>` in DI (webhook plugin precedent).
- `Channel.IsVisible` has no administrator exemption, so administrators are treated like everyone else.

### Security pass items

- Only the plugin's own channel id is ever written; the code path cannot add any other id.
- Admin-only endpoint for the manual trigger (`Policies.RequiresElevation`), same as the probe.
- Log one Information line per run with counts, never per user; no user names in logs.
- Tests: `AccessGranter.Classify` / `AddToEnabledChannels` on real `User` entities, and `GrantAllAsync` against a fake `IUserStore` and `IChannelLocator`; no network, no database.

### Cards

6a. `AccessGranter` service + tests + config field + startup and user-created hooks.
6b. Config page section + admin endpoint + README/DESIGN update. Exit: fresh non-admin user on the test
    host sees the tile without any admin action; counts shown on the page; LESSONS block.
