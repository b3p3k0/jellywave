# Jellywave: SUB/WAVE radio as a "My Media" entry in Jellyfin 12.x

Status: cards 1–18 accepted; wave 3 (cards 12–18 plus 15b, second security review) closed 2026-10-27 with v0.4.0.0, v0.4.1.0 and v0.4.2.0, each host-tested and its zip verified against the manifest (see the Wave 3 section). Cards 1–11: (1–4: 3ebcd03, c63aa14, b096685, 9953caf; 5 docs: ec8efde; 6: c370a24; 7: 2026-10-27, commits 0228d43 and 8edfa75; 8s/8a/8b/8c: 2026-10-27, commits d95f759 through 13b9ebf; 8d: 2026-10-27, commits a923124 through 4895824; 9: 2026-10-27, commits 4d67e6f and c6ea146; 10: 2026-10-27, commit 0505d94; 11: 2026-10-27, commit cf9d672, see the phase 2 closeout table). v1 is closed: release v0.1.0.0 is published and HI confirmed installing from the manifest URL in the dashboard catalog on 2026-10-27. Open: the lyrics panel shows a sentence instead of lines on HI's station (station side, not a blocker); v0.3.4.0 is released (manifest de8d8dd); 0.4.0.0 is on `wave3-hardening` and untagged until card 16 is accepted. Wave 3 (second security review remediation, cards 12–18) approved 2026-10-27; see the Wave 3 section at the end. Original plan approved 2026-10-26; sections marked "corrected"/"changed" record what implementation taught. Role at writing: PA. Track: full process. Phase 4 (Live Radio, cards L0–L7) planned 2026-10-27 on branch `live-radio`; L0 done.
Research performed 2026-10-26 against Jellyfin v12.3 source, jellyfin-web, jellyfin-roku, jellyfin-androidtv, the plugin template, the SUB/WAVE repo, and live probes of the Jellyfin test host and the station host.

## Context

HI runs Jellyfin 12.3.0 on the Jellyfin test host and a SUB/WAVE internet-radio station ("IHateRadio") on the station host. Goal: a tile in Jellyfin's My Media row, alongside Live TV, that plays the station with transport controls on web, Android, and (bonus) Roku / Android TV. Stream audio is the must-have; the SUB/WAVE web player is a later phase.

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
- the Jellyfin test host: Jellyfin 12.3.0 as systemd service, plugins in `/var/lib/jellyfin/plugins/<Name>_<ver>/` owned `jellyfin:jellyfin`, ffmpeg at `/usr/lib/jellyfin-ffmpeg/ffmpeg`. Existing `meta.json` shape: see SeerrFin's (`targetAbi` "12.0.0.0", `assemblies` list, `status` Active).
- Station: SUB/WAVE 1.26.0: station "IHateRadio", public (no listener auth, no private player), MP3 192 kbps 44.1 kHz stereo at `http://<station-host>:7700/stream.mp3`, CORS `*`, ICY headers present. Public JSON: `GET /api/now-playing` (`nowPlaying.title/artist/album/subsonic_id/duration`, `context.activeShow.name`), `GET /api/state` (`station.name`, `privacy`), `GET /api/health` (`{"status":"on-air"}`). Tune-in files at `/listen.pls`, `/listen.m3u`.
- SUB/WAVE is not modified by this plugin; it only reads the station's public API and stream.

## Architecture

One plugin assembly, server-side only. No jellyfin-web changes.

```
~/DEV/jellywave
├── Jellyfin.Plugin.Jellywave.slnx
├── Directory.Build.props            # version 0.1.0.0
├── build.yaml                       # jprm-style metadata (name, guid, targetAbi 12.0.0.0 (corrected in card 12: 12.3.0.0), framework net10.0)
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
│   ├── Api/JellywaveController.cs         # admin-only routes for the config page (probe: POST /Jellywave/Probe since L3)
│   ├── Services/NowPlayingService.cs      # IHostedService poller
│   └── Assets/logo.png                    # embedded; channel Primary image (temporary mark; swap for station art later)
├── scripts/
│   ├── build.sh                           # dotnet publish -c Release → dist/Jellywave_<ver>/{dll, meta.json, thumb.png} + zip
│   ├── update-manifest.sh                 # writes the version entry (checksum, sourceUrl, timestamp) into manifest.json
│   └── (untracked) local/deploy-jellyfin.sh  # dev helper HI runs on the test host; local/ is gitignored, not a deliverable
└── docs/
    ├── dev/PLAN.md                        # this plan, committed to the repo on approval (docs/PLAN.md until card 16)
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

`local/deploy-jellyfin.sh` (untracked dev helper; HI runs it on the Jellyfin test host): modeled on the SeerrFin test stack's deploy script. Steps: confirm systemd Jellyfin is active; show what it found; stop the service; back up any existing `Jellywave_*` folder to `~/jellywave-plugin-backup-<ts>.tar.gz`; remove old `Jellywave_*`; copy the staged folder in; `chown -R jellyfin:jellyfin`; start; tail the log for `Jellywave` and `Loaded plugin`. `--rollback` restores the latest backup. MI copies `dist/Jellywave_<ver>/` and the script to `<jellyfin-host>:~/jellywave-dist/` with `scp`; HI runs `bash ~/jellywave-dist/deploy-jellyfin.sh`.

## Task cards (DA implements one at a time; RA accepts each)

1. **Scaffold + build + manifest.** Copy the template, rename to Jellywave, bump to net10.0 / 12.3.0, new GUID, `git init`, first `wip:` commit. Add `manifest.json`, `build.yaml`, `scripts/build.sh`, `scripts/update-manifest.sh`, `scripts/check-manifest.sh`, and `.github/workflows/release.yml`. Exit: `dotnet build -c Release` clean with warnings-as-errors; `scripts/build.sh` produces `dist/Jellywave_0.1.0.0/` and `dist/Jellywave_0.1.0.0.zip`; `manifest.json` carries the zip's MD5; `check-manifest.sh` passes.
2. **Channel + stream.** `JellywaveChannel`, embedded logo, config class with defaults (no page yet). Exit: deployed on the Jellyfin test host; `GET /Channels` lists it; `GET /UserViews` includes it; web My Media shows the tile with logo; click → one item → play → audio in browser, Relay mode (ffmpeg job visible in dashboard / log).
3. **Config page.** `configPage.html` with the fields above and "Fetch from station"; `LastChangedUtc` on save. Exit: switching Delivery to Direct and back changes `PlaybackInfo` output (`SupportsDirectPlay`, `TranscodingUrl`) after cache bust.
4. **Now-playing service.** `NowPlayingState`, `NowPlayingService`, registrator (`StationClient` already landed with card 3). Exit: after a track change on the station host, reopening the tile shows the new "Now playing" overview within one poll interval; log shows no errors over 30 min.
5. **Docs, release, closeout.** `README.md` (install from the repository URL, configuration, limitations), `docs/DESIGN.md`, `docs/PLAN.md` (now `docs/dev/PLAN.md`) current, `CLAUDE.md` for agents. The deploy helper stays untracked (HI decision 2026-10-26: users install through the plugin repository). Exit: docs match behaviour; `scripts/check-manifest.sh` passes. Once HI has pushed `main` and tagged `v0.1.0.0`: the release workflow succeeds, and installing from the repository URL in the test host's dashboard works (this last check is after push, so it closes when HI reports it).

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
| Renaming the station changes the channel id and orphans the old entry | Suggested | Document; Jellyfin's "Refresh Internet Channels" task (the task is named 'Refresh Channels') cleans uninstalled channels. |
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

- Commit this plan into the repo as `docs/PLAN.md` on the first checkpoint commit (moved to `docs/dev/PLAN.md` in card 16).
- Save memory notes: the Agent Charter URL and response contract; host roles and access (kept in memory and `local/`, never in tracked files).

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

## Wave 3: remediation of the second security / integrity review (v0.3.4.0)

Role at writing: PA. Track: full process (authorization, release pipeline, public interface, many modules).

### Context

An independent reviewer checked main de8d8dd (v0.3.4.0) and recommends holding the next release.
MI verified the main claims against the repo and Jellyfin 12.3 source on 2026-10-27. All are **facts**:

- `DefaultAuthorizationHandler` (12.3) returns `Succeed` for any API key and never checks channel visibility. Every `/Jellywave` station route uses plain `[Authorize]`.
- `NamedClient.Default` uses a `SocketsHttpHandler` that keeps `AllowAutoRedirect` at its default (on). `StationClient` uses this client for every call.
- `ChannelManager.GetInternalChannelId` = `GetNewItemId("Channel " + name)`. `LiveItemFinder.FindChannel` and `LibraryChannelLocator` also match by name, case-insensitively.
- The csproj references Jellyfin 12.3.0 packages, but `build.yaml` says `targetAbi: "12.0.0.0"`.
- The File Transformation catalog returns `[]` to `User-Agent: Jellyfin-Server/12.3.0`; its newest entry targets 12.2.0.0.
- `TryGetServerStreamUrl` compares only `Host`. `TryParseBaseUrl` accepts userinfo, query and fragment.
- `BuildToday` uses `ToDictionary`, so duplicate ids throw. A null feed records neither success nor failure (`NowPlayingService.cs:187`).
- `ScheduleCache`, `NowPlayingState.StationState` and `LiveItemArtwork.Applied` carry no station identity. In `ui.js`, `fetchLike` does not check the song id, and the observer is a global `MutationObserver` with no debounce.
- The README contradicts itself (lines 62 and 82). `configPage.html:77` still mentions an "album line". PLAN.md lines 32 and 34 hold host-access notes (removed in card 16).

HI decisions (2026-10-27):

| Question | Decision |
|---|---|
| ABI | Claim **12.3 only**: `targetAbi 12.3.0.0`. |
| API keys on station routes | **Deny on every** `/Jellywave` user route (403). |
| Request name | **The listener keeps a free-text name for the DJ.** The server keeps the real Jellyfin user id with each request, for accounting behind the scenes. No plugin-side limiter; the shared station budget stays an accepted, documented risk. |
| Docs packet | Keep the packet and **move PLAN.md and LESSONS_LEARNED.md to `docs/dev/`**. Remove the host-access notes, fix the contradictions, add SECURITY.md, a threat model and a support matrix. No history rewrite. |

Settled earlier, not reopened: Relay is the default, the access grant is off by default, File Transformation stays optional.

### Finding triage

| ID | Finding | Disposition | Card |
|---|---|---|---|
| O1 | Built against 12.3, claims 12.0 | Fix: targetAbi 12.3.0.0, plus a check that the DLL's references and targetAbi agree | 12 |
| S5 | Release job holds a write token throughout | Fix: read-only build job, separate publish job, actions pinned by SHA, Dependabot | 12 |
| O13 | No push/PR CI | Fix: `ci.yml` with read-only token | 12 |
| S1/O2 | Station routes skip channel visibility; API keys get in | Fix: one access filter on every user route | 13 |
| S2 | Request receipts not bound to a user | Fix: receipt → user id map; any other caller gets 403 | 13 |
| T4 | Shared station identity, free-text name | Mitigate per HI: user id kept per request; budget accepted, documented | 13, 16 |
| S3/O5 | Redirect SSRF | Fix: plugin's own HTTP client, redirects refused | 14 |
| S4/O4 | URL authority / credentials | Fix: reject userinfo, query and fragment; compare scheme, host, port and userinfo | 14 |
| S6/O7 | Image MIME trusted, no decode limits | Fix: check magic bytes and header dimensions. Accept: GIF frame count (documented) | 14 |
| D1/O3 | Channel-name collision | Fix: find the channel by its derived id and fail closed when another channel has the same name. Accept: the name-derived id itself (Jellyfin API) | 15 |
| D3/O8 | User policy lost update | Mitigate: serialize the plugin's own grant passes. Accept: the window against other writers | 15 |
| O9 | Live-item whole-row write vs channel refresh | Accept: already narrowed (prepare first, then apply; reconciled every poll); Jellyfin has no partial update | — (threat model) |
| O10 | verify-jellyfin.sh can report another channel as success | Fix: select the channel by name; fail when it is missing or there is more than one | 15 |
| T1/O6 | File Transformation not offered for 12.3 | Mitigate: support matrix and README say so, with the upstream issue link; vendor side | 16 |
| O14 | Doc drift, internal diary | Fix per HI decision | 16 |
| D2 | Data from two stations mixed after a station switch | Fix: caches keyed by station origin; browser state keyed by server and user | 17, 18 |
| D4/O11 | Missing art refetched every poll | Fix: negative cache with a bounded TTL | 17 |
| D5 | Null feed leaves stale state | Fix: a null feed counts as a station failure | 17 |
| D6 | Duplicate schedule ids → 500 | Fix: first id wins | 17 |
| T2 | Global MutationObserver | Fix: boot guard, debounce, cheap checks | 18 |
| T3 | Like response applied to the wrong track | Fix: capture the song id and discard stale replies | 18 |
| O12 | Test gaps | Fix: tests land with each card (listed per card) | all |
| — | Public `wip:`/`accept:` history | Accept: the charter forbids history rewrites | — |
| — | CHANGELOG.md | Not added: GitHub releases and the manifest changelog already record each version | — |

### Branch and release

- Work happens on the branch `wave3-hardening`, cut from main. HI pushes the branch and merges it with `--no-ff`, which keeps the `wip:` trail.
- Each card bumps `Directory.Build.props`. **No tag until cards 12–16 are accepted**, because those cover every P0 item. Then merge to main, test on the host, and tag `v0.4.0.0`. Cards 17 and 18 follow as v0.4.1.0 and v0.4.2.0, one tag each, as usual.
- One card at a time. RA must be a different session or vendor than DA.

### Cards

#### 12. ABI, release pipeline, CI (O1, S5, O13)

**Accepted 2026-10-27** (ec952e7 plus review fixes in the acceptance commit). RA: no Blocker, nothing Required; folded in: the publish job refuses a manifest entry whose targetAbi differs from the zip's (Suggested 1), DESIGN/README wording (Nits). Deferred to Dependabot: moving the v4 action pins to current majors (Node 20 deprecation notice). CI run 40547909389 green. Still open until the v0.4.0.0 tag: the release path itself.

- `build.yaml`: set `targetAbi: "12.3.0.0"`. Published manifest entries are not touched.
- `scripts/check-manifest.sh`: assert that the Jellyfin.Controller and Jellyfin.Model package versions in **both** csproj files equal targetAbi's first three parts. It already reads build.yaml through `scripts/lib.sh`.
- Test `AbiTests`: every `Jellyfin.*` and `MediaBrowser.*` entry in `typeof(Plugin).Assembly.GetReferencedAssemblies()` must have a version ≤ targetAbi. Read targetAbi with one anchored regex over the single-line `build.yaml`; that file's values are single-line by rule. This is the DLL-level check the reviewer asked for.
- `release.yml`:
  - Top level: `permissions: {}`.
  - Job `build` (`contents: read`): checkout with `persist-credentials: false`, tests, `build.sh`, `check-manifest.sh`, then upload the zip and `meta.json` as an artifact.
  - Job `publish` (`needs: build`, `contents: write`): download the artifact, check out main, run `gh release create`/`upload` and `update-manifest.sh`, then push. These are the existing steps moved, with the same logic.
  - Every `uses:` pinned to a full commit SHA, with the tag in a comment. Resolve the SHAs with `gh api` at implementation time; do not take them from memory.
- `.github/workflows/ci.yml`: runs on `push` and `pull_request` with `contents: read` and pinned actions. Steps: build (warnings are errors), test, `check-manifest.sh`, `node --check Jellyfin.Plugin.Jellywave/Web/ui.js`, `bash -n scripts/*.sh`, `dotnet format --verify-no-changes`.
- `.github/dependabot.yml`: `github-actions` ecosystem, weekly.
- Exit: build and tests are clean locally, and check-manifest prints only ok lines. Make check-manifest fail once on purpose with a csproj/targetAbi mismatch. HI pushes the branch and CI goes green. The release path is proven at the v0.4.0.0 tag.

#### 13. Station route authorization (S1, S2, API keys, request accounting)

**Accepted 2026-10-27** (5614428, cfb334f; review fixes ac3ca88, 6de1e04, 3a8c8f8, 52866ce). RA first pass: CHANGES REQUIRED, one Required item (receipt wiring had no route-level test); Suggested: content-hash asset version, channel reused by Feed, 404 while the channel is missing, receipt-cap note; all fixed and re-reviewed: ACCEPT, 140/140 tests. Moved to card 18: clear an already-rendered header when the feed starts answering 404. Host checks pending (with cards 14–15).

- New `StationAccess` service plus an action filter attribute. They run on Feed, Avatar, Like GET/POST, Schedule, Lyrics, Request POST and Request/{id}. The `ui.js` and `ui.css` assets stay anonymous; WebUi stays RequiresElevation. The rules:
  1. The API-key claim is set → 403.
  2. No user id claim, or the user is not found (reuse `IUserStore`) → 403.
  3. The channel entity is not found → 403 (fail closed).
  4. Jellyfin's own `channel.IsVisible(user)` is false → 403. This is the authoritative rule: blocked list, enabled list or EnableAllChannels, and the parental check. Admins get no exemption, same as Jellyfin.
  5. Otherwise, the user id goes into `HttpContext.Items` for the action.
- Verify the claim names (`Jellyfin-UserId`, `Jellyfin-IsApiKey`) in the 12.3 source `Jellyfin.Api/Constants/InternalClaimTypes.cs`.
- New `RequestReceipts` singleton: request id → (user id, expiry). Lifetime 30 min and at most 1000 entries, both as named constants; entries are pruned on insert.
  - `SubmitRequest` records the receipt.
  - `RequestStatus` returns 403 for another user's id. An unknown id gets "unknown" without calling the station.
  - After a restart the receipts are gone, so the page shows "lost track". Documented.
- The free-text name still goes to the station unchanged. Accounting: the receipt holds the user id, and the existing Information line already logs the request id with the user id (never the name).
- `ui.js`: a 403 from the feed stops polling quietly (longer backoff). No feed means no header and no drawer link, so blocked users stop seeing the page.
- A reflection test: every action that is neither AllowAnonymous nor RequiresElevation carries the filter.
- Tests:
  - API key → 403; no user → 403; user not found → 403; no channel → 403; not visible → 403; visible → passes.
  - User A submits, user B polls → 403; the owner polls → passes; an expired receipt → "unknown".
  - If `Channel.IsVisible` cannot run in a unit test, because of the parental check's static dependencies, put the visibility call behind a seam. The rest of the decision is still tested.
- Docs: README "Who can use the station page"; DESIGN gets an authorization section.

#### 14. Outbound hardening (S3, S4, S6)

**Accepted 2026-10-27** (91ff06f, 8d9e17c, f5e03e6; review follow-ups 6c69545, bd5c0c2). RA: ACCEPT, no Blocker or Required; folded in: a refused saved station URL is reported as "set but not allowed: <reason>", redirect suggestions only for acceptable addresses, the authority check documented as load-bearing, animation risk widened to APNG and WebP, a test race. 245/245 tests. Host check pending: an admin probe of a redirecting URL.

- `PluginServiceRegistrator`: register the named client `Jellywave` with `SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = All }` and a User-Agent header. If `AddHttpClient` does not compile with the current references, add `Microsoft.Extensions.Http` with `ExcludeAssets runtime`.
- `StationClient`: every call goes through one private `CreateClient()` helper. That also removes the six copies of the timeout and buffer setup.
- A 3xx is never followed:
  - `GetJsonAsync` → `EnsureSuccessStatusCode` throws.
  - Images → null.
  - Like, request and lyrics replies map to unavailable or refused.
  - The admin probe (`JellywaveController`) says "the station redirected to <Location>; use that address".
- `StationUrls`:
  - The base URL rejects userinfo, query and fragment. Check the raw parse before the slash is appended.
  - `StreamPath` is rejected when it parses as an absolute URI.
  - The built stream URL must equal the base on `SchemeAndServer | UserInfo`, which covers scheme, host, port and userinfo (`Uri.Compare`).
  - `ClientStreamUrl` rejects userinfo. Any host stays allowed: Direct mode discloses it to clients on purpose (documented).
  - HTTP stays allowed; the station runs on plain HTTP on the LAN. Documented.
- New `ImageSniffer`:
  - Detects JPEG, PNG, GIF and WebP from magic bytes and uses the detected MIME. The declared MIME only has to be image/* or missing.
  - Reads width and height from the headers: PNG IHDR, the GIF screen descriptor, WebP VP8/VP8L/VP8X, JPEG SOFn.
  - Rejects unknown types, a side over `MaxImageDimension` (4096) or an area over `MaxImagePixels` (16 777 216).
  - Applies to covers and avatars. GIF frame count is not checked; that is an accepted risk.
- Tests:
  - URLs: same host on another port, another scheme, userinfo, query, fragment, an absolute StreamPath, userinfo in the client URL.
  - Redirects: 302 to another host for JSON, image and POST, using a fake handler as in `StationClientCoverTests`.
  - Images: a sniffer fixture per format, oversize dimensions, a mismatched MIME, a missing MIME with PNG bytes.

#### 15. Channel identity, grant serialization, verify script (D1, D3, O10)

**Accepted 2026-10-27** (623bf69, f109957, 6b900f5, 6b82c18, 9be708f; follow-up 354536c). RA: ACCEPT, no Blocker or Required; deadlock ruled out against the 12.3 source (UpdateUserAsync publishes no event; policy/create events fire outside any lock). Folded in: the no-publish rule on IUserStore.SaveAsync, the collision status read live, DESIGN wording. Accepted: the cover is re-downloaded each poll during a collision. 270/270 tests. Host check pending: verify-jellyfin.sh.

- `LiveItemFinder.FindChannel`:
  - Computes the id the way Jellyfin does, `_libraryManager.GetNewItemId("Channel " + name, typeof(Channel))`, and loads it with `GetItemById`. The name scan goes.
  - `LibraryChannelLocator` reuses it instead of its own copy.
- Collision check:
  - Compare our name with every other registered `IChannel` from `IEnumerable<IChannel>`. Resolve it lazily to avoid a DI cycle through `JellywaveChannel`. Check whether `GetNewItemId` is case-sensitive and compare to match.
  - On a collision: the poller writes nothing, the granter grants nothing, and `StationAccess` denies.
  - Log one Warning per streak, and show "channel name collides with another plugin's channel" on the config page's status line through `PollerHealth`.
- The default name stays "SUB/WAVE". Renaming changes the id and drops existing grants. README: pick a unique station name, because Jellyfin's API cannot make the id collision-proof.
- `AccessGranter`: one `SemaphoreSlim` serializes `GrantAllAsync` and `GrantAsync`, and each user is re-read under it. The remaining window against other writers (dashboard saves) is accepted and documented.
- `scripts/verify-jellyfin.sh`:
  - Takes `JELLYWAVE_CHANNEL` (default `SUB/WAVE`) and selects that channel by exact name. It FAILs when the channel is missing or there is more than one.
  - Item and PlaybackInfo checks run against that channel only.
- Tests: collision → fail closed; derived-id lookup; two concurrent grant calls do not overlap (the fake store records overlap).

#### 16. Docs and repo hygiene (O14, T1, T4 docs, HI decision on the packet)

**Accepted 2026-10-27** (283f1d5, 61410bd, c1b4ad3, bf87822, 83d94d9, 48a1272; review fixes 1b0406a). RA: first pass CHANGES REQUIRED (PlaybackInfo wording, redirect control scoped to the plugin's own requests with the stream fetch as an accepted risk, one grep hit); fixes re-checked: ACCEPT. Condition before merge: HI enables private vulnerability reporting (SECURITY.md relies on it).

- Move `docs/PLAN.md` and `docs/LESSONS_LEARNED.md` to `docs/dev/` with `git mv`. Grep every reference and update it: CLAUDE.md, README, DESIGN and the scripts.
- Remove the host-access notes from PLAN.md (the admin-rights note, remote-login user and key names). They stay in `local/` notes and memory. Git history keeps them; they hold no secret values and the history is not rewritten.
- README:
  - A support matrix: Jellyfin 12.3.x; File Transformation 3.0.x works when installed, but its catalog does not offer it to 12.3 servers as of 2026-10-27 (upstream issue #87). No 12.0–12.2.
  - The playing bar line: standard clients do not update mid-track; the station page does.
  - Who can use the page.
  - Requests: one shared station identity, a free-text name, and the user id kept for accounting.
- `configPage.html:77`: remove "and the track as its album line".
- `SECURITY.md`: report through GitHub private vulnerability reporting. HI enables it under repo Settings → Security. Supported: the latest release.
- `docs/THREAT-MODEL.md`:
  - Trust boundaries: admin config is trusted; station data is untrusted and is never allowed to redirect; users must be signed in and see the channel; API keys are denied; the browser.
  - Accepted risks: the shared station budget; the free-text DJ name; the HTTP station URL; the station URL disclosed to clients in Direct mode; the name-derived channel id; the user-save window; the live-item row race; GIF frames; jellyfin-web selectors that can move between releases.
- DESIGN.md: a consistency pass over cards 12–15. Each card has already closed its own section.
- Exit: the docs match the code, a grep for host-access strings comes back empty, and the links resolve.
- Changelog must mention: station URLs with credentials, a query or a fragment are refused (card 14).

#### 15b. Access grant as a one-shot admin action (found in the 0.4.0.0 host test)

**Accepted 2026-10-27** (4ec324b, 4828310, f77fa8a, 6efa974; review fixes 4589d57, 7f742e1). RA first pass: CHANGES REQUIRED (the dashboard's Add User form unticks all channels; a press re-adds users the admin unticked); fixed and re-checked: ACCEPT, 279/279 tests. Host check pending.

Host test 2026-10-27: with `GrantAccessToAllUsers` on, an admin who unticked the station on a user's Jellyfin Access page saw it re-added at once (`UserUpdatedConsumer` → `GrantAsync`, log 18:50:04 and 18:52:09); the startup pass would also re-add it after each restart. Blocker for v0.4.0.0. HI decision 2026-10-27: trade the always-on shortcut for stability ("I'm willing to trade that shortcut... if it makes the end product more stable"); MI recommendation adopted: one-shot admin action.

- Remove the background writers: `AccessGrantService` (startup pass), `UserCreatedConsumer`, `UserUpdatedConsumer` and their registrations; remove the `GrantAccessToAllUsers` setting (an old saved value must load without error).
- Keep `AccessGranter.GrantAllAsync` (with card 15's gate, collision check and block-list rule) behind the existing admin-only `POST /Jellywave/GrantAccess`, now triggered only by a button on the plugin page: "Add the station to every user now", showing granted / already visible / blocked counts. It never removes access and never sets EnableAllChannels.
- Plugin page text: Jellyfin's own Channel Access setting on each user decides from then on; new users see the station when their policy allows all channels (Jellyfin's default), when a provisioning tool's template includes it, or after another click.
- Docs: README, DESIGN (card 6 section rewritten as history plus current behaviour), THREAT-MODEL (no background policy writes; the user-save window risk goes away or narrows to the button), build.yaml changelog line.
- Tests: no consumer or hosted grant service is registered (registrator test); the button route grants and reports counts; an existing config file containing `GrantAccessToAllUsers` still deserializes.
- Host check: untick a test user → stays hidden after the save and after a restart; the button reports counts.

**Then: merge to main, deploy to the host, and tag v0.4.0.0.**

**Host evidence for v0.4.0.0 (2026-10-27, build from 858bc05, installed dll md5 2ad07da7 equals the local build; plugin source unchanged up to the tag).** HI on the test host: the station page works for a non-admin user (play, like, a request answered by the DJ); a user unticked on the Access page stays without the tile and page after the save and after a restart; the "Add the station to every user now" button reports its counts and re-adds that user, as the page warns; an API key gets 403 from `/Jellywave/Feed`; "Fetch from station" works with the saved URL and reports the redirect for `http://github.com`; `scripts/verify-jellyfin.sh` (after ebfda78: tile name from the plugin configuration, PlaybackInfo with an mp3 profile) ends with ALL CHECKS PASSED. Server log after the 0.4.0.0 load: poller started, cover updated, 0 ERR/WRN from Jellywave. Private vulnerability reporting enabled (API: `enabled: true`). CI green on the branch.

#### 17. Station data consistency (D2 server, D4, D5, D6) — v0.4.1.0

**Accepted 2026-10-27** (2b29bda, a47f002, e47aa77, bf06fa4, d445801, c4b7c12; review fixes d70cc97, 7719913, a34b6ee, 78f85ca, a55c4e1; nits in the acceptance pass). RA first pass: CHANGES REQUIRED (Feed served the old station after a URL change until the next poll); fixed and re-checked: ACCEPT, 320/320 tests. Host check pending.

- From the card 15b review: README/DESIGN note that saving a user's Access page clears any BlockedChannels entry (jellyfin-web `Access.tsx` posts `BlockedChannels = null`); `PolicyWriterDependencyTests` lists `IServiceProvider` as allowed only in `LiveItemFinder` (and `StationAccessAttribute` if it resolves services that way), so a new service locator is noticed.

- Cleanup from card 15: `PollerHealth.SetNameCollision` and `PollerHealthSnapshot.NameCollision` are written by the poller but no longer read (Health resolves the collision live); remove them and their test.

- The station origin is `baseUrl.AbsoluteUri`.
  - `NowPlayingState` stores the origin with each update and clears `StationState` when the origin changes.
  - `ScheduleCache` stores the origin with the schedule. A change of origin drops the cache, and a failure for a new origin returns null, never the old station's copy.
- `LiveItemArtwork`:
  - The applied key is origin + cover id.
  - A negative cache holds the missing cover key. While it holds, the logo is used without a fetch, until the cover id changes or `MissingCoverRetry` (30 min, a named constant) passes.
- A null feed → the failure path: health records the failure and the state is cleared.
- `BuildToday`: `TryAdd`, so the first id wins.
- Tests:
  - ScheduleCache: fresh, stale after a failure, origin switch, retry delay. No tests exist today.
  - NowPlayingState origin switch; the artwork negative cache and its TTL; a duplicate schedule id; a null feed.

#### 18. Web client (T2, T3, D2 browser) — v0.4.2.0

**Accepted 2026-10-27** (89ef5a0, cd901b2, 0327e66; review follow-ups b6fced9, 9df922c). RA: ACCEPT with four Suggested (header after an in-flight fetch, request poll across a session change, GET like reply for another song, cached header re-point); all fixed and re-checked against Node timing simulations: ACCEPT, 320/320 C# tests. Host check pending (log out and in as another user, a blocked user, playback in another library).

**Host evidence (2026-10-27).** v0.4.1.0 (build from f78f61c, dll md5 535d35d2 equals the local build): HI saw the station page as before; with the station URL saved as `http://127.0.0.1:9` the page showed nothing from the old station and an empty schedule, and filled again after the real URL was saved; log: "unreachable … Connection refused", cover "logo", then "answering again" and the cover back. Released: zip md5 9B145687… equals the manifest entry. v0.4.2.0 (build from 53579a5, dll md5 d82b5c5e equals the local build): HI: play and like work (heart hides briefly on a track change), signing out and in as another user shows fresh state, a blocked user sees no header and no drawer link, playback in another library shows no slowdown; 0 ERR/WRN from the plugin after the load.

- From the card 13 review: when the feed starts answering 404 or 403 while a header is rendered (station renamed, access revoked), remove the header and the drawer link instead of leaving them stale.

- `ui.js`:
  - A boot guard: one observer and one set of listeners.
  - The observer is debounced through one pending timer (`DOM_DEBOUNCE_MS` 250) and runs only cheap selector checks.
  - Check whether jellyfin-web 12.3 dispatches `viewshow`. If it does, use it and keep the observer only for the drawer.
- `fetchLike`: captures the song id and discards the reply when the current feed's `SongId` has changed.
- State is keyed by `serverId + userId`. When either changes, the feed, like, lyrics and schedule state and the header references reset.
- Exit, checked by HI on the host:
  - The page works, likes work, and a log-out with log-in as another user shows fresh state.
  - A blocked user sees no header and no drawer link.
  - Playback in another library shows no slowdown.

### Validation (every card)

```bash
set -o pipefail
dotnet build Jellyfin.Plugin.Jellywave.slnx -c Release
dotnet test Jellyfin.Plugin.Jellywave.Tests -c Release
dotnet format Jellyfin.Plugin.Jellywave.slnx --verify-no-changes
node --check Jellyfin.Plugin.Jellywave/Web/ui.js
bash -n scripts/*.sh
scripts/check-manifest.sh            # only "ok" lines
git grep -nE '<host patterns from local/>' -- . ':!local'   # empty
```

Host checks (HI runs `bash ~/jellywave-dist/deploy-jellyfin.sh`):

- Card 13: a non-admin user with access gets 200 from `/Jellywave/Feed`. A user with the channel in BlockedChannels gets 403. An API key gets 403. User B polling user A's request id gets 403.
- Card 14: an admin probe of a URL that redirects gets the redirect message.
- Card 15: `scripts/verify-jellyfin.sh` passes (set `JELLYWAVE_CHANNEL` to the tile name when it is not `SUB/WAVE`).
- Every card: 0 ERR/WRN lines mentioning Jellywave after the restart.

### Risks

| Risk | Handling |
|---|---|
| `Channel.IsVisible` parental check denies a user who can see the tile | Same rule Jellyfin uses for the tile; the host test covers a normal user |
| Station page breaks for API-key scripts | None known; verify-jellyfin.sh does not call /Jellywave routes |
| Redirect refusal breaks an install whose station URL redirects (http→https) | Probe message tells the admin the right URL; README notes it |
| targetAbi 12.3 hides new versions from 12.0–12.2 servers | Intended; README support matrix |
| Workflow split breaks the release | Same steps, moved; exercised at v0.4.0.0; rollback = revert the workflow commit and re-run `gh workflow run Release -f tag=…` |
| Receipts lost on restart | Page says "lost track"; documented |

### Hard Stops in this wave

- Authorization change (cards 13, 15): HI approved the direction through the decisions above. HI reviews before merge (Elevated Risk).
- Release workflow change (card 12): HI reviews before merge.
- `git push`, the merge to main and enabling private vulnerability reporting are HI's.
- No history rewrite. The host-access notes stay in old commits.

### Rollback

Each card is its own commits on `wave3-hardening`. Revert a card's commits, or do not merge the branch. Published releases and manifest entries are untouched until v0.4.0.0.

### First steps after approval (MI)

1. `git switch -c wave3-hardening`.
2. Add this plan as the "Wave 3" section of `docs/PLAN.md` with a `wip:` commit (the move to docs/dev/ happens in card 16).
3. Append the reviewer's report summary and the HI decisions to LESSONS_LEARNED when card 12 closes.
4. Start card 12 as DA. RA for each card is a different session or vendor.

## Phase 4: Live Radio (plan approved 2026-10-27)

**Phase 4 status (2026-10-27; L5b added 2026-10-28):** L0–L5, L5b and L7 accepted; L5b (header follows playback, HI's
change from the host test) accepted 2026-10-28 after one RA fix round. Host test in progress: web station page
passed 2026-10-28. HI added L5c (plugin page wording and colours), L8 (header for plain streams) and L6 (ICY titles),
in that order, before the merge. Nothing is pushed or tagged.

Role at writing: PA. Track: full process (config migration, channel identity, authorization scope,
URL inputs to ffmpeg). Branch `live-radio`, cut from main at 81ecc3c (v0.4.2.0 manifest). Planned in
an advisory session against v0.4.0.0 and re-checked against v0.4.2.0.

### Branch, release, process

- One card at a time on `live-radio`, `wip:` commits staged by name. Each card: a DA subagent
  implements, a separate RA subagent reviews (its own build, tests and mutations), MI writes the
  `accept:` commit with the evidence here and a LESSONS block.
- The cards are not releasable one by one (L1 alone renames the tile). `Directory.Build.props` goes to
  0.5.0.0 in L1; one release, v0.5.0.0, after the host test of the finished branch.
- Hard Stops: L1 (rename bridge, policy writes) and L5 (station routes) are authorization changes;
  HI reviews before merge. `git push`, the merge and host deploys are HI's.

### Context

Jellywave started as "play one SUB/WAVE stream in Jellyfin" and grew SUB/WAVE-specific features:
now playing, per-track cover, station page with DJ portrait, likes, lyrics, schedule, requests.
The playback core (a channel item with a static remote HTTP media source relayed through ffmpeg)
is not SUB/WAVE-specific. Kevin wants the existing My Media tile to become a **Live Radio** section
holding SUB/WAVE stations (with all their features) alongside other MP3 streams.

#### HI decisions so far (2026-10-27)

| Decision | Choice |
|---|---|
| Direction | Build it in Jellywave, one plugin. Prior-art plugins are references only. |
| Station source | Admin pastes stream URLs on the config page. No directory search. |
| Tiles | One tile; SUB/WAVE stations are items inside it, next to plain streams. |
| Timing | After the current work closes. **Updated:** that now means after card 18 (v0.4.2.0). |
| Tile name on upgrade | **Rename to "Live Radio"** (HI, knowing it changes the channel id). The plan adds the bridge below so it does not lift blocks. |
| Query strings in plain-stream URLs | **Allowed and documented.** No userinfo, no fragment. |
| Visibility | **One gate for the whole tile.** Every user who sees it sees every station. |
| SUB/WAVE header | **Above the grid, with a picker** (mockup 2). *Superseded: changed by HI on 2026-10-28, see the next row.* |
| SUB/WAVE header (changed by HI 2026-10-28, host test) | **Header only while a SUB/WAVE station plays, showing that station; no picker.** No header when nothing plays or a plain stream plays; it switches with playback; paused counts as playing; features the station turns off stay hidden. Card L5b. |

### What changed since the first draft (Wave 3, v0.4.0.0)

| Wave 3 change (fact, from repo) | Effect on Live Radio |
|---|---|
| Card 15: channel found by the id Jellyfin derives from the name (`LiveItemFinder.Resolve`); collision with another plugin's channel fails closed | **Renaming the tile to "Live Radio" changes the channel id.** See "The rename problem" below. A generic name like "Live Radio" is also more likely to collide with another radio plugin; that fails closed and shows on the config page. |
| Card 15b: no background grant; "Add the station to every user now" is a one-shot button | After an id change nothing re-grants. Users who had the old id in `EnabledChannels` lose the tile until the admin presses the button. |
| Card 13: `[StationAccess]` on 8 station routes, gate = `Channel.IsVisible(user)` | One gate for the whole tile: Jellyfin cannot hide one station from one user. Every station route also needs a station id, checked against the config (unknown, disabled or plain-stream id → 404). |
| Card 13: `RequestReceipts` maps request id → user id | Must also hold the station id, so a status poll goes to the right station. |
| Card 14: own HTTP client, no redirects; base URL refuses userinfo, query, fragment; stream URL must match the base's scheme, host, port | SUB/WAVE stations keep these rules unchanged. Plain streams need a whole-URL rule (HI: query allowed). The add-station probe uses the no-redirect client, so it reports redirects instead of following them; the admin pastes the final URL (card 14 already suggests it). |
| Card 14: `ImageSniffer` (magic bytes, 4096 px, 4 MB) | Plain-stream logos should go through it (download in the plugin) rather than through Jellyfin's `ImageUrl`, which fetches with Jellyfin's own client and skips these controls. |
| Card 8d: Avatar, Like, Schedule, Lyrics routes; `ScheduleCache`; `PollerHealth`; `GET /Jellywave/Health` | More single-station state to key by station: schedule cache, poller health, like state, lyrics. |
| Card 17 (v0.4.1.0): caches keyed by station origin, negative cover cache, null feed = failure | Good seam: Live Radio replaces "keyed by origin" with "keyed by station id". No change to card 17's scope is needed. |
| Card 18 (v0.4.2.0): browser state keyed by server + user; header removed on 403/404 | Live Radio adds the station id to that key. Build Live Radio on top of card 18, not alongside it. |
| `verify-jellyfin.sh` checks one channel, one audio item | Needs a check per enabled station. |
| `docs/THREAT-MODEL.md`, `docs/dev/PLAN.md`, CI, RA in a separate session | Live Radio updates the threat model (new asset: plain-stream URLs; one gate for many stations) and follows the same card and review process. |

#### The rename problem (fact, from card 15 and Jellyfin's `Channel.IsVisible`)

The channel id is `GetNewItemId("Channel " + name)`. If an upgrade renames the tile from
"SUB/WAVE" (or "IHateRadio") to "Live Radio":

- Users with the old id in `EnabledChannels` stop seeing the tile (39 of 41 on the test host got it
  that way). Nothing re-grants in the background since card 15b.
- **Users blocked through `BlockedChannels` start seeing it.** When a block list exists,
  `IsVisible` only checks the list. It names the old id, so the new tile passes. That is an
  authorization regression.
- The old channel entity lingers until "Refresh Internet Channels" (the task is named 'Refresh Channels') removes it.

**HI chose the rename.** The plan keeps it and adds a bridge, so the new tile fails closed for
blocked users and the admin can carry grants over in one press. The bridge also fixes the existing
accepted risk "renaming changes the id, so grants and blocks naming the old id stop applying",
which covers every later rename too.

**The rename bridge** (design; one fact still needs checking, see L1):

1. The config records `PreviousChannelNames`, the earlier tile names (L1: names, not ids; the ids are
   derived when used, so they follow `EnableCaseSensitiveItemIds`). The migration adds today's
   `StationName`; any later rename on the config page adds the outgoing name (server side).
2. **Old blocks are honoured, read-only.** A user whose `BlockedChannels` names any previous id is
   refused by `StationAccess` (403), and is hidden from the tile through `IChannel.IsEnabledFor(userId)`,
   which today always returns true. This writes nothing, so it does not undo card 15b.
   - **Fact (v12.3 source, checked 2026-10-27):** `ChannelManager.GetChannelsInternalAsync` drops a
     channel for a user when `IsVisible(user)` is false or `IsEnabledFor(userId "N")` returns false or
     throws (`ChannelManager.cs:221-237`); `UserViewManager.GetUserViews` builds My Media from that list
     (`UserViewManager.cs:120`). So the tile is hidden.
   - **Fact:** item lists come from the library database after the provider is asked
     (`GetChannelItemsInternal`), so `IsEnabledFor` does not gate an item id a user already knows, and
     returning no items would not hide anything. Raw API access to the new item ids stays possible for
     an old-blocked user until the carry-over (point 3) copies the block.
3. **Carry access over, one-shot, admin only.** The config page shows "Tile renamed from
   'IHateRadio'. 39 users had it by name. [ Carry access over ]". The button adds the new id to
   `EnabledChannels` wherever a previous id is there, and **adds the new id to `BlockedChannels`
   wherever a previous id is there** (MI refinement after the source check above; adding a block only
   narrows access, and afterwards Jellyfin itself enforces the block on every route). Add-only, under
   the existing `AccessGranter` lock, each user re-read before the save. Until the admin presses it,
   users granted by name simply do not see the tile, which is the safe direction.
4. Once the admin has pressed it and nobody has a previous id any more, the page stops showing the
   notice. `PreviousChannelNames` stays, because old blocks keep being honoured until the admin saves
   that user's Access page (jellyfin-web then clears `BlockedChannels`, a card 17 note).

This is an authorization change, so it gets the Hard Stop review: elevated risk, a separate RA
session, and HI review before merge.

### Research facts (unchanged from the first draft)

- **Tile count is fixed at startup** (fact): `IChannel` comes from DI in `PluginServiceRegistrator`,
  so stations must be items inside one channel.
- **Jellyfin Live TV is not an alternative** (research agent read jellyfin master): the M3U parser
  never sets `ChannelType.Radio` (`M3uParser.cs:116-167`). Radio plays in the video player with a
  black screen, there is no Radio tab, and issue #17124 is open.
- **Prior art** (GPL-3.0, read for ideas only, no copying into MIT Jellywave):
  `digibaro/jellyfin-plugin-radiobrowser`, and `SurfHost/jellyfin-plugin-radio`, which is MP3 only
  because the Cast receiver has no AAC profile.
- **Codec mix** (radio-browser, 63k stations): MP3 68%, AAC 28%, HLS 7%, Ogg 1%.
- **ICY titles**: connect with `Icy-MetaData: 1`, skip `icy-metaint` bytes, read one block, close.

### Target shape

- **Channel**: `JellywaveChannel` stays the one `IChannel`. `Name` comes from `LibraryName`, which
  defaults to "Live Radio" on upgrades and fresh installs alike. Tile image: the embedded logo for
  now (a generic radio mark is an art task for later).
  `IsEnabledFor` honours blocks that name a previous id (rename bridge).
- **Stations**: config list `Stations` of `StationDefinition` (one type per file):
  - Common fields: `Id` (GUID made on add; the item's external id), `Name`, `Kind` (`SubWave` |
    `Stream`), `Delivery`, `ClientStreamUrl`, format (`Codec`, `Container`, `BitrateKbps`,
    `SampleRate`, `Channels`), `Enabled`, `SortOrder`.
  - `SubWave`: `BaseUrl` + `StreamPath`, under the card 14 rules unchanged.
  - `Stream`: `StreamUrl` + optional `LogoUrl`, under new whole-URL rules.
  - Global fields: poll interval, `NowPlayingEnabled`, `CoverArtEnabled`, `LastChangedUtc`.
- **Items**: one Audio item per enabled station, built by today's `BuildLiveItem`, with the format
  taken from the station. The rename creates a new channel, so every item is new, including the
  migrated SUB/WAVE one. Its old item goes with the old channel through "Refresh Internet Channels" (the task is named 'Refresh Channels').
  Losing play state on a live item costs nothing.
- **Routes**: every station route takes `stationId`. `[StationAccess]` stays the channel gate. The
  action then resolves the id to an enabled `SubWave` station from the config, else 404.
- **Features by kind**:

  | Kind | Gets |
  |---|---|
  | `Stream` | Playable item, name, logo, Relay/Direct. Later (L6): ICY "now playing" while someone listens |
  | `SubWave` | Everything Jellywave has today: overview, cover, DJ, likes, lyrics, schedule, requests |

### Mockups and diagrams (how I understood the request)

These are sketches, not pixel designs. Anything marked "ui.js" is drawn by our injected script, so
it shows only in the web client and in the Android app (which wraps the web client). Roku and
Android TV show the plain Jellyfin grid.

#### 1. Home screen: My Media row

```
 My Media
 ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐
 │  [img]   │ │  [img]   │ │  [img]   │ │   ((•))  │
 │          │ │          │ │          │ │   radio  │
 └──────────┘ └──────────┘ └──────────┘ └──────────┘
    Movies       Shows        Music      Live Radio    ← one tile for all stations, replaces "IHateRadio"
                                                         (new channel id; see the rename bridge)
```

#### 2. Inside the tile: the Live Radio page (option A, recommended for L5)

```
┌────────────────────────────────────────────────────────────────────────────┐
│ ←  Live Radio                                                   🔍   👤     │
├────────────────────────────────────────────────────────────────────────────┤
│  ui.js header (SUB/WAVE stations only)              [ IHateRadio      ▾ ]  │ ← picker only with 2+
│ ┌────────┐  IHateRadio                                    [ ♥ 12 ]        │   SUB/WAVE stations;
│ │ cover  │  Artist – Title (Album)                                         │   defaults to the one
│ │  art   │  (◉) DJ Nova · "late-night static" · Show: Night Shift          │   playing
│ └────────┘  Up next: Morning Mix with Ana at 06:00 · 12 listening          │
│             [ ▶ Play ]  [ ■ Stop ]                                         │
│  ▸ Recent tracks   ▸ Lyrics   ▸ Today's schedule        (today's panels)   │
│  Request a song [ artist / title ...................... ]                  │
│                 [ name: kevin ]   [ Send ]                                 │
│  Requests go through this server; everyone here shares the station's limit.│
├────────────────────────────────────────────────────────────────────────────┤
│  Jellyfin's own grid (every client)                          Sort: Name ▾  │
│ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐           │
│ │  track   │ │  track   │ │ station  │ │ station  │ │  ((•))   │           │
│ │  cover   │ │  cover   │ │  logo    │ │  logo    │ │ default  │           │
│ │ ✦SUBWAVE │ │ ✦SUBWAVE │ │          │ │          │ │          │           │
│ └──────────┘ └──────────┘ └──────────┘ └──────────┘ └──────────┘           │
│  IHateRadio   Station Two  Radio Parad.  SomaFM Gro.  KEXP                  │
│                                                                            │
│  cover = changes per track (SUB/WAVE)      logo = from the station's URL   │
│  ✦ badge = ui.js only, optional            ((•)) = no logo given           │
└────────────────────────────────────────────────────────────────────────────┘
 With only plain streams configured, there is no header, just the grid.
```

#### 3. Option B for L5: header on the station's own page instead

```
 Live Radio grid (no header)  ──click IHateRadio──►  station detail page
                                                     ┌───────────────────────────┐
                                                     │ [cover]  IHateRadio   ♥   │
                                                     │          Artist – Title   │
                                                     │          DJ · Show · Next │
                                                     │ [ ▶ Play ]                │
                                                     │ ▸ Recent ▸ Lyrics ▸ Sched │
                                                     │ Request a song [.....]    │
                                                     └───────────────────────────┘
 + no picker needed, one station per page
 − one extra click; Jellyfin's detail page is busier and its markup moves more between releases
```

#### 4. Playing bar (bottom of the screen)

```
 SUB/WAVE station:  ▕[cover] Artist – Title        ⏹   🔊 ──●──  ▏  ← ui.js repaints every 5 s
                    ▕        IHateRadio · DJ Nova                 ▏
 Plain stream:      ▕[logo]  Radio Paradise — Live  ⏹   🔊 ──●──  ▏  ← fixed text (L6 could add the
                    ▕        Live Radio                           ▏    track title from the stream)
```

#### 5. Config page (Dashboard → Plugins → Jellywave)

```
 Jellywave
 ── Library ──────────────────────────────────────────────────────────────
 Tile name   [ Live Radio                  ]   renaming makes a new tile; old blocks
                                               keep applying, grants need one press below
 Channel status: ok        (or: "name collides with <other plugin>'s channel")
 ┌ Tile renamed from "IHateRadio" ──────────────────────────────────────┐
 │ 39 users had the old tile by name and cannot see "Live Radio" yet.    │
 │ Users blocked from the old tile stay blocked.                         │
 │                                         [ Carry access over ]         │
 │ Done: carried over 39, blocked 1, already visible 2.                  │
 └───────────────────────────────────────────────────────────────────────┘
 ── Stations ─────────────────────────────────────────────────────────────
 ┌───┬────────────────┬──────────┬──────────┬──────────┬─────────┬──────────────┬────────┐
 │   │ Name           │ Kind     │ Format   │ Delivery │ Enabled │ Status       │        │
 ├───┼────────────────┼──────────┼──────────┼──────────┼─────────┼──────────────┼────────┤
 │ ≡ │ IHateRadio     │ SUB/WAVE │ MP3 192  │ Relay    │   [x]   │ ok 12 s ago  │ Edit ✕ │
 │ ≡ │ Station Two    │ SUB/WAVE │ MP3 128  │ Relay    │   [x]   │ ⚠ unreachable│ Edit ✕ │
 │ ≡ │ Radio Paradise │ Stream   │ MP3 320  │ Relay    │   [x]   │ —            │ Edit ✕ │
 │ ≡ │ KEXP           │ Stream   │ AAC 128 ⚠│ Relay    │   [ ]   │ —            │ Edit ✕ │
 └───┴────────────────┴──────────┴──────────┴──────────┴─────────┴──────────────┴────────┘
   ≡ drag to reorder   ⚠ AAC may not play on Chromecast   Status = poller health (SUB/WAVE)
 [ + Add station ]                                            [ Refresh status ]
 ── Now playing (SUB/WAVE stations) ──────────────────────────────────────
 [x] Show what's playing    every [ 20 ] seconds      [x] Track cover art
 ── Who can see it ───────────────────────────────────────────────────────
 One switch for the whole tile: Jellyfin cannot hide a single station from a user.
 [ Add the tile to every user now ]
   Done: granted 0, already visible 41, blocked by a block list 0.
 ── Station page (optional) ──────────────────────────────────────────────
 File Transformation plugin: installed ✓
                                                               [ Save ]
```

#### 6. "Add station" dialog

```
 ┌ Add station ───────────────────────────────────────────────────────┐
 │ Stream URL [ https://radio.example.org/listen.pls             ]    │
 │                                                     [ Check ]      │
 │                                                                    │
 │ ✓ Playlist read → https://radio.example.org:7700/stream.mp3        │
 │ ✓ Audio: MP3 · 192 kbps · 44.1 kHz · stereo                        │
 │ ✓ SUB/WAVE detected: now playing, covers, likes, requests on       │
 │                                                                    │
 │ Name       [ IHateRadio               ]  (filled from the station) │
 │ Logo URL   [                          ]  optional (plain streams)  │
 │ Delivery   (•) Relay through Jellyfin   ( ) Direct to the client   │
 │                                                                    │
 │                                          [ Cancel ]   [ Add ]      │
 └────────────────────────────────────────────────────────────────────┘
 Other outcomes:
   ↪ "The station redirected to https://…; use that address"  [ Use it ]   (card 14 rule)
   ⚠ AAC stream (may not play on Chromecast)   ✕ not audio   ✕ not reachable   ✕ URL not allowed
```

#### 7. What Jellyfin stores (entity tree)

```
Jellyfin library
├── Channel "IHateRadio"   old id → removed by "Refresh Channels"; name kept in PreviousChannelNames
└── Channel "Live Radio"   new id
    ├── Audio "IHateRadio"          external id = live (migrated)  SubWave  cover per track
    ├── Audio "Station Two"         external id = 9b11…            SubWave  cover per track
    ├── Audio "Radio Paradise"      external id = c07e…            Stream   logo (sniffed)
    ├── Audio "SomaFM Groove Salad" external id = 77aa…            Stream   logo (sniffed)
    └── Audio "KEXP"                (disabled → not listed)
```

#### 8. Plugin configuration (data tree)

```
PluginConfiguration
├── LibraryName            "Live Radio"
├── PreviousChannelNames[] earlier tile names; ids derived when used (rename bridge)
├── NowPlayingEnabled / NowPlayingPollSeconds / CoverArtEnabled / LastChangedUtc   (global)
└── Stations[]
    ├── StationDefinition  (Kind = SubWave)
    │   ├── Id, Name, Enabled, SortOrder
    │   ├── BaseUrl, StreamPath, ClientStreamUrl, Delivery      ← card 14 rules, unchanged
    │   └── Codec, Container, BitrateKbps, SampleRate, Channels, Tagline
    └── StationDefinition  (Kind = Stream)
        ├── Id, Name, Enabled, SortOrder, LogoUrl
        ├── StreamUrl, ClientStreamUrl, Delivery                ← new whole-URL rules
        └── Codec, Container, BitrateKbps, SampleRate, Channels
```

#### 9. Upgrade from today's config (L1 migration)

```
 BEFORE (v0.4.x, flat)                       AFTER (Live Radio)
 StationName     "IHateRadio"   ─────────►   Stations[0].Name "IHateRadio"
                                ─────────►   PreviousChannelNames += "IHateRadio"
                                             LibraryName  "Live Radio"    ← new channel id
 StationBaseUrl  http://…:7700  ──┐          Stations[0]  Id = "live", Kind SubWave
 StreamPath      /stream.mp3    ──┼──────►     BaseUrl, StreamPath, ClientStreamUrl,
 ClientStreamUrl, Delivery      ──┤            Delivery, Tagline, format copied
 Tagline, stream format         ──┘
 Poll / cover settings          ─────────►   unchanged (global)
 GrantAccessToAllUsers (old)    ─────────►   still ignored on load (card 15b test stays green)

 What users see after the upgrade:
   EnableAllChannels users ............ "Live Radio" at once
   old tile by name (EnabledChannels) . nothing until the admin presses [ Carry access over ]
   blocked from the old tile .......... still nothing (IsEnabledFor + StationAccess honour old blocks)
 Never configured (empty StationBaseUrl) → no stations; the old tile name is still recorded
 (corrected in L1: 0.4.x listed an empty tile too, and an admin may have blocked it).
```

#### 10. "Check" button logic (add-station probe, admin only)

```
 pasted URL
     │
     ▼
 passes the URL rules? (http/https, no userinfo, no fragment; query allowed) ──no──► ✕
     │yes
     ▼
 GET with Icy-MetaData: 1 through the plugin's no-redirect client, headers only, then close
     │
     ├─ 3xx ─────────────────────────► ↪ "redirected to X; use that address"
     ├─ playlist (.pls/.m3u, or its content type) ► read ≤ 256 KB, take the first entry, re-check it
     ├─ audio/mpeg ──────────────────► MP3 ✓
     ├─ audio/aac, audio/aacp ───────► AAC ⚠ (Chromecast)        ← L0 decides allow or block
     ├─ HLS (mpegurl) ───────────────► ⚠ or ✕                     ← L0 decides
     └─ anything else ───────────────► ✕ "not an audio stream"
     │
     ▼
 GET {origin}/api/state answers like SUB/WAVE? ──yes──► Kind SubWave, prefill name, tagline, format
     │no
     ▼
 Kind Stream, name from the icy-name header
```

#### 11. Now-playing poller, many stations (L4)

```
 every poll interval
   for each enabled SubWave station  (Stream stations skipped until L6)
     GET {base}/api/now-playing                       (no-redirect client, 256 KB, 8 s)
       ├─ failed / null feed → that station only: health = failing, state cleared, logo + default text
       └─ ok                 → State[station.Id].Update(feed);  Health[station.Id] = ok
                                └─ changed? → find the Audio item (external id = station.Id)
                                              save cover + overview (LiveItemArtwork, sniffed,
                                              negative cache per station + cover id)
```

#### 12. Request and status flow with many stations (L5)

```
 browser ── POST /Jellywave/Request {stationId, text, name} ─► [StationAccess: user can see the tile?]
                                                                 │ no → 403
                                                                 ▼ yes
                                              stationId is an enabled SubWave station? ── no → 404
                                                                 ▼ yes
                                              station POST /api/request ──► receipt
                                              RequestReceipts[requestId] = (userId, stationId)
 browser ── GET /Jellywave/Request/{id} ─► owner check (card 13) → station from the receipt, not the URL
```

#### 13. Card order

```
 card 17 (v0.4.1.0) ─► card 18 (v0.4.2.0) ─► L0 spike ─► L1 model+migration ─► L2 many items ─┬─► L3 config page ─┐
                                              (AAC/HLS)    (+ rename bridge,                   └─► L4 poller ──────┤
                                                            Hard Stop review)                                      ▼
                                                                    L7 docs+threat model ◄─ L6 (opt) ◄─ L5 routes + page
```

#### 14. Codec mix in the wild (radio-browser, 63k stations)

```
 MP3   ██████████████████████████████████  68%   ← target first; Chromecast-safe
 AAC   ██████████████                      28%   ← L0 decides
 HLS   ███                                  7%   (overlaps the codecs above)
 Ogg   ▌                                    1%
```

### Cards

- **L0 Spike: stream matrix. Done by MI 2026-10-27** (server side; client side is in the host checklist).
  Headers fetched without following redirects; relay check = Jellyfin 12.3's own `jellyfin-ffmpeg`
  on the test host reading 6 s of each URL and transcoding to MP3:

  | Stream (public) | Content-Type | Relay |
  |---|---|---|
  | SomaFM Icecast MP3, https and http | audio/mpeg | ok |
  | SomaFM MP3 with an added `?query` | audio/mpeg | ok |
  | Radio Paradise MP3 (icy-metaint 16000) | audio/mpeg | ok |
  | SUB/WAVE test station | audio/mpeg | ok |
  | SomaFM AAC, KEXP AAC (icy-metaint 4096) | audio/aac | ok (AAC LC) |
  | Radio France FIP HLS (`.m3u8?id=…`) | application/vnd.apple.mpegurl | ok (AAC LC, 48 kHz) |
  | SomaFM `.pls` and `.m3u` playlist URLs | audio/x-scpls, audio/x-mpegurl | **fails**: ffmpeg cannot open them; the plugin resolves playlists |

  Decisions (MI, within the approved scope): MP3 and AAC are accepted in both modes; in Relay Jellyfin
  transcodes to what each client supports, so AAC is safe there (Chromecast included); the probe
  warns about AAC only for Direct. HLS is accepted for Relay only, and only if L2 confirms how
  `MediaSourceInfo.Container` reaches ffmpeg's input arguments (otherwise HLS is refused by the probe
  and deferred). Query strings are fine. Client playback (web, Android, Direct mixed content) is checked
  by HI at the test stop.
- **L1 Station model, migration, rename bridge** (elevated risk, Hard Stop review).
  **Accepted 2026-10-27** (68afb48, cf2a5af, 48f94fa, 6cc42ee, f73904a; fix rounds 288dfc9, d37f7e2, ff506d7,
  c667509, 1c3025f, ca92323, 31521b1; acceptance pass 881846d). RA: first pass CHANGES REQUIRED (R1: the production
  wiring could be removed with every test green; S1–S6), second pass CHANGES REQUIRED (F-R1: a page save on a failed
  migration lifted old blocks), third pass ACCEPT. 439/439 tests; 43 RA and 21 DA mutants caught, R08 equivalent.
  HI reviews the authorization change before the merge (Hard Stop); host evidence comes with the v0.5.0.0 test.
  - Add `StationDefinition`, `StationKind`, `Stations`, `LibraryName` and `PreviousChannelNames`.
    Migrate as in diagram 9.
  - Old blocks are honoured in `IsEnabledFor` and `StationAccess`.
  - Add the "Carry access over" admin route on `AccessGranter` (grants and blocks, add-only, under its
    lock, counts returned). Keep the card 15b reflection test green, so only the admin controller writes policies.
  - `StationUrls` gains a whole-URL validator for `Stream` (query allowed; no userinfo, no fragment).
    The `SubWave` rules stay as they are.
  - Tests:
    - real-shaped old XML: one with `GrantAccessToAllUsers`, one never configured, one with a
      refused URL;
    - a block on a previous id refuses on both paths;
    - the carry-over is add-only and idempotent: the second run reports 0 carried;
    - a rename on the config page records the outgoing name.
  - **Corrected in implementation (DA, L1):**
    - `PreviousChannelNames` holds names, not ids (ids derived through `GetNewItemId` when used, so they
      follow `EnableCaseSensitiveItemIds`). A name is not recorded when it derives the current id or an id
      already listed.
    - Diagram 9: a never-configured install also records its old tile name ("SUB/WAVE"), since 0.4.x
      listed the empty tile and it may carry blocks. A fresh 0.5.0.0 install gets the same entry; harmless.
    - Migration trigger: `BasePlugin<T>` 12.3 has no load hook (lazy `Configuration` getter, private
      `LoadConfiguration`), so `Plugin`'s constructor reads the configuration, migrates, saves, then sets
      `Plugin.Instance`. The constructor takes `ILibraryManager` (already resolved by
      `ApplicationHost.SetStaticProperties` before `FindParts` creates plugins) and `ILogger<Plugin>`.
      `Plugin` is now sealed (CA2214 with a virtual save from the constructor).
    - Rename capture: `Plugin.UpdateConfiguration` also keeps every name the current configuration lists
      (union), so a client that drops the list cannot lift old blocks; a posted body with
      `SchemaVersion` 0 is migrated before the save.
    - Read-only user access for the channel: new `IUserReader` (`GetUser` only), `IUserStore` extends it;
      registered as a second, stateless `UserManagerStore` (no service-locator forwarding).
      `PolicyWriterDependencyTests` now also guards `UserManagerStore` itself (no type may take it).
    - `RenameBridge` (one singleton) derives the previous ids for the channel, `StationAccess` and the
      granter. `StationAccess` checks the old block right after the user lookup, before the channel
      lookup, so an old-blocked user gets 403 even while the channel is missing.
    - Admin routes: `GET /Jellywave/CarryAccess` (status: earlier names, pending count, or the collision
      text) and `POST /Jellywave/CarryAccess` (counts: carried grants, carried blocks, already current,
      untouched). The grant button's text change moved here from L3.
    - The item's codec and container stay MP3 in L1; the station's `Codec`/`Container` are stored and
      migrated but first read in L2. `MediaSourceSchemaDate` unchanged (the media source shape did not
      change). `DataVersion` now hashes the tile name and the primary station's fields.
    - `scripts/verify-jellyfin.sh` reads `LibraryName` (default "Live Radio"), so the host check of the
      L1 build finds the renamed tile; the per-station check stays in L2.
    - The tile-name fallback is "Live Radio"; the station-name fallback stays "SUB/WAVE".
  - **Fix round after RA (DA, L1):**
    - R1: `PluginWiringTests` build a real `Plugin` on a temporary configuration folder (non-parallel
      collection; `Plugin.Instance` reset) and drive the DI constructors of `StationAccess`,
      `JellywaveChannel` and `AccessGranter` through it. The `Plugin.Instance` lookups stay where they were.
    - S1: the `Plugin` constructor never throws. A load failure runs on defaults in memory, a migration
      failure runs the file unmigrated; both logged at Error. `Migrate` derives every id before changing
      anything (no half migration), and `RenameBridge.PreviousIds` counts the 0.4.x tile name of an
      unmigrated configuration, so old blocks hold after a failed *migration* (addition beyond the brief, fail
      closed). They do not hold after a failed *load*: the plugin runs on defaults and cannot know the real old
      name; the empty tile shows, without content, and the page warns (second fix round).
  - **Second fix round (DA, L1):**
    - F-R1: `CaptureRename` records the 0.4.x tile name when the configuration in effect is unmigrated, so a
      page save after a failed migration (posted as schema 0 with `Stations[0]`, so kept, not migrated) no
      longer drops it.
    - F-S1: `Plugin.RunningOnDefaults` after a failed load; `GET /Jellywave/Health` carries
      `ConfigurationWarning` and the page shows it; cleared by the next successful save.
    - F-S2: tests for a carry-over whose save throws (no names recorded) and for a carried-name record during
      a page save (one save gate, configuration read inside it); the RA's spec5 R01, R02, R03, R09 are caught.
      R08 survives as an equivalent mutant: on the early return `names` is always empty.
    - F-N1: lock order documented at both gates: the granter's `SemaphoreSlim`, then `Plugin`'s save gate.
    - S2: the carry-over and its count skip earlier ids that belong to another registered channel
      (`LiveItemFinder.OtherChannelIds`); `CarryOverStatus.SkippedOtherChannel` and one page line.
    - S3: `UserManagerReader : IUserReader` (not an `IUserStore`); `IUserStore` still extends `IUserReader`.
    - S4: `CarriedChannelNames`, recorded by `Plugin.RecordCarriedNames` after a finished run and merged on
      save; `UpdateConfiguration` and `RecordCarriedNames` share one lock (`PrepareSave` moved inside it).
      A run with nothing pending returns zero counts without reading users.
    - N2: a schema-0 POST is migrated only with no stations and the default tile name; otherwise it is kept
      and marked schema 1 (so the next start does not migrate it again).
    - N4: `Classify` has an overload with the earlier ids; the grant pass counts an old-blocked user as
      blocked.
    - Mutations: every mutant in the RA's spec.json and spec2.json is caught, including the earlier survivors
      M06, M14, M20–M23, M27–M29, M35–M36 and M09 (new D-format test). M04, M37, M38 and M40 no longer matched
      the changed code; they were re-expressed (spec3/spec4 in the session scratchpad) and are caught, along
      with 16 new mutants for this round (S1–S4, N2, N4).
- **L2 Many items.** `GetChannelItems` returns one item per enabled station. `DataVersion` hashes the
  **Accepted 2026-10-27** (be1ff54, 43ff206, 37fa31b, cddd61d, 03f32ab, bea0c3a, 9478bc7, 2c7ceb5; fix rounds 283bb1c,
  ca628c6, 5f1e680, 2c0e66d, 67b86c3, 7d1dac5). RA: first pass CHANGES REQUIRED (R1: a refused duplicate primary wrote
  onto another station's item every poll; R2: a former primary kept its now-playing line), second pass CHANGES REQUIRED
  (F1: the logo download budget left new items blank), third pass ACCEPT. 548/548 tests; DA 70 and RA 25 mutants run,
  survivors either killed or judged equivalent (N02) or cosmetic (N12); the `Retain` cleanup is untested by choice.
  Also fixed here: ffmpeg input quoting (`Uri.AbsoluteUri`), tracked in THREAT-MODEL "Known issues" for 0.1–0.4.2.
  station list; bump `MediaSourceSchemaDate`. `LiveItemFinder.Resolve` keys on `LibraryName`, and
  `FindLiveItem` takes a station id. Logos for plain streams are downloaded through `StationClient`
  and `ImageSniffer`, then saved with the existing `LiveItemArtwork` path. `verify-jellyfin.sh` checks
  every enabled station's item and PlaybackInfo.
  - **Corrected in implementation (DA, L2):**
    - Grid order: `InternalChannelFeatures.DefaultSortFields` is only reported to clients and the provider is asked with
      no sort (12.3 `ChannelManager.cs:594`, `721-728`); clients list channel items by `SortName` (jellyfin-web's default
      `IsFolder,SortName`; the repository's default without an order). `ChannelItemInfo` has no sort field and
      `IndexNumber` is set only at creation and is in the user-data key, so the poller writes each item's
      `ForcedSortName` ("<rank, 4 digits> <name>") and reconciles it every poll, like the overview.
    - More refusals than the URL rules, each one Warning per streak naming the station: an id that is not 1–64 letters,
      digits, `_`, `-`; a duplicate id (ignoring case; the first in grid order wins); an unknown kind; a codec other
      than mp3/aac; a container other than mp3/aac/hls (`m3u8` = hls); a stream URL ending in `.pls` or `.m3u` (L0:
      ffmpeg cannot open them). A blank container follows the codec. A refused client stream URL warns once per
      streak instead of on every listing.
    - HLS: decided for Relay only from the 12.3 source (DESIGN "Delivery modes"). A station is HLS when its container
      is `hls`/`m3u8` or its URL path ends in `.m3u8` (as `M3UTunerHost.IsManifest`); its source has `Container=hls`.
    - Security fix found on the way: `Path` and `EncoderPath` are now `Uri.AbsoluteUri`. `Uri.ToString()` turned `%22`
      into `"`, and Jellyfin 12.3 quotes a remote ffmpeg input without escaping it (`EncodingUtils.GetInputArgument`),
      so a quote in an admin-pasted query (now allowed for plain streams, and already allowed in a SUB/WAVE stream
      path) could end the argument and add ffmpeg options. For L7: threat model line.
    - Items other than the primary one carry the default overview; the channel's description and home page stay the
      primary station's. Jellyfin sets an item's name only when it creates it (`ChannelManager.cs:1027-1029`), so a
      station renamed later keeps its item's old name; `verify-jellyfin.sh` matches items by name and says so in its
      FAIL line. For L3: the config page could warn on rename.
    - Logos: transport failures go into the negative cache too (covers retry them next poll), since a logo never
      changes per track. `StationLogos.Forget` runs for the primary station each poll, so a station that stops being
      primary gets the embedded logo instead of keeping its last cover. `LiveItemArtwork`'s applied key is not reset
      when the primary station changes; its origin key already makes the new station's cover download (L4 keys it by
      station id).
    - `JellywaveChannel.LiveItemId` is gone; `FindLiveItem(channel, stationId)` returns null for a blank id without a
      query. `MaxPageSize = 10` is left as it was: 12.3 only reports it (`ChannelManager.cs:592-595`, `CanFilter =
      false`) and never pages the provider; a GitHub code search of jellyfin-web and jellyfin-androidtv found no reader.
  - **Fix round after RA (DA, L2):**
    - R1: `PrimaryStation()` returns the candidate (`PrimaryCandidate()`, the old rule) only while `StationSources.Publish`
      lists it; no fallback to the next SUB/WAVE station. The poller also takes the primary from the listed sources.
      Health uses the candidate to explain: "the SUB/WAVE station is not listed: <reason>" (new
      `StationSources.NotListedReason`). Tests that built an id-less SUB/WAVE station now give it `Id = "live"`; a
      station without a URL now has no feed item id (it is not listed).
    - R2: the poller resets a non-primary item's overview to the default text (write only on change).
    - S1: a failed logo save puts a downloaded logo in the negative cache (`StationLogos.SaveFailed`); save errors are
      logged once per station and streak (`WarningStreaks`, which gained `End`).
    - S2: `StationLogos.MaxDownloadsPerPoll` = 2 per poll (`BeginPoll`); the rest follow next poll.
    - S3: `verify-jellyfin.sh` requires a play method for every source; the mp3-only profile transcodes any input.
    - S4: the listed tests; a separate Warning when every enabled station is refused.
    - S5: `.mpd` refused; DESIGN wording on `M3UTunerHost.IsManifest` corrected.
    - S6: `LiveItemArtwork.Reset` when the primary station id changes.
    - Nits: Cast wording; logo warnings keyed per station by key and kind of problem, ended by a good logo.
  - **Second fix round (DA, L2):** F1: over the download budget an item without an image gets the embedded logo at once
    (the real logo follows on a later poll). S-a: test that a failed embedded-logo save leaves the negative cache alone.
    S-b: route test with a refused (duplicate-id) primary: Feed 200 with no item id, Avatar 404, Schedule 200
    "unavailable", LikeStatus disabled, the station never asked. Nit: `StationLogos.Retain` and the poller's save-error
    streaks drop stations that are no longer listed (not covered by a test).
- **L3 Config page.** Station table, add/edit dialog, and the probe from diagram 10 (extends the
  **Accepted 2026-10-27** (7571fdd, 95099b5, 44aadca, bed8d77, fcc43dd, 38216ba, aaf15fe; fix round 6544a65, f9e1eb2,
  9bd243f, 3e9f3eb, 8e29e7a). RA: first pass CHANGES REQUIRED (R1: a late probe answer filled the wrong dialog or the wrong
  URL; R2: probe cancellation and the SUB/WAVE mount checks untested), second pass ACCEPT. 704/704 .NET tests and a
  committed page harness (54 checks, plain node, one CI step). Rename finding confirmed against v12.3: favourites and
  play state survive a station rename. Carried to L7: the THREAT-MODEL wording of the probe's station-chosen requests
  (any non-audio address, the HTTP status, Kind/BaseUrl revealing an internal SUB/WAVE); the SUB/WAVE-specific warning
  when a URL is changed without a Check; a harness scenario for re-checking after a confirmed downgrade (K4); README.
  admin `GET /Jellywave/Station`). Per-row health. The grant button's text becomes "Add the tile
  to every user now".
  - **Corrected in implementation (DA, L3):**
    - Probe route: `POST /Jellywave/Probe {Url}` (`StationProbe`, `StationClient.ProbeStreamAsync`) replaces
      `GET /Jellywave/Station?baseUrl=`, which is removed with its base-URL redirect suggestion (`SuggestedBase`). A POST
      keeps a stream URL's query out of request logs. The answer is 200 with `Ok`, `Kind`, the URLs, the format, name,
      tagline, `Redirect` and result `Lines` (level + fixed text); a refused URL is a line, not a 400.
    - Header-only reading: `ResponseHeadersRead`, the content is never touched unless the answer is a playlist, and the
      response is disposed. Proven by fake bodies that throw when read or buffered, and by a socket test against an
      endless MP3 stream through the registered client (must finish within 6 s).
    - Additions within the diagram: `application/x-mpegurl` counts as HLS (Apple's alternative type) besides
      `application/vnd.apple.mpegurl`; a `.m3u8` path is HLS whatever the type. A playlist over 256 KB is refused
      ("larger than 256 KB"), not parsed from its first 256 KB; a declared length over the cap is not read at all. A
      SUB/WAVE base URL pasted as is (its web player, not audio) is also tried as a base, and the feed's `stream.mount`
      becomes the stream (one more headers-only check), so an upgrading admin can paste the old base URL. Candidates
      for `/api/state`: origin, the stream's directory, and (non-audio only) the address itself. Whole probe bounded by
      `ProbeBudget` = 30 s on top of each call's 8 s (at most seven requests).
    - Shoutcast v1: .NET 10 refuses `ICY 200 OK` with `HttpRequestError.InvalidResponse` ("Received an invalid status
      line: 'ICY 200 OK'.", checked on a socket). Named only when the message carries `'ICY ` (the only clue the client
      gives); otherwise "the server's answer is not valid HTTP". A socket test pins the .NET behaviour.
    - **Station rename and favourites: the brief's premise does not hold on 12.3.** `Name` is in
      `Audio.GetUserDataKeys()` (`Audio.cs:111`, first key at 124), but 12.3's `UserDataManager` keeps rows per item id,
      keeps rows under old keys (`UserDataManager.cs:79-92`) and resolves a row by trying every key, the external id
      included, then any row of the item (`ResolveUserDataRow`, 386-403). So a rename keeps favourites and play state and
      leaves one stale row per user and rename. The page says the favourite is kept; DESIGN "Station rename" has the
      lines. THREAT-MODEL's card 7 control row now says `Name` is written on an admin rename (L7 reviews it).
    - Rename write: `NowPlayingService.ReconcileName` on every listed item (SUB/WAVE and plain), `MetadataEdit` only on
      change, one Information line; `JellywaveChannel.ItemName` is shared with `GetChannelItems`.
    - The dialog is an inline panel under the table, not a modal (no jellyfin-web dialog helper in a plugin page);
      Escape cancels it. Remove confirms in the row ("Remove X? [Remove] [Keep]") instead of `window.confirm`, which the
      Android app's WebView may not show. No drag; ↑/↓ only.
    - A URL that has not been checked successfully can still be saved, as a plain stream with the format as entered,
      after a second press (a station that is down right now can be added). Edit without a re-check keeps the stored
      kind and URLs.
    - New ids come from `crypto.getRandomValues` ("N" format, version-4 bits): `crypto.randomUUID` needs a secure
      context and Jellyfin is often plain http.
    - Section 1 shows the channel status line (collision) under the tile name; the old per-primary poller line is gone
      (the table shows every station); section 3 keeps one poller line with `LastFetchSeconds`.
    - Save is refused while the dialog is open (its draft would be lost silently).
    - Tests (after the fix round): `StationProbeTests` (98 cases), `StationProbeSocketTests` (2), `ConfigPageTests` (8: no markup sink, probe route,
      id source), the rename poll test, and a page-shaped JSON save round trip in `PluginWiringTests`. The page script
      was also run against a scratch fake DOM (not committed: there is no JS harness) for the flows below.
    - Mutations (DA, spec `l3-da.json` in the session scratchpad, 47 mutants on the client's probe read, the probe, the
      route and the rename): 47 caught. L39 (stream path not verified) survived the first run and is killed by the
      doubled-slash test; L02 and L16 first failed to build (spec whitespace) and are caught after the fix. Not mutated:
      the page script (no harness; see the checklist).
  - **Fix round after RA (DA, L3):**
    - R1: a Check answer is applied only when the dialog, the check number and the URL are still the ones asked for;
      Enter does nothing while a check runs; opening or closing the dialog resets the check (late answers are dropped and
      Check is usable at once). A URL changed while it was checked says "press Check again".
    - R2 and S7: probe tests for caller cancellation at `/api/state` and the feed, the mount redirecting, answering
      `text/html`, carrying `#x` or `.mpd`, or blank (`/stream.mp3`), HLS kept under a SUB/WAVE feed, the playlist body's
      own timeout (`StationClient` takes its per-call budget in an internal constructor, so the test runs in 200 ms),
      the IOException category, the not-audio timeout, `[FromBody]`, feed channel and bitrate bounds, and a case-only
      rename. A UTF-8 BOM is now also stripped from the body in `StationClient`.
    - Page tests: `Jellyfin.Plugin.Jellywave.Tests/js/configPage.test.js`, plain node, in `CLAUDE.md` and as one CI step
      ("Configuration page tests"; nothing else in the workflows changed). 54 checks: the earlier 28 flows, R-A to R-G,
      late answers in a new or replaced dialog, new ids, and the fix-round cases. Against the page before this round it
      fails 12 checks.
    - Mutations, fix round: the RA's `l3-ra.json` on the fixed code catches every mutant except R10, R16, R22 and R26
      (agreed); R03, R11 and R12 no longer matched the changed code and are restated in `l3-da2.json` (R03b, R11b, R12b,
      caught). `l3-da2.json` (19 server mutants on the fixes): 18 caught, E08 equivalent (`Candidates` applies the base
      URL rule to the stored base again). Page mutants (`l3-da2-page.py`, against the committed harness): the RA's
      pm1–pm5 and 22 of ours, all caught except P02 (every open and close already renumbers the check), P06 (no dialog
      is left to apply to after a close) and P08 (unreachable: Remove closes the dialog, a page load closes it too),
      kept as defence. 704 .NET tests.
    - S1: a redirecting mount is named ("the station's stream at … redirected to …") and has no "Use it".
    - S2: removing the station whose dialog is open closes the dialog with a note; Apply copies `Enabled` from the row.
    - S3: `ProbeRequest.BaseUrl` carries the stored base when a SUB/WAVE station is re-checked; the probe tries it first
      when the address lies under it (same scheme, host, port, path below). A SUB/WAVE station saved as a plain stream
      needs a second press with a warning.
    - S4: the sink list adds `setHTML`, `setHTMLUnsafe`, `parseHTMLUnsafe`, `createContextualFragment`,
      `parseFromString`, `srcdoc`, `.html(`. S5: THREAT-MODEL boundary 1 and an accepted risk for the probe's requests
      to station-chosen addresses. S6: checklist steps 12 and 16–20.
    - Nits: `ProbeBudget` comment (eight requests with the stored base); format lines written after the feed's format;
      name-resolution, TLS and response-ended errors get their own lines; repo map line fixed.
  - **HI host checklist for L3** (Dashboard → Plugins → Jellywave, web client; repeat the starred ones in the Android
    app):
    1. The page opens with the sections in order: tile, stations, now playing, who can see it, station page. The
       migrated station shows as `IHateRadio · SUB/WAVE · MP3 192 · Relay`, enabled, status "ok … ago".
    2. ★ Add station: paste a plain MP3 URL, Check: ✓ Audio line, ✓ "no SUB/WAVE station answers here", name filled
       from `icy-name`. Add, Save: the row's status turns "listed · …logo…", and the tile shows the new item.
    3. Add station with a `.pls` URL: "Playlist read → …" then the audio line; the saved station's URL is the entry.
    4. Add station with an AAC URL: ⚠ AAC line; set Delivery to Direct, Save: the row shows "⚠ AAC in Direct".
    5. Add station with an HLS (`.m3u8`) URL: Direct is greyed out with the HLS note; format column shows HLS.
    6. Add station with an `http://` URL that redirects to `https://`: ↪ line with "Use it"; pressing it fills the field;
       Check again succeeds.
    7. Add station with the second SUB/WAVE station's base URL (no stream path): ✓ "SUB/WAVE detected", name and
       description filled; after Save it polls (status "ok") and gets covers.
    8. Paste `http://user:pw@host/x.mp3` or a `#fragment` URL: "URL not allowed: …", nothing requested. Paste an
       unreachable host: "not reachable: …" within about 8 s.
    9. ★ Keyboard only: Tab to ↑/↓, Enter moves the row and focus stays on the moved row's arrow; Save; the grid order in
       the tile follows within one poll interval.
    10. Remove: the row asks "Remove X?"; Keep restores it; Remove then Save deletes the item from the tile (after a
        "Refresh Internet Channels" run (the task is named 'Refresh Channels') or the next listing).
    11. Untick Enabled on one station, Save: status "disabled"; the item leaves the tile.
    12. Rename a station, Save: within one poll interval the item's card shows the new name. A favourite set before the
        rename (by a second, non-admin user) is still set after a Jellyfin restart, checked as that user (the restart
        empties the user-data cache, so the row really comes from the database).
    13. Edit a station, change nothing, Apply, Save: nothing changes (no new item, same status).
    14. A station name or `icy-name` with `<b>` in it shows the angle brackets as text, never bold.
    15. Rename the tile: the rename notice and "Carry access over" still work; the station list is unchanged.
    16. With the dialog open, press Save at the bottom: it refuses with "Finish the station dialog first".
    17. Add station with an unreachable URL and press Add without a successful Check: the first press warns, the second
        adds it as a plain stream.
    18. Escape inside the dialog closes it without applying.
    19. Check a slow URL (one that takes several seconds, e.g. an unreachable host), press Cancel at once, then Edit
        another station: when the slow answer arrives nothing in that station's dialog changes, and its Check button
        works straight away.
    20. Edit a SUB/WAVE station, change its URL to a plain MP3 stream and Check: Apply asks once more ("found a plain
        stream"); Cancel keeps it SUB/WAVE.
- **L4 Per-station state.** `NowPlayingState`, `ScheduleCache`, `PollerHealth` and the artwork applied
  **Accepted 2026-10-27** (522d08f, c310186, 10db101, b23cdb9, 882e3f1; fix rounds a1ff4e5, 82eee0a, 8b20896, d4866ba).
  Order changed by MI: L4 ran before L3, because L3's status column needs per-station health. RA: first pass CHANGES
  REQUIRED (R1: shutdown during a poll untested, four shutdown mutants survived; S2: the old origin's cover served during
  the fetch phase), second pass ACCEPT with S-A (a regression: a failing pre-fetch lookup counted as a station failure),
  fixed and confirmed in a third pass. 600/600 tests; every required RA mutant caught; R11–R13 skipped by agreement.
  key and negative cache are keyed by station id, replacing card 17's origin key. The poller loops
  over SubWave stations; one station's failure never touches another's state.
  - **Corrected in implementation (DA, L4):**
    - Key: `StationKey` = (station id, origin), not the id alone, so a base URL changed under the same id resets that
      station exactly as card 17 did. Each map holds one entry per station id carrying its origin; a write under
      another origin replaces it, a read under another origin gets nothing. `StationSource` carries a SUB/WAVE
      station's parsed base URL and key; `StationSources.Primary` gives the listed primary source.
    - Polling: two phases instead of a plain loop. Fetch (network only) runs every listed SUB/WAVE station, at most
      `MaxConcurrentStations` = 4 at once; write reconciles the items one by one in grid order, as in L2. Worst case per
      poll: ceil(stations / 4) × 32 s (feed 8 s, state 8 s, cover 8 + 8 s) plus 32 s of logos. Sequential polling was
      rejected because one black-holed station would add 8 s or more to every other station's update each poll.
    - Failures are recorded and logged as fixed categories (`StationFailures`: timed out, could not connect, HTTP n,
      answered with a redirect, not the expected JSON, empty feed, unexpected error). This also changes the top-level
      `LastError` from the exception's message to the category. An unexpected exception inside one station's fetch is
      that station's failure (logged at Error); one outside every station (the channel lookup) is logged and no longer
      written to health, since it belongs to no station. An item write that throws is logged, and the next item is
      still written (before, the whole poll stopped).
    - Log lines name the station (its configured name) instead of the host: "Station {Name} is unreachable or not
      answering with a feed: {category}", "Station {Name} is answering again", "Station {Name} item overview updated",
      "Station {Name} item cover updated". The warning streak is keyed by the station id with its origin, so a URL
      changed to another failing one warns again. Cover and logo save errors share one streak store per station, and
      a cover save that works again now logs one Information line too.
    - The L2 primary switch is gone: `LiveItemArtwork.Reset`, `StationLogos.Forget`, the poller's `_primaryId`, the
      single-station getters of `NowPlayingState` (`Description()`, `Feed()`, `StationState()`, `TrackLine()`,
      `CoverId()`, `FetchedUtc`), `NowPlayingSnapshot.Origin` (replaced by the `OtherOrigin` flag), the web controller's
      `FromOtherStation` and `NowPlayingService.EmptyFeedMessage` (now `StationFailures.EmptyFeed`).
    - Kind changes: `LiveItemArtwork` retains only listed SUB/WAVE ids and `StationLogos` only listed plain streams, so
      an id that changes kind and back gets the right image each time (tested).
    - `ScheduleCache` keeps one copy per station but one fetch gate for all; only the primary's page asks until L5,
      which may want a gate per station. The poller now takes `ScheduleCache` to drop unlisted stations' copies.
    - Health `Stations`: `Kind` is a string; a disabled station's reason is "it is disabled"
      (`StationSources.NotListedDisabled`); a station refused as a duplicate gets no poll fields; a refused logo URL
      reports `failed`. The channel's overview for each SUB/WAVE item comes from that station's state.
  - **Fix round after RA (DA, L4):** the RA judged the production code sound (no race, isolation holds, keying correct).
    - R1: shutdown during a poll is tested. Stopping in the fetch phase rethrows and logs, records and writes nothing
      (RA test adopted; kills R01, R02, R04). Stopping in the write phase (the first item's save cancels; the fake
      library answers the cancelled token as the database does) rethrows, logs nothing and writes no further item
      (kills R03). The write loop checks the token before each item.
    - S1: the RA's tests for R05, R06/R07, R14, R17, R21, R26/R28 adopted into `PerStationPollTests`.
    - S2: `Feed` withholds the image tag while `LiveItemArtwork.AppliedOrigin(id)` is set and differs from the primary
      station's current origin, so the old origin's cover is not served through the whole fetch phase after a URL
      change (the controller now takes `LiveItemArtwork`, read only). DESIGN's "few seconds" corrected.
    - S3: no deadline (agreed). The fetch phase is timed with an injected `TimeProvider`: one Information line per
      streak when it takes longer than the poll interval, and Health's top-level `LastFetchSeconds`.
    - Nits: no cover download for a station whose item does not exist yet (one read-only lookup in the fetch phase,
      the channel resolved once before it); DESIGN cross-reference and `NowPlayingSnapshot.Nothing` wording fixed.
      Skipped by agreement: R11, R12, R13, R15, the `StationHealth.Id` nit.
  - **Final round (DA, L4):**
    - S-A: the fetch phase's channel resolve and item lookup no longer count as a station failure when they throw (a
      library lock): "unknown", so no cover download this poll, the item keeps its image, health and state stay, Debug
      only. A throwing pre-fetch resolve no longer aborts the poll.
    - S-B: the slow-fetch streak ends only below 80 % of the interval (`SlowFetchEndRatio`); the line says "the fetch
      phase alone took … longer than the poll interval".
    - S-C: E14 was not equivalent (an unchanged item awaits no token); the RA's test is adopted (`FakeLibrary.OnQuery`).
      Image saves pass `CancellationToken.None` (12.3 `ImageSaver.cs:277-303` truncates the file before it observes the
      token) and check the poll's token before they start, in `LiveItemArtwork` and, for the same reason, in
      `StationLogos` (beyond the brief).
- **L5 Routes + station page per station.** Every `[StationAccess]` route takes `stationId`, resolved
  **Accepted 2026-10-27** (54e27a6, 1f79c96, c76c289, 0899d6c, 587a2a3, 8a2acae, fff23a5, f54fac5; fix rounds 9dd4f1c,
  c500a9c, 8dc058a, fb65ae1, 9d4183f, 317df57, c45b105). Hard Stop: HI reviews before the merge. RA: first pass CHANGES
  REQUIRED (R1: after a feed 404 an idle page never got its header back), second pass ACCEPT with S1 (a second 404 within
  15 s left an idle page blank) and S2 (the reflection walk was bypassable), both fixed and confirmed in a third pass:
  a refused page retries once per back-off, and an IL scan of the controller and `StationList` forbids every path to a
  station except `CallerStation()` (RT01–RT06 and B01–B08 caught by the rule tests alone). 403-before-404 verified in
  Jellyfin 12.3's own pipeline. 887/887 .NET tests, configPage 54, stationPage 131. Carried: S-a (forbid
  `HttpContext.Items` outside the three accessors) to L7; S-b (an allow-list of plugin static/extension helpers the
  controller may call) as a follow-up; both bypasses RA found (RX2, RX5) are caught by behaviour tests.
  to an enabled SubWave station or 404. `RequestReceipts` stores the station id. `ui.js` state key
  becomes server + user + station (on top of card 18); the header layout follows mockup 2 or 3. The
  reflection test is extended: every station route takes a station id.
  - **Corrected in implementation (DA, L5):**
    - One path: `[StationRoute]` (`StationRouteAttribute`, an action filter, so it runs after the `[StationAccess]`
      authorization filter and after model binding) asks `StationResolver`, the only code that turns an id into a
      station; actions read the result only through `CallerStation()`, which throws without the filter. GET routes take
      `[FromQuery] string? stationId`, POST bodies implement `IStationSubmission` (`StationId`). Ids compare ordinally
      (exact case, no trim). The controller no longer takes a configuration source; it takes `StationResolver`.
    - Absent id = primary (`StationSources.Primary`, no fallback). ASP.NET Core binds an empty or blank query value as
      absent, so `?stationId=` is the primary too; `""` in a JSON body is 404. A `stationId` in a POST's query is not bound.
      Behaviour change for an old page: with no listed primary every route now answers 404 (L2's Feed 200 / Schedule
      "unavailable" / Like disabled), so the old page removes its header (card 18).
    - Receipts hold the station's `StationKey` (id and origin), not only the id (safe option beyond the brief): a receipt
      whose station is no longer listed, or whose base URL changed under the same id, is "lost track" without a station
      call. Ownership (403) and lost track are answered by the filter (`FromReceipt = true`) before the action runs.
    - `GET /Jellywave/Stations` also returns `ChannelId` (the page needs it before any feed) and `ChannelName` (the tile
      name, for the legacy drawer link). `FeedResponse` gains `StationId`, so the page drops a reply naming another station.
    - "Song-id check": server side it stays the format check (`IsValidId`) before anything reaches the named station; the
      card 18 check (captured song id) is the page's, now under the station epoch too. No server check that the song is
      the one on air (the station answers 409 for an ended track).
    - `ScheduleCache`: one fetch gate per station (the L4 note), so a black-holed station holds up only its own schedule;
      `Retain` drops unlisted stations' gates without disposing them (one may be held).
    - `ui.js`: a switch rebuilds the header (fresh request form and references) and bumps the epoch; the list is read
      again at most once a minute while the header shows or a station plays, and on every playback start; 403/404 on the
      list or a feed drops the list and the header, with the back-off on the list (403 five minutes, 404 15 s). The
      harness found that the playing station's repaint waited for the header's own feed; another station's item is now
      repainted without waiting. The picker is a plain `<select>`; no ✦ badge.
    - Tests: `StationRouteTests` (every route × ten unlisted ids, listed id, absent id, no primary, receipts per station,
      filter fail-closed), `StationListRouteTests` (grid order, no URL serialized in either casing, the L4 tag rule),
      `StationRoutePipelineTests` (ASP.NET Core's real MVC pipeline on loopback with a test authentication handler:
      403 before any body or station id, 404 for an allowed caller's unlisted id, no station asked), `ScheduleGateTests`,
      `StationPageScriptTests`, the extended reflection test, wiring and registration tests. Page harness
      `js/stationPage.test.js` (plain node), in `CLAUDE.md` and in the renamed CI step "Page tests (configuration page,
      station page)".
    - Mutations (DA, specs in the session scratchpad): server `l5-da.json`, 36 mutants on the resolver, the filter, the
      receipts, the controller, the schedule gate and the registration: 35 caught; C09 (persona id check in `Avatar`) is
      equivalent, since `StationClient.GetAvatarAsync` checks the id again and the answer is the same 404. First run 32/36:
      S10 (plugin not ready), C08 (lyrics refusal text) and C11 (schedule copy per station) were killed by new tests.
      Page `l5-da-page.py`, 40 mutants against the harness: 35 caught. The five left are masked by a second guard: J07
      and J32 by `ensureHeader`'s own `data-station` check, J15 and J16 by each other (the playing feed is checked both
      when it arrives and before it is painted), J23 by the reset channel id (no header without the new list).
  - **Fix round after RA (DA, L5):** the RA found the server side sound (403 before 404 holds in 12.3's own pipeline;
    receipts fail closed).
    - R1: a feed 404 (the header's station left the list) dropped the header and the list, and an idle page asked again
      only after a DOM change or a playback event. Now a feed 404 removes the header and reads the list again at once,
      skipping the back-off (`stationGone`); the list's answer moves the header to the default station, and only the
      list's own 403 or 404 refuses the page. A second feed 404 within 15 s (the list still names a station whose feed is
      gone) is a refusal, so the two cannot loop. A feed 403 stays a refusal (card 13). Harness: the RA's idle-page probe
      (two minutes without a DOM change), the no-loop case, and the RA's K01, K12 and K20 scenarios.
    - S2: the controller no longer holds `StationResolver`. `GET Stations` and the image tags go through `StationList`
      (ids, names, item ids, tags; `ImageTag` moved there from the controller, which also drops `LiveItemArtwork` and
      `IImageProcessor`). Reflection rule: no constructor parameter or field of the controller is, or hands out through a
      public property, field, method result or `out` parameter (walked transitively over the plugin's types; a method
      that takes a `Uri` is not followed, since it only derives from a URL the caller already has), a `StationSource`,
      `StationDefinition`, `PluginConfiguration`, `Uri` or the resolver; a control test proves the walk sees each of
      those shapes. The text ban list stays as a second check for static paths (`Plugin.Instance`, `StationSources.`).
    - N1: `ScheduleCache.GateCount` (internal) and a test that `Retain` drops unlisted stations' gates.
    - N3: both guards of each masked pair stay, with a comment at each saying it is deliberate defence in depth.
    - Mutations, fix round (fresh worktree of 8dc058a): the RA's `l5-ra.json` catches every mutant except RS23 and RS24
      (agreed); RS21 is now caught (N1). RS06–RS13 no longer compile (the controller has no resolver to bypass with) and
      RS14/RS15 no longer match (the list moved to `StationList`); they are restated in `l5-da2.json` (RS06b: a resolver
      field in the controller; RS13b: the station from `Plugin.Instance`; RS14b, RS15b) and caught. `l5-da2.json` (S2,
      N1: a resolver or URL field in the controller, `StationList` handing out the resolver, sources, a station through
      `out` or a base URL, the image-tag rule, the list names, the gate count): 14 of 14 caught. Page: the RA's
      `l5-ra-page.py` on the committed harness catches K12 and K20; K01 and K03 no longer match and are restated in
      `l5-da2-page.py` (K01b caught; K03b, like K03 before, survives and was not required). `l5-da2-page.py` (R1:
      feed 404 refusing again, no re-read, back-off kept, no loop guard, a guard that never refuses, header kept, 403
      taken as a station gone, unforced re-read): 9 of 10 caught, K03b the survivor.
  - **Final round (DA, L5)** after the RA's ACCEPT:
    - S1: a refused page (the list's 403 or 404, a feed 403, or a second feed 404 within 15 s) arms one retry for the
      end of its back-off, so an idle page comes back once allowed; refused again, it retries once per back-off. The
      RA's RB1 (two stations leave within 15 s) and RB3 (the channel answers 404 and comes back) are harness scenarios;
      the "does not loop" scenario no longer nudges the DOM before the header returns.
    - S2: the walk follows every method (the `Uri`-parameter exemption is gone); its one remaining path, the probe's
      redirect `StationClient.ProbeStreamAsync()<StreamAnswer>.Location`, is allow-listed with its reason, and the list
      must hold no stale entry. New `StationRouteIlTests` (option b, an IL scan with `MethodBody.GetILAsByteArray` and
      `Module.ResolveMember`; `IlReferences` in the test project) over the controller and its nested compiler types,
      and over `StationList`, with the rules in DESIGN "Station per route". `NameOf` is a fourth station opener (the
      feed's station name); actions read the station id through `KeyOf`. Limits: dynamic code and reflection through
      names the list does not cover could still evade a static scan; the forbidden list includes `Type.GetType`,
      `Type.Get*`, `MethodBase`/`PropertyInfo`/`FieldInfo` members, `Activator` and `dynamic` call sites.
    - N1: a harness scenario that a session change resets the re-read guard (G1). N2: checklist step 1 names the
      WebView's HTTP cache.
    - Mutations (fresh worktree of 317df57): the RA's RT01–RT06 each fail the rule tests run alone
      (`StationRouteCoverageTests`, `StationRouteIlTests`); RT07–RT09 (the L4 tag rule) are caught by the full suite.
      Own bypasses `l5-da3.json` B01–B08 (the station from `HttpContext.Items`, a URL from a receipt's origin, static
      configuration in a lambda, `StationList` handing out an origin, reflection, an opener reading a definition's URL,
      the list naming by URL, an action opening the station): all caught by the rule tests alone. Page `l5-da3-page.py`
      (G1, the retry removed, fired at once, doing nothing): 4 of 4 caught. `l5-da2-page.py`: 9 of 10 as before (K03b);
      the RA's K12 and K20 caught.
  - **HI host checklist for L5** (two SUB/WAVE stations and one plain stream enabled; web client as a non-admin user,
    starred ones again in the Android app after a force-close):
    1. **First, before any force-close of the Android app** (old cached page): open the tile in the app. The old script
       shows no picker even with two SUB/WAVE stations listed; that is how to tell it ran. Its header shows the primary
       station (it sends no station id). Caveat: the server answers the current `ui.js` whatever `v` the page asks for,
       so this only shows something while the app still holds the old `index.html` and the old `ui.js` is still in the
       WebView's HTTP cache (a day at most); a picker means the new script ran, so the check says nothing then.
    2. ★ Open the Live Radio tile: the header shows above the grid with a picker naming the two SUB/WAVE stations (not
       the plain stream); the first is selected.
    3. ★ Pick the second station: cover, track, DJ and panels change to it. Reload the page: the second is still picked.
       Sign in as another user: that user starts on the first station.
    4. ★ Play from the header: the second station plays; the playing bar shows its track and follows a track change
       within a few seconds; Stop works.
    5. While the second station plays, pick the first: the playing bar keeps showing the second station's track; the
       header shows the first station with Play.
    6. Play the plain stream from the grid: the playing bar keeps Jellyfin's own text (no repaint); the header stays.
    7. Like, Lyrics and Today's schedule on the second station show that station's data; the first station's like
       count does not change.
    8. Send a request on the second station: that station's DJ answers and the page shows the acknowledgement.
    9. With the second station picked and the page left alone (no clicks), disable it and Save in another tab: within a
       few seconds the header shows the first station and the picker is gone.
    10. While the second station plays, disable it and Save: the playing bar stops being repainted within about a minute
        (it keeps its last text); the server log has no ERR or WRN from Jellywave for it.
    11. Raw API with a non-admin session: `/Jellywave/Stations` contains no `http` and no stream path; `/Jellywave/Feed`
        with the plain stream's id, the disabled station's id, or `nope` answers 404. An API key, and a user blocked from
        the tile, get 403 from `/Jellywave/Stations` and from `/Jellywave/Feed?stationId=<id>`, and also 403 (not 404)
        from `/Jellywave/Feed?stationId=nope`: the gate answers before the station id is looked at.
    12. User B polling user A's request id gets 403 (card 13 check, now per station).
    13. Only plain streams enabled: no header and no drawer link.
    14. A SUB/WAVE station named with `<b>` shows the angle brackets as text in the picker.
    15. Server log after the deploy: 0 ERR/WRN from Jellywave.
- **L5b Header follows playback (HI 2026-10-28, host test).** The L5 picker header looked right in the mockup, but
  on the host HI expected the header to follow what plays. HI's rule: (a) no header when nothing plays or when the
  playing item is not a listed SUB/WAVE station; (b) while a listed SUB/WAVE station's item plays (paused included),
  the header shows that station, switches when playback moves to another SUB/WAVE station, and goes on stop or a
  plain stream; features the station turns off stay hidden. `ui.js` only, no server change: `followPlayback()` sets
  the header's station from `playingStation()` on every playback event, tick, `viewshow` and DOM pass (epoch reset on
  a change); picker, last-pick memory (`localStorage`) and the Play button removed; the schedule is asked when the
  header is built and its panel shows only when the station publishes one. Paused: jellyfin-web 12.3's
  `isPlaying(player)` stays true while paused (fact, see DESIGN "The page"). Requests: no outcome means only "this
  station does not take requests" (`unavailable` mixes SUB/WAVE's 503 for disabled and autopilot with outages), so the
  form stays and shows the station's message. Harness: picker scenarios replaced; idle, plain stream, start, switch
  with stale replies, pause, stop, plain stream after SUB/WAVE, opening the tile while playing, features off, request
  refusal, 403; a flag silences the fake `MutationObserver` so the event and tick paths are proved alone. Checklist (e)
  rewritten. **Built by DA 2026-10-28** (df7f6fa, f18eaf1, e0be5e0). RA: FIX (checklist (e) feature steps could not
  fail; test gaps). Fix round: (e)13–20 compare the page with a note taken in S1's admin and on S2's own player; the
  fake stop fires `playbackstop` before it clears (as jellyfin-web does); scenarios for two views of the tile, a hidden
  cached view, `viewshow` re-pointing, the portrait revoked on stop, a closed schedule not refetched, likes off with a
  song id, same-song lyrics across a switch; DESIGN notes the header rebuild on any `isPlaying` gap.
  **Accepted 2026-10-28** (MI, after the fix round 47a698b, 46280c1, 0cb1ca6): station page 178/178, config page
  68/68, dotnet 901/901, 0 warnings; the RA's mutant runner catches 30 of 34 on the final `ui.js` (the 4 survivors
  write only into a header already detached from the page). Host check: checklist (e).
  **Host check 2026-10-28 (HI, web):** the header shows on SUB/WAVE stations and goes on plain streams and on stop;
  requests work on IHateRadio and the public demo; HI's misuse of both SUB/WAVE servers raised no errors.
- **L5c Plugin page wording and status colours (HI 2026-10-28, host test).** HI found the "Who can see it" and
  "Station page" texts too dense for a new admin. Config page only (`configPage.html`), no server change.
  - Wording (HI picked option C for both; MI drafted the rest in the same style, HI approved the sweep):
    - Who can see it: "Users see all the stations or none; set it per user under Dashboard → Users → Access. The
      button adds the tile for everyone who doesn't have it, including anyone you switched off on purpose."
    - Station page: "Optional. Install File Transformation (link) to get a live panel on the Live Radio page.
      Everything else works without it." The 12.3 catalog gap and the repository URL stay in the README.
    - Tile name, "Show what's playing" and "Track cover art" help texts: same facts, 2–3 plain sentences each.
    - "Skips users whose block list names the tile" leaves the page (12.3 cannot set a block from the dashboard);
      the README keeps it.
  - Colours: a status line is a coloured head (symbol + one or two words) and a plain tail. Green ✓, yellow ⚠,
    red ✕, grey for off or disabled; the symbol carries the meaning without colour. Each colour has at least 3:1
    contrast on both #101010 and #ffffff (Jellyfin's dark and light backgrounds).

    | Line | Green ✓ | Yellow ⚠ | Red ✕ | Plain or grey |
    |---|---|---|---|---|
    | Channel status | ok | couldn't check | name taken by another channel | |
    | Poller | running, last poll ≤ half the interval | last poll over half the interval; not running yet; couldn't check | last poll longer than the interval | now playing off |
    | Station page | Active (+ version, reload note) | not checked yet; couldn't check | File Transformation not installed; registration failed | |
    | Station table | ok, listed | failing, waiting for the first poll, poller not running | not listed | disabled, not saved yet, now playing off |

    Text goes in with `textContent` only. The thresholds are named constants.
  - **Built by DA 2026-10-28** (fccf065, dd8a4aa). One fact corrected from MI's draft: a track without art shows the
    Jellywave logo, not the station's. RA: ACCEPT with 3 Suggested, 5 Nits. Fix round (b6b8f94, 72f378f): a saved
    station reads "⚠ couldn't check" when Health failed (plain "checking…" before the first answer); the poller bands
    use the server's clamped interval (5–300 s); a ⚠ suffix turns a green row head yellow; "Carry access over brings
    along everyone who had it"; the now-playing help says what updates where; HI's Station page text says "the
    tile's page" (the tile can be renamed); README and changelog say grey lines carry no symbol; Refresh status
    pressed twice gives the same text. **Accepted 2026-10-28** (MI): config page 191/191, station page 178/178,
    dotnet 901/901, 0 warnings, check-manifest only ok; the RA's mutant runner on the final page catches 40 of 43
    (survivors: a harmless null guard, and the grant and carry result texts, a gap from before L5c, picked up in L8).
    Host check: both Jellyfin themes, by eye.
- **L8 Header for plain streams (HI 2026-10-28, approved).** HI wants the station-page header to show for every
  playing station, so it becomes a normal sight. For a plain stream: logo, name, genre, notes, format line
  (codec · kbps · delivery), a website link, Stop. New station fields `Genre`, `Notes` (plain text, at most 280
  characters, any kind of station) and `Website` (http/https only; a link the browser opens in a new tab with
  `rel="noopener noreferrer"`, never fetched by the server). Check fills genre, notes and website from the stream's
  `icy-genre`, `icy-description` and `icy-url` headers; the admin can edit them. Left out (HI): listener counts,
  radio directory lookups, schedules for plain stations.

  ```text
  ┌──────────────────────────────────────────────────────────┐
  │ ┌──────┐  KEXP                                 [ ■ Stop ]│
  │ │ logo │  Eclectic · Seattle                    ← genre  │
  │ └──────┘  Listener-supported, 90.3 FM.          ← notes  │
  │           MP3 · 128 kbps · Relay       kexp.org ↗        │
  │ L6 adds:  Now: Artist – Title                            │
  └──────────────────────────────────────────────────────────┘
  ```

  - Model: `StationDefinition` gains `Genre` (at most 60 characters), `Notes` (at most 280) and `Website`, for both
    kinds; the `DataVersion` hash includes them. The media source shape does not change, so `MediaSourceSchemaDate`
    stays.
  - Website rule (new, in `StationUrls`): absolute `http`/`https`, no userinfo, at most 2048 characters; anything else
    is dropped where it is read (station still listed). A link only: the server never requests it, the browser opens
    it with `target="_blank"` and `rel="noopener noreferrer"`, and `ui.js` re-checks the scheme before setting `href`.
  - Probe: `StreamAnswer` gains `icy-genre`, `icy-description` and `icy-url`; `StationProbe` cleans them like
    `icy-name`, cuts them to the limits, and returns `Genre`, `Notes`, `Website` (an `icy-url` without a scheme gets
    `http://`, then the website rule). The editor fills empty fields from Check, like the name.
  - Config page editor: Genre and Website inputs, a Notes textarea with `maxlength` 280 and a character count; help
    text in the L5c style. The server enforces the limits again (the page is not trusted for them).
  - Item: a plain stream's item overview is its notes when set (so every client shows them in Jellyfin's own
    detail view), else today's fixed text. SUB/WAVE overviews stay the now-playing text.
  - `GET /Jellywave/Stations` lists every listed station in grid order with `Kind`; plain entries add `Genre`,
    `Notes`, `Website`, and a format line source (`Codec`, `BitrateKbps`, `Delivery`); SUB/WAVE entries add `Notes`.
    Still never a stream URL. The SUB/WAVE station routes keep refusing plain ids (404).
  - `ui.js`: `playingStation()` matches any listed station; the header draws by kind. Plain: logo (item image),
    name, genre, notes, format line, website link, Stop; no Feed polling. SUB/WAVE: as L5b, plus the notes line when
    set. Text through `textContent` only.
  - Harness: plain header shape; switching SUB/WAVE ↔ plain both ways; a `javascript:` or `data:` website never
    becomes an `href`; notes with markup shown literally; no SUB/WAVE route called for a plain station.
  - Docs: README (station fields, the header for every station), DESIGN, THREAT-MODEL (the website link: admin-set
    or from `icy-url`, shown to every user of the tile, never fetched), checklist (e) gains plain-header steps.
  - **Built by DA 2026-10-28** (28b69a1, e78656d, b016f98, 013b333). DA choices: Genre and Website show only for
    plain streams in the dialog (HI Q1); the list's `Codec` is a display label; notes served as one line. RA: FIX
    (two checklist steps would fail on a correct build; code sound, 60 hostile website values refused or
    normalised). Fix round (b7d8af1, f2f4447): non-ASCII website hosts served as punycode (look-alikes visible),
    ASCII-only page rule; a plain item's overview drops `<` and `>`; a missing or unknown `Kind` gets the plain
    header, which asks the server nothing; Android website tap step; harness pins for a cached plain header and
    trimming. The DA reported a third commit renaming one test (3f01e5f); it does not exist and the test is in
    b7d8af1 under its first name (cosmetic). **Accepted 2026-10-28** (MI): dotnet 969/969, 0 warnings, format clean,
    config page 240/240, station page 312/312, check-manifest only ok, host grep empty. Accepted note: Markdown
    links typed in notes can still render as links in Jellyfin's detail view (admin text). Host check: checklist (e)
    34–49.
- **S-b IL scan allow-list (HI 2026-10-28: after L8, before L6).** `StationRouteIlTests` today forbids named types and
  members (a deny-list) and reads only the controller, its nested compiler types and `StationList`; a plugin helper
  elsewhere could reach a station's configuration unseen (THREAT-MODEL "Limits of the IL scan" (renamed by S-b)). Add an
  allow-list: every plugin method the station controllers call (directly or through their nested types) must be on a
  list with a one-line reason, else the test fails. Before L6 because L6 changes the checkpoint itself (a route that
  accepts plain station ids for the title). No runtime change; refactors only where they shorten the list, proved by
  the existing behaviour tests. Users and admins see nothing. Docs: THREAT-MODEL row narrowed, DESIGN, CLAUDE.md rule
  line if the workflow changes.
  - **Built by DA 2026-10-28** (a02c55f, a205c34). RA: FIX (R1: listed data types were not read inside, so a property
    initializer or a getter only the serializer calls could leak another station's URL with all 988 tests green; R2:
    generic implementations skipped). Fix round (09186fb, 9a85a2e): every listed data type walked whole; open generic
    implementations dispatched; station sinks per member (`StationDefinition.get_Notes`); a static field holding a
    sink type is a sink; more reflection sinks; a plugin type handed to the framework must be a listed data type
    (found `WebUiStatus`). List: 38 data types + 23 members. The DA's first report miscounted (62); corrected.
    **Accepted 2026-10-28** (MI): no production change (`git diff f140a68..9a85a2e -- Jellyfin.Plugin.Jellywave/`
    empty), dotnet 1003/1003, 0 warnings, format clean; MI re-ran the RA's three real-code mutants (LikeStatus
    initializer, LikeSubmission initializer, FeedResponse computed getter): all three now fail the scan with the
    path named; the DA's re-run of the RA harness catches 21 of 21. CLAUDE.md rule line extended. Remaining limits
    are listed in THREAT-MODEL "Limits of the IL scan" (follow-ups: type-valued attributes such as
    `[JsonConverter]`; writes inside the three `Items` accessors).
- **L6 ICY titles for plain streams (HI 2026-10-28: build, after L8 and S-b).** Adds "Now: Artist – Title" to the L8 header.
  Sampled only for stations someone is playing (the
  sessions' now-playing item ids via `ISessionManager`), with the same bounds as other station calls.
  HI answers 2026-10-28:
  - Waiting: the line reads "Querying upstream…" from the start of playback; after a named timeout of 30 s (HI)
    with no title it reads "Delayed response: is the stream up?". A stream that sends no `icy-metaint` header carries
    no titles at all (MI, HI agreed), so it reads the shared "not supplied" text at once instead of waiting. Wording of
    that text open: HI's "Data not supplied by upstream provider" or MI's shorter "Not supplied by the station".
  - Where: the header and the playing bar, like SUB/WAVE stations. ICY carries one `StreamTitle` string (usually
    "Artist - Title"), no album field (fact). Proposed: show the string as sent in the header; in the playing bar,
    split on the first " - " into artist and title when present, else the whole string as the title.
  - Junk titles: shown as sent, no filtering (HI: the admin is responsible for the stations they add).
  - Design (MI 2026-10-28):
    - Sampler: a background loop (named interval, 15 s) finds plain stations someone is playing (`ISessionManager`
      sessions whose now-playing item is the station's live item), and for each opens its server-side stream URL
      through `StationClient`'s no-redirect client with `Icy-MetaData: 1`, reads `icy-metaint` audio bytes and one
      metadata block, then closes. Bounds: refuse `icy-metaint` over 64 KiB, read cap = metaint + 1 + 255×16 bytes,
      8 s timeout (named constants). No `icy-metaint` header, or an HLS station: state "no titles" without reading
      audio. Nobody playing: no requests. Each sample is a short extra listener on the station (README note).
    - State per station id: title (cleaned like other station text, cut to a named limit), state (`Title`,
      `NoTitles`, `Failing`), time of the last good sample. Dropped when nobody has played it for a few intervals.
    - Route: `GET /Jellywave/Title?stationId=` behind `[StationAccess]`; it resolves only an enabled, listed plain
      station (SUB/WAVE and unknown ids → 404) through the station checkpoint (a plain-station variant of
      `[StationRoute]`/`CallerStation()`, on the S-b allow-list). It reads state only and never triggers a sample.
      Never a URL.
    - `ui.js`: the plain header gets a "Now:" line. "Querying upstream…" from playback start; the title when one
      arrives; "Delayed response: is the stream up?" after 30 s (named constant) with no title or while `Failing`;
      the shared not-supplied text for `NoTitles`. The playing bar shows artist and title (split on the first
      " - ", else the whole string as title) like SUB/WAVE stations. Text through `textContent` only.
    - Shared text: one constant, "Not supplied by the station" (HI 2026-10-28: MI's polish of HI's wording stands).
  - HI Q1 (2026-10-28): the "Show what's playing" checkbox also switches title sampling off (route answers `Off`, the
    page hides the Now line); its section heading is now "Now playing".
  - **Built by DA 2026-10-28** (88f6ed7, c9d2d1e, e450326, 2b62f05, 3f10565). DA readings: the bar keeps following the
    title while a plain header sits in a cached view; `Failing` shows the delayed text at once; an over-cap
    `icy-metaint` is `NoTitles`. RA: FIX (R1: .NET drained up to 1 MB of an unread chunked or Content-Length body on
    dispose, so a sample read about 1 MB and closed 2 s late, even on the NoTitles path; R2: the playing bar froze on
    an old title once the tile view was evicted). Fix round (3f45306, 720a0b2, cfd3e11, 98e4e99, 58ddff5, 4fc9d99):
    `MaxResponseDrainSize = 0` on the station handler (only image, probe and sample leave a body unread) with real-
    socket tests; the bar keeps polling while the stream it painted plays; Q1; HLS shows the not-supplied text at
    once; concurrency, error-category and collision tests; surrogate-safe cut in `StationText.Clean`; checklist
    timings 25–30 s, (e)63–66. Direct-only findings (README Direct bullet, a client-URL test, how an unreachable Direct
    station shows) held for HI's Direct decision. **Accepted 2026-10-28** (MI): dotnet 1124/1124, 0 warnings, format
    clean, config page 242/242, station page 395/395, check-manifest only ok; MI reverted the drain fix and the five
    socket tests failed ("the station saw the close 00:00:01.99… after the sample returned"), restored; the RA's own
    eviction scenario passes ("New – Song"). RA runners after the fix round: page 27/32 + adapted variants, server
    25/33 + later variants all killed; survivors have no visible effect. Open, minor: switching titles off takes up
    to one round (15 s) to clear the page.
- **L9 Starter stations (HI 2026-10-28; idea, details open).** A fresh install ships a few stations so the tile works
  out of the box. HI's picks: the SUB/WAVE public demo, WRUW, WJCU, XCSB (successor webcast of WCSB 89.3), plus MI's
  Radio Paradise, SomaFM Groove Salad and KEXP (HI 2026-10-28), each only if its terms allow third-party players.
  Logos by `LogoUrl` from each station's own host where one exists (WJCU: its stream host's station art), downloaded
  at runtime through the L2 logo path, not bundled. Fresh installs only; a removed station is never put back (HI).
  CLAUDE.md gets one line exempting public station URLs from "no hostnames in tracked files" (HI). Empty genre,
  notes or website stay hidden in the header (HI; L8 already does this). Order: after L10 and the fresh-eyes walk.
  **HI 2026-10-29:** the list is the SUB/WAVE demo, KEXP (AAC), WRUW, WJCU and Groove Salad; XCSB and Radio Paradise
  wait on HI. XCSB checked by MI 2026-10-29: `https://xcsb.live/listen/xcsb-cleveland/xcsb160.mp3` answers (AzuraCast,
  MP3 160 kbps, `icy-metaint` 16000, so titles work); its `icy-genre` is about 280 characters, so the entry sets
  "Freeform"; description "Cleveland's Alternative to FM.". HI picked logos for WJCU, KEXP and Groove Salad on the
  host; for shipped entries MI recommends each station's own host over third-party copies (open for HI).
  **HI later 2026-10-29:** XCSB in (S1); Radio Paradise out of the starters, kept in HI's own config (S2); logos from
  the station's own site or Wikimedia Commons, not aggregators or social CDNs (S3). Research (MI subagent, re-checked
  by MI with `User-Agent: Jellywave/0.5.0.0` and no redirects, all 200 image/png):

  | Station | LogoUrl | Source | Size |
  |---|---|---|---|
  | KEXP | `https://upload.wikimedia.org/wikipedia/commons/thumb/d/dd/KEXP_logo_%28black_on_orange%29.svg/500px-KEXP_logo_%28black_on_orange%29.svg.png` (bars and name, opaque orange); own-site runner-up `https://www.kexp.org/static/assets/img/KEXP_ios_safari_152px.png` (bars only, transparent) | Commons, public domain (text logo) | 500×542 |
  | WRUW | `https://wruw.org/wp-content/uploads/fbrfg/apple-touch-icon.png` | own | 180×180 |
  | WJCU | `https://www.wjcu.org/files/2020/11/site-icon.png` ("88" mark; the `https://wjcu.org` form redirects, so `www.` exactly) | own | 128×128 |
  | Groove Salad | `https://somafm.com/logos/512/groovesalad512.png` | own | 512×512 |
  | XCSB | `https://xcsb.live/static/uploads/browser_icon/original.png` (globe) | own (its AzuraCast) | 256×256 |

  Open for HI: KEXP from Commons (MI: yes) plus a contact URL in the plugin's User-Agent, which Wikimedia's policy
  asks of bots (MI: yes, one line in L9); WJCU's "88" mark, the only square opaque WJCU image (MI: yes). The
  SUB/WAVE demo needs no logo (SUB/WAVE stations show the track cover). Full findings: MI scratchpad `logos/logos.md`.
  **HI 2026-10-29: both approved** (KEXP from Commons with a contact URL in the User-Agent; WJCU's "88" mark).
  - **Built by DA 2026-10-29** (0d5cfc6 seeding, 75fac0a User-Agent, fa71c9b Groove Salad out, 4955374 docs, and this
    PLAN commit with checklist (j) and LESSONS). Starters in `Configuration/StarterStations.cs`, fixed `N`-format GUID
    ids, `SortOrder` in list order, all enabled.
    - **Seeding rule (DA choice):** a fresh install is "no configuration file when the plugin starts".
      `Plugin`'s constructor checks `File.Exists(ConfigurationFilePath)` before the first read of `Configuration`
      (Jellyfin's `LoadConfiguration` writes a default file when there is none, and a 0.4.x install that never had a
      URL has the same values as a fresh one, so no in-file marker can tell them apart: an element missing from an old
      file reads as the constructor's default). With no file, `Plugin.StartFresh` builds the defaults, migrates them as
      before, adds the starters (`StarterStations.Seed`, which also refuses a non-empty list) and saves. With a file,
      nothing is ever seeded. No "starters seeded" marker: it would add nothing to the file check. The constructor's
      list stays empty (the XmlSerializer trap; a mutant that fills it fails 20 tests). A failed save keeps the starters
      in memory and reports `RunningOnDefaults` (as a failed default save did); the next start is fresh again. Tests on
      real-shaped files: fresh install, restart (each id once in the file), page save with all removed then two
      restarts, hand-written `<Stations />` file, one starter removed by a page save, hand-written file with one starter
      left, configured 0.4.2 file, unconfigured 0.4.x file, failed save, failed id rule. Mutant "seed every empty list
      on load" fails 3 of them.
    - **User-Agent:** `Jellywave/0.5.0.0 (+https://github.com/b3p3k0/jellywave)` (product, then the URL as a comment);
      the registration test pins the whole value. ffmpeg's relay sends Jellyfin's own User-Agent, not this one.
    - **URL check (DA 2026-10-29, curl with the new User-Agent, no redirects followed, 4–8 s):** every stream 200 with
      audio, every logo 200 `image/png`, none redirected. ffprobe on the first seconds: SUB/WAVE demo mp3 192 kbps 44.1
      kHz stereo (`https://www.getsubwave.com/stream.mp3`, HTTP/2); KEXP aac 160 kbps 44.1 kHz stereo; **WRUW mp3 128
      kbps 48 kHz** stereo (the brief gave no rate; the entry says 48000); WJCU mp3 128 kbps 44.1 kHz; Groove Salad mp3
      128 kbps 44.1 kHz; XCSB mp3 160 kbps 44.1 kHz. Logos: KEXP 500×542, WRUW 180×180, WJCU 128×128, Groove Salad
      512×512 (361 KB), XCSB 256×256, all under the 4 MB and 4096 px caps. https for the two http streams: **WJCU
      answers on `https://wjcu.jcu.edu/listen/wjcu_radio/wjcu-mp3-hi` with the same stream** (HTTP/2 200, mp3 128
      kbps, same `icy-name`); the entry keeps `http://` as approved, and WJCU's own playlist
      (`https://www.wjcu.org/streams/wjcu-mp3-hi.m3u`) names the http address. Open for HI: switch WJCU to https. WRUW
      has no https form (port 8000 TLS fails, 443 answers 404, 8443 times out).
    - **Terms check (DA 2026-10-29):**
      - SUB/WAVE demo: the project's own public demo (HI's pick); no terms found.
      - KEXP (`kexp.org/terms-and-conditions/`): personal, noncommercial use; no copying, redistribution or public
        performance of streams, no "reproduction on any computer network". No clause on third-party players; KEXP staff
        have pointed listeners to the direct stream URL for VLC, Apple Music and foobar2000 (r/KEXP threads, not an
        official page). A household Jellyfin relaying to its own users reads as personal use; flagged for HI, not
        dropped.
      - WRUW: the "legal statement" page has no stream terms; only a copyright line. Nothing forbids it.
      - WJCU: the listen page offers the direct stream addresses for other players (VLC named); no terms.
      - XCSB: an AzuraCast instance; no terms published.
      - **Groove Salad (SomaFM): forbidden, left out** (fa71c9b, revertable on its own). SomaFM's terms
        (`somafm.com/contact/tos.html`): "We can't grant permission for third-party SomaFM clients or applications, even
        noncommercial ones", content is "for individual/personal use by end users only", and "Re-streaming,
        retransmitting, or broadcasting the Content through any medium or service" needs prior written permission.
        Every Jellywave station is relayed through Jellyfin, and the card's rule is "each only if its terms allow
        third-party players". So a fresh install lists five starters. Open for HI: keep it out (DA), or ship it anyway
        (revert fa71c9b and change "five" in README, DESIGN, THREAT-MODEL and the changelog).
    - CLAUDE.md exempts public station URLs; no repo-side host-name check exists (no script, CI step or hook greps for
      hosts), so nothing else needed changing. A test checks every starter host is a public DNS name (no IP literal,
      no single label, no `.local`/`.lan`).
    - Checks: dotnet 1186/1186 (baseline 1167 + 19), 0 warnings, format clean, config page 428/428, station page
      433/433 (pages unchanged), check-manifest only ok.
    - Also for HI: a fresh install now contacts public hosts without anyone listening: the SUB/WAVE demo's feed every
      update interval (20 s by default) and each logo host once per start. README, DESIGN and THREAT-MODEL say so.
  - **Fix round (DA 2026-10-29, after review):** 0382aa6 and d117aaf.
    - **Wrong warning after a failed first save (DA choice: a separate flag).** `StartFresh` set `RunningOnDefaults`,
      so the page said the file "could not be read" and Health's text spoke of blocks on the old tile, while there was
      no file and no old tile; the folder had refused the write. Now `Plugin.RunningUnsaved` (set only by `StartFresh`;
      the two never come together, since one runs with no file and the other with one). Health sends `UnsavedWarning`
      and `ConfigurationUnsaved = true`; the page shows "⚠ Jellywave could not save its settings file. The stations are
      kept in memory until a save works (see the server log)." Why not one reworded warning: the two cases need
      different things from the admin (an unreadable file that the next page save overwrites, and old-tile blocks that
      may not hold; versus a folder that refuses writes and nothing lost), and a sentence true for both says neither.
      The tail does not say "starter stations", because a failed page save leaves the page's list in memory. The log
      line says "runs on the starter stations in memory"; `RecordCarriedNames` still holds back in both states (its
      log names which: "could not be loaded/saved"), and a successful page save clears both flags. The built note's
      "reports `RunningOnDefaults`" above is superseded.
    - **Each starter's format pinned:** `StarterStationsTests.Expected` carries codec, bitrate and sample rate per
      starter (ffprobe values above) in place of the 64–320 and 44100/48000 ranges.
    - **Docs on a failed first save:** `StartFresh`'s comment and DESIGN no longer say the next start is always fresh.
      A save that fails before the file exists (read-only folder) leaves none, so the next start is fresh again; one
      that fails midway (full disk) can leave a truncated file, since `SerializeToFile` creates it first, and the next
      start then sees a file, Jellyfin replaces it with defaults and no starters are added (never a duplicate).
    - Mutants (scratch worktree, removed): 9 of 9 killed (StartFresh sets the old flag, `RecordCarriedNames` ignores
      the new one, a page save not clearing it, Health dropping `ConfigurationUnsaved`, Health sending the load text,
      WRUW at 44100, KEXP at 128, the page ignoring the flag, the page showing a banner on the flag alone).
    - Checks: dotnet 1187/1187 (+1), 0 warnings, format clean, config page 431/431 (+3), station page 433/433,
      check-manifest only ok. Starter URLs, names and membership unchanged (WJCU https, KEXP and Groove Salad still
      open for HI).
  - **HI 2026-10-29 on the open points:** Groove Salad stays out ("kick em to the curb"); WJCU uses its https address
    (`https://wjcu.jcu.edu/listen/wjcu_radio/wjcu-mp3-hi`, checked by MI: 200 `audio/mpeg`, no redirect; changed by MI
    with the THREAT-MODEL row: WRUW is the one plain-HTTP starter); KEXP stays ("within the spirit of their station and
    its terms"); the SUB/WAVE demo polled around the clock is not acceptable ("don't want to DDOS the very thing we
    love"): card L14, before release.
  - **Code part accepted 2026-10-29** (MI: 0d5cfc6, 75fac0a, fa71c9b, 4955374, 37f3491; fix round 0382aa6, d117aaf,
    9fdfddc; WJCU https in the acceptance commit). RA: ACCEPT (no Blocker or Required; 9 of 9 mutants killed; the
    fresh-install check read against the decompiled 12.4 `BasePlugin<T>`); its two Suggested and two Nits are fixed or
    were HI decisions. Host check: checklist (j), together with L14.
- **L10 Relay only: remove Direct delivery (HI 2026-10-28, approved).** Direct was card 2's fallback in case ffmpeg
  could not relay an endless stream; Relay works everywhere HI uses it, and Direct mostly adds failure modes (mixed
  content on https, stations the client cannot reach, client-side AAC, stream URLs handed to every client) and a
  choice a new admin cannot judge. HI accepts the costs: one ffmpeg per listener (host fact 2026-10-28: Jellyfin
  re-encodes each Relay listener to AAC 256 kbps HLS, `-acodec libfdk_aac -ab 256000 -f hls`), a small re-encode
  loss, HLS start delay, server location seen by stations. Users whose admin turned off "Allow audio playback that
  requires transcoding" cannot play stations; HI: the admin's choice, honoured; README says so in one line. Default
  is HI's own hardware class; README gives the per-listener cost so small servers can plan, nothing is throttled.
  - Model: `Delivery` and `ClientStreamUrl` go from `StationDefinition` (and the legacy flat fields stay read only by
    the migration); an old config with Direct loads as Relay. Media source always `SupportsDirectPlay=false`; bump
    `MediaSourceSchemaDate`. `DataVersion` follows.
  - Config page: Delivery choice, Client stream URL field, the AAC-in-Direct warnings and their status suffixes go.
    Station header format line drops "· Relay"; `GET /Jellywave/Stations` drops `Delivery`.
  - L6 held items: the sampler uses the server URL only (one URL per station now); the client-URL test is moot.
  - Docs: README, DESIGN, THREAT-MODEL (rows about Direct, client-visible stream URLs where Relay changes them: check
    what PlaybackInfo still carries in Relay and say exactly that), checklist steps naming Direct, the 0.5.0.0
    changelog. Tests follow.
  - **Built by DA 2026-10-28** (f94abd8, 8a7e419). DA choice, accepted by MI: the 0.4.x flat `Delivery` and
    `ClientStreamUrl` fields are removed, not kept read-only (the migration does not need them), so a downgrade to
    0.4.2.0 comes back in Relay. `MediaSourceSchemaDate` 2026-10-28 19:00 UTC. Fact recorded: in Relay the station URL
    still reaches clients as the media source's `Path` in PlaybackInfo; docs say "relayed", not "hidden". RA: FIX
    (README and THREAT-MODEL still promised the downgrade keeps old settings; `verify-jellyfin.sh` could not detect
    direct play because 12.3 recomputes the flag in PlaybackInfo). Fix round (a0db4ba): the downgrade exception in
    both docs; the script reads the stored flag from `GET /Items/{id}`; page and probe text checks. **Accepted
    2026-10-28** (MI): dotnet 1120/1120, 0 warnings, format clean, config page 260/260, station page 397/397,
    check-manifest only ok, `bash -n` ok; MI confirmed on the host that `GET /Items/{id}?userId=` returns the channel
    item's `MediaSources` with `SupportsDirectPlay: false`. RA mutants 23/28 (all behaviour mutants), fix-round text
    mutants all caught. The untracked `local/HOST-TEST-0.5.0.0.md` is marked superseded by PLAN (a)–(g).
- **Host run 2026-10-29 (L5c + L8 + L6 + L10 build, dll md5 `5aa20a64…`).** HI deployed and ran the look list in the
  web client: all pass. HI's partner used the tile as an end user with no help and found it intuitive. MI: the
  installed dll equals the staged one; `verify-jellyfin.sh` as testA: 41 ok, 0 FAIL (the host serves under the base
  path `/emby`, so `JELLYFIN_URL` must include it). The script printed Jellyfin's `TranscodingUrl`, which carries the
  caller's key as `ApiKey=`; fixed in 0d5b68d (redacted; the rerun shows the key 0 times). Log from the restart
  (13:10 UTC) to 14:00: Jellywave WRN only for two KEXP AAC title timeouts, each followed by "works again" within 40 s,
  and one WJCU logo URL refused for its `#fragment` (by design; HI pasted a URL without it next). Two `FFmpeg exited
  with code 251` ERR lines are WRUW and WJCU refusing one connection ("Error opening input: Input/output error");
  the retry a minute later played. Not Jellywave's.
  - **D3, server side (MI, `local/jwtest.py d3`):** with testB's "Allow audio playback that requires transcoding" off,
    PlaybackInfo for WRUW answers 200 with no `ErrorCode`, one source, no `TranscodingUrl`, `SupportsDirectPlay`
    false: nothing the client can play, and no error code that says why. On again: a `TranscodingUrl`. The flag was
    restored (read back: true). What the web client shows the user in that state is HI's optional look. Feeds E.
  - Commit 194e895 (Notes box) says "configPage 262"; the harness count was 261.
- **L11 First-run page fixes and Stop instead of Pause (HI 2026-10-29: G1, B, C and P approved; A, D, E later).**
  From the fresh-eyes walk (2026-10-28) and HI's host notes. HI also fixed the Notes box size (194e895, done: full
  width, 5 lines, not resizable).
  - **G1, six bugs:**
    1. Save fails silently: a failed `updatePluginConfiguration` leaves the spinner and no message. Hide the spinner,
       show the reason in red next to Save.
    2. Unsaved stations are lost without a word when the admin leaves. Warn on tab close or reload (`beforeunload`)
       while the list differs from the last save. Check in the 12.3 bundle whether in-app navigation can be
       stopped; if not, say so here and keep the "Unsaved changes" note.
    3. A plain stream with a blank name is called "SUB/WAVE" ("SUB/WAVE — Live"). Name it after its stream URL's
       host (ASCII form) wherever a name shows; SUB/WAVE stations keep "SUB/WAVE". The dialog warns on a blank name.
    4. The tile's default description says "from a SUB/WAVE station". Make it "Live internet radio."
    5. "Save the station settings first." shows forever under the grant button. Drop the default text; the server's
       "Nothing done: …" answer stays.
    6. The Add box opens with "⚠ Hint: No stream URL yet." Show no hints until the URL field is typed in or Check
       is pressed.
  - **B, File Transformation status:** "not installed" in grey (`jwMuted`), with "not in its catalog for Jellyfin 12.3
    yet; see README" beside it. Red stays for "registration failed". The intro keeps HI's option C text.
  - **C, plain words** (walk §3, with MI's choices HI approved): "tile", not "channel", on the page (once, in Who can
    see it: "Jellyfin calls this a channel"); column "Type" with values "Stream" / "SUB/WAVE"; after Check, a plain
    stream reads "✓ Radio stream." and SUB/WAVE is named only when found; "listed" → "in the tile", "not listed" →
    "left out of the tile: <reason>"; "poller" / "poll interval" → "now playing updates" / "Update every (seconds)";
    "Refresh status" → "Reload status"; "Carry access over" → "Give them access to the renamed tile", and its
    "blocked N" → "blocks copied N"; "waiting for the first poll" in grey, not yellow; "Add" / "Apply" in the dialog →
    "Add to list" / "Apply to list". Server strings (probe lines, status reasons) and their tests follow; README and
    DESIGN where they quote the page. Moving fields is A, not C.
  - **P, Stop instead of Pause (HI 2026-10-29).** A live stream has no "resume where you left off": today a pause
    keeps ffmpeg running and a resume plays from the buffer, behind live, and the header's Stop does not change on
    pause. HI: only start and stop for stations. `ui.js`, while a listed station's item (either kind) is the current
    item:
    1. The playing bar shows jellyfin-web's own Stop button and hides its play/pause buttons. Fact (12.3 bundle on the
       host, `14245.*.chunk.js` and `.css`): the bar already has `.stopButton`, wired to `playbackManager.stop`, and
       hides it at widths ≤ 80em (`.nowPlayingBarCenter .stopButton{display:none!important}`); `.playPauseButton`
       exists twice (centre and right). A class on `document.body` while a station is current, CSS injected once by
       `ui.js`; the class goes on stop or any other item.
    2. A pause of a station item from anywhere (space bar, media keys, lock screen or notification, the Now Playing
       page, a cast receiver) becomes a stop: on `pause`, after a short settle (named constant), if the same listed
       station is still the current item and the player is paused, call `playbackManager.stop()`. A pause fired
       while switching to another item does nothing.
    3. Every other item (music, video) keeps Jellyfin's pause, untouched.
    4. Limits, plainly in README: only where `ui.js` runs (web client and the Android app, with File Transformation);
       other apps keep their pause; Stop removes the bar, so playing again is a tap on the station; anything that
       pauses the player (an incoming call taking audio focus) now stops the station.
    - DESIGN "The page": paused no longer lasts for a station. Harness: bar class on start, switch, stop and a
      non-station item; pause → stop; pause during a switch → no stop; a non-station pause untouched.
  - **HI 2026-10-29 (via MI):** P accepted with its side effects (Stop removes the bar; anything that pauses the player
    stops a station; other apps keep their pause). README says why in plain words next to the limits.
  - **Built by DA 2026-10-29** (465c313, a482de4, bb7a27e, cace4e8 and this PLAN commit). G1, B and C in the first three
    commits, P alone in cace4e8 so it can be dropped on its own.
    - Facts (jellyfin-web 12.3.0, commit c06cb17b27, read on the test host 2026-10-29, read-only; snippets in DESIGN):
      the view manager ignores the result of its before-change hook and `viewbeforehide` (`main.jellyfin.bundle.js`),
      so **in-app navigation cannot be stopped**; the "Unsaved changes" note stays and only a tab close or reload asks.
      jellyfin-web dispatches `viewhide`/`pagehide` and `viewdestroy` on the plugin page, so the guard asks only while
      the page is shown. A failed `updatePluginConfiguration` rejects with the fetch response;
      `processPluginConfigurationUpdateResult` only hides the spinner and shows the "Settings saved" toast.
      `playbackManager` never passes a player's `pause` on (`Te(){Qe(this,"pause")}`, a progress report): P listens on
      the current player and rebinds on `playerchange`, as the bar does (the script's older manager-level `pause`,
      `unpause`, `statechange` listeners never fire in 12.3; left as they were). A switch stops the old item and
      clears its stream info before the media element's `pause` arrives, so that pause finds no station.
      `paused(player)` is the media element's `paused`. Media keys, lock screen and Media Session pause go through
      `playbackManager.pause(player)`, so they arrive as the player's `pause`.
    - **DA choices where the card was silent or its premise did not hold:**
      1. Below 56em jellyfin-web hides the whole centre group (`@media (max-width:56em){.nowPlayingBarCenter{display:
         none!important}}`), which holds the bar's only Stop; the right play/pause is then the only control (phones,
         the Android app). Hiding it alone would leave no control. There ui.js adds one Stop of its own to the right
         group (jellyfin-web's button classes and stop icon, calling `playbackManager.stop()`), shown only by the
         injected CSS below 56em while a station is current. Above 56em jellyfin-web's own Stop shows, as the card says.
         **Open for HI:** keep this, or leave jellyfin-web's play/pause there and rely on pause → stop.
      2. The timeout line still names SUB/WAVE ("Radio stream. The SUB/WAVE check did not finish in time; press Check
         again if this is a SUB/WAVE station."), since a SUB/WAVE station may be behind it; the plain line is "Radio
         stream." and nothing says "added".
      3. "Type: stream (checked)." / "Type: SUB/WAVE station (detected)." in the dialog, matching the Type column;
         "waiting for the first update", "now playing updates not running yet" (the poll words, everywhere on the page).
         The server's collision and missing-tile texts say "tile": "another plugin's tile has the same name", "the tile
         does not exist yet (add a station and save)". "Who can see it" reads "…Access, where Jellyfin calls the tile a
         channel." The not-installed line names the plugin: "Not installed: File Transformation is not in its catalog
         for Jellyfin 12.3 yet; see README."
      4. The guard watches the station list only (as the card says): an open dialog, the tile name and the now-playing
         fields are not counted. A failed load also hides the spinner and **blocks Save** (the page would otherwise post
         its empty list over the server's stations); that path had the same silent spinner.
      5. Hints: an Add box shows none until the URL field is typed in or Check is pressed (typing a name alone does not
         count); an Edit box shows them at once. The blank-name warning is a hint.
      6. A blank plain stream whose URL has no http(s) host is "Unnamed stream" (it is not listed then). An IPv6 host
         keeps its brackets, as the browser's `URL.hostname`. A blank plain stream's item is renamed once on the first
         poll after the upgrade ("SUB/WAVE — Live" → "<host> — Live"; the user-data key moves once, as for any rename).
         `MediaSourceSchemaDate` is not bumped: the source's shape is unchanged (its name follows on the next save).
         The station list route now shows that host as the name; `StationRouteAllowList` names the two getters with
         the reason (the item name shows it to every user anyway), THREAT-MODEL says so.
    - Mutants (scratchpad `l11/mut-page.py`, `l11/mut-ui.py`; server ones by hand): page 14 of 14 caught (hints on an
      untouched box, a name counting as touched, the guard asking with nothing changed or after in-app navigation or
      keeping its listener after `viewdestroy`, `_unsaved` counted, spinner left or no reason after a failed save, Save
      after a failed load, the old name fallback, no blank-name hint, red not-installed, the stale grant text, yellow
      waiting). Server 5 of 5 (blank stream "SUB/WAVE", IPv6 without brackets, `Host` not `IdnHost`, any scheme, the
      old description). ui.js 13 of 15: no settle, no paused check, no same-station check, no same-player check, no
      station check after the settle, nothing captured at the pause, class never removed, play/pause not hidden, old
      player kept, not bound on playbackstart or on playerchange, our Stop at every width, our Stop doing nothing;
      survivors are equivalent: dropping the "station at pause time" guard (the after-settle check still needs the
      same station id) and an extra stop path on the manager's `pause` (12.3 never fires it).
    - Checks: dotnet 1137/1137, 0 warnings, format clean, configPage 317, stationPage 429, `node --check` ok,
      check-manifest only ok. Checklist: (e)4 and (e)5 now say pause stops; new section (h) 1–19; older steps keep
      their numbers with the page's new words.
    - Not checked on a host: what the web client shows for the tile's description (unit-tested only); the narrow-width
      Stop's look on a phone (checklist (h)14).
  - **Fix round (RA FIX, checklist only; code sound)** (3e4676b, e3af0e4 and this PLAN commit; e3af0e4 holds the P
    parts, so P with its fixes stays droppable as cace4e8 + e3af0e4):
    - R1: (h)11 unticks Enabled instead of pressing ↓ (a move never marks a row "not saved yet") and expects the red
      line in full.
    - S1: `load()` clears the save note, so neither "Your changes are still on this page" nor "Couldn't load the
      settings…" outlives a later good load. S2: (b)6 quotes the current rename notice. S3: (h)19 adds a station
      switch on cast; DESIGN records that the cast player fires `pause` on every play-state change
      (`chromecastPlayer-plugin.*.chunk.js`, `playstatechange` → `trigger(e,"pause",[n])`; `paused()` is
      `lastPlayerData.PlayState.IsPaused`), so on cast only the settle and the paused check protect a switch (read on
      the test host, read-only, 2026-10-29).
    - S4: harness checks for the RA's three survivors: the bar class goes on a list 403, a feed 403 and a sign-out while
      a station plays; a failed reload after a good one blocks Save; an `ftp://` URL is "Unnamed stream" on the page as
      on the server.
    - S5: DESIGN's Health paragraph and the `StationListRouteTests` comment say a blank plain stream's `Name` is its
      host; route tests for Health and `GET /Jellywave/Stations` with a port, a path and a query assert the host alone.
      THREAT-MODEL says "the host only, never its port, path or query".
    - S6: (h)14 narrows the window to about 1000 px (jellyfin-web's Stop in the centre) and about 800 px (our Stop at
      the right), no play/pause either time; (h)18 runs right after (h)17, so the switch to music also clears the class.
    - N1: README says "after about a second" (settle 500 ms plus the audio player's fade on stop) and "Do not seek" no
      longer calls pause fine. N2 (MI): an Add box warns about a blank name only after a Check answered or failed, or
      after Add to list was pressed; an Edit box warns at once (DA reading: the card's warning still shows for a stored
      blank name; MI named the Add box). (h)3 no longer expects the name hint. N3: `StationSources.RefusedKind` reads
      "its type is not SUB/WAVE or Stream". N4: (h)4 says "Replace the Stream URL with …".
    - N5: `MediaSourceSchemaDate` stays as it is: the media source's shape did not change, only its `Name` text for a
      blank plain stream ("<host> live stream" instead of "SUB/WAVE live stream"), which follows on the next save; a
      bump would make every client re-read every item's sources for a label no client shows for a one-source item.
    - HI: README explains the phone Stop (what, why, how) and broadens the side effects (Android audio-focus loss: a
      call, a navigation prompt, a voice assistant; a SyncPlay group pause; pausing a remote-controlled device).
    - Mutants for the new checks: page 7 of 7 (a good load keeps the note, a failed reload keeps Save, page host for
      any scheme, blank-name hint at once in an Add box, and Add to list, a failed Check or an answered Check not
      turning it on); ui.js 1 of 1 (`forgetPage` leaves the class); server 3 of 3 against the two route tests alone (a
      blank name becomes the whole URL, host with port, host and path).
    - Checks: dotnet 1140/1140, 0 warnings, format clean, configPage 328, stationPage 433, `node --check` ok,
      check-manifest only ok.
- **L11 accepted 2026-10-29** (MI, after the fix round 3e4676b, e3af0e4, 6ff4e52; build 465c313, a482de4, bb7a27e,
  cace4e8, fd1212f): dotnet 1140/1140, 0 warnings, format clean, config page 328/328, station page 433/433, `node
  --check` ok, check-manifest only ok, no host names in tracked files. RA (first pass FIX: checklist (h)11 could not
  pass; suggested items S1–S6, nits N1–N5) checked the P facts against the 12.3 bundle itself; its mutants 24/28, the
  four survivors closed in the fix round (11/11 new negative checks caught). P can be dropped alone as cace4e8 +
  e3af0e4. Host check: checklist (h).
- **L11, HI 2026-10-29 on the DA's phone choice:** approved: below 56em ui.js adds its own Stop where jellyfin-web
  hides the bar's centre group. HI asks that README documents the override, the reason (jellyfin-web hides its own
  Stop on narrow screens, and hiding pause too would leave the bar with no control) and briefly how it works (shown
  only while a station is current, calls Jellyfin's own stop). Goes into the L11 fix round. D3 look stays on HI's list.
- **L12 Placeholder pictures for stations without a logo (HI 2026-10-29: later, low priority).** About 6 radio or
  music themed pictures instead of the one Jellywave logo, so a tile of logo-less stations is not one repeated image.
  Open when picked up: drawn for the project (SVG, MIT) or CC0, never copied; chosen per station by a stable hash of
  its id, so a station keeps its picture; the tile itself keeps the Jellywave logo.
- **L13 First-run page layout, plain-stream health, who can see it (HI 2026-10-29: A, D and E approved, after L11).**
  From the fresh-eyes walk §5 and §6 (items 1, 4, 5). Sketch for HI before it is built:

  ```text
  Jellywave
  Internet radio in a "Live Radio" tile in My Media.

  ┌ banners, each only when it applies ─────────────────────────────────────────┐
  │ ✕ Another plugin's tile is also called "Live Radio". Jellywave does nothing   │  red
  │   until you rename it (Advanced → Tile name).                                 │
  │ ⚠ Tile renamed: 3 users could see "Radio" but not "Live Radio" yet.           │  yellow
  │   [Give them access to the renamed tile]                                      │
  │ ⚠ Jellywave could not read its settings file and runs on defaults.            │  yellow
  └───────────────────────────────────────────────────────────────────────────────┘

  STATIONS
    Order  Name             Type      On  Status
    ↑ ↓    SUB/WAVE demo    SUB/WAVE  ☑   ✓ on air · 12 s ago            [Edit] [✕]
    ↑ ↓    WRUW 91.1 FM     Stream    ☑   ✓ answered 4 min ago            [Edit] [✕]   ← D
    ↑ ↓    WJCU             Stream    ☑   ⚠ no answer 2 h ago             [Edit] [✕]   ← D
    ↑ ↓    Radio France     Stream    ☑   ✓ in the tile                   [Edit] [✕]   (HLS: not sampled)
    ↑ ↓    KEXP             Stream    ☐   off                             [Edit] [✕]
    [+ Add station]  [Reload status]          Unsaved changes. [Save]   ← shown only while unsaved

  WHO CAN SEE IT
    Everyone who can see the tile sees every station (Jellyfin calls the tile a channel).
    Per user: Dashboard → Users → Access.
    4 of 6 users can see the tile.                                                ← E
    [Add the tile to every user now]
    ⚠ testB can see the tile but can't play stations: "Allow audio playback that   ← E, only when > 0
      requires transcoding" is off for them.

  LIVE PANEL
    Optional. Install File Transformation to get a live panel on the tile's page.
    ○ Not installed: not in its catalog for Jellyfin 12.3 yet; see README.         grey (L11)

  ▸ Advanced                                                                      closed by default
      Tile name  [Live Radio]   Renaming makes a new tile; see README first.
      ☑ Show what's playing   ☑ SUB/WAVE: track cover as the station picture
      Update every [20] seconds   Now playing updates: ✓ running

  [ Save ]
  ```

  - **A, layout:** stations first; tile name, Show what's playing, cover art, update interval and the updates line
    under one closed "Advanced" fold; the name collision, rename notice and settings-file warning become banners at
    the top, shown only when they apply (no green "ok" line for the tile); a second Save beside the station table,
    shown only while there are unsaved changes. Same fields, same defaults, same ids where tests and docs use them.
  - **D, plain-stream health:** the server already keeps, per plain station, the last title read's outcome and time
    (`StationTitles`). `GET /Jellywave/Health` (admin) reports it; the row shows "✓ answered N min ago" or "⚠ no
    answer N min ago", else "✓ in the tile". Known only while someone played the station with Show what's playing on,
    since the last restart; HLS streams are not sampled. No extra listener, nothing stored on disk.
  - **E, who can see it:** an admin-only read: how many users can see the tile (the same visibility rule the grant
    button uses), and the names of users who can see it but have "Allow audio playback that requires transcoding"
    off (L10: they cannot play any station; D3 2026-10-29: Jellyfin then answers with no error code). Read-only; it
    changes no policy. The DA verifies where 12.3's dashboard shows that setting and names the place in the text.
  - Out of scope: L9, L12, the Add box's "optional details" fold (walk §5) unless HI asks.
  - **HI 2026-10-29: sketch approved as drawn.** Built after L11 is accepted (same page file).
  - **Built by DA 2026-10-29** (56b03ed A, 7fc13f1 D, 94505fd E, and this PLAN commit with checklist (i) and LESSONS).
    - **A:** an intro line naming the saved tile ("Internet radio in a "Live Radio" tile in My Media."), then the
      banners, Stations, Who can see it, Station page, a closed `<details id="advancedSettings">` "Advanced" (tile name;
      an h3 "Now playing" with Show what's playing, cover art, Update every, the "Now playing updates" line), Save.
      Same ids, fields and defaults. Banners (`.jwBanner`, a coloured left edge): `channelStatus` red "✕ Tile name taken:
      <server text>. Rename the tile under Advanced → Tile name; nothing is written or granted until then." (no line at
      all when the tile is fine), `renameNotice` yellow, `carrySkipped` plain, `configWarning` yellow "⚠ Running on
      default settings: …". A second Save (`btnSaveNear`, "Unsaved changes. [Save]") beside Add station / Reload status,
      shown while the station list differs from the last load; it runs the same save, and the save note shows beside both.
    - **D:** the card's premise held only in part. `StationTitles` kept the state, but a failed sample stored no time
      (`LastGoodUtc` is set for `Title`/`NoTitles` only) and `Expire` drops an entry 45 s after the last listener
      (`IdleIntervalsKept` 3 × 15 s), so "⚠ no answer 2 h ago" could not come from it. Added a second record per station:
      `RecordRead(id, url, answered, now)` from the sampler on every request it makes (never for HLS), `LastRead(id, url)`
      and `KeepReads` (each round: drops stations no longer listed or whose URL changed). The record holds a hash of the
      URL (`AbsoluteUri.GetHashCode(Ordinal)`, per process), never the URL, so an earlier URL's outcome never shows, even
      before the next round. Health: `StationHealth.LastReadAnswered` / `LastReadUtc` for listed plain streams only.
      Page: "✓ answered 4 min ago" green, "⚠ no answer 2 h ago" yellow, else "✓ in the tile" (logo suffix kept).
    - **E:** `AccessGranter.ViewersAsync` (reuses the channel lookup and `Classify` with the bridge's earlier ids; no
      save, no gate, no log line; `ChannelIdAsync` got a `log` flag) behind `GET /Jellywave/Viewers` on the admin
      controller; answer `TileViewers` (`Users`, `Visible`, `NoAudioTranscoding`, `Error`). No new constructor
      dependency (the granter already holds the user store; `PolicyWriterDependencyTests` unchanged). The S-b
      allow-list is not involved: it covers the station page controller and `StationList`, and nothing new is called
      from them.
    - **Host fact (jellyfin-web 12.3.0 on the test host, read-only, 2026-10-29):** the setting is on the user's
      **Profile** tab, section **Media playback**. `users-edit.6b717b133ba6e3c018c5.chunk.js`: tabs
      `translate("Profile")`, `"TabAccess"`, `"TabParentalControl"`, `"HeaderPassword"`, and
      `b==y.v.Profile&&(0,r.jsx)(M,{userDto:m})`; inside `M` (offsets 13720–33994; the label at 28326):
      `h.Ay.translate("HeaderPlayback")` … `className:"chkEnableAudioPlaybackTranscoding",title:"OptionAllowAudioPlaybackTranscoding"`.
      `en-us-json.*.chunk.js`: `"HeaderPlayback":"Media playback"`, `"OptionAllowAudioPlaybackTranscoding":"Allow audio
      playback that requires transcoding"`. The page says "Dashboard → Users → <user> → Profile → Media playback".
    - **DA choices where the card was silent:**
      1. The sketch shows the columns "On" and no "Format", and the heading "LIVE PANEL"; the card's text lists neither
         change, so the columns stay Order, Name, Type, Format, Enabled, Status, Actions and the heading stays "Station
         page". Help texts stay as HI approved them in L5c/L11 (the sketch's short texts read as abbreviations). **Open
         for HI.**
      2. A name collision also opens Advanced (the banner points there). A failed Health request shows a yellow "⚠
         Couldn't check the tile" banner. The rename notice turns green with the outcome after its button.
      3. The second Save follows the station list only (the leave guard's rule): a tile name or now-playing change
         alone shows no second Save; the bottom Save covers them.
      4. A read outcome survives "Show what's playing" off and a quiet stream (it is history, with its time); a restart,
         a URL change or leaving the list forgets it. `NoTitles` counts as answered (the stream answered, sends no title).
      5. E counts every user, disabled ones included (the grant pass does too); names sorted, case ignored; the page
         reads Viewers on show and after the grant button, the rename button and Save (a rename makes a new tile), not
         on Reload status. "1 of 1 user"; several names read "2 users (a, b) can see the tile, but …".
    - **Tests:** `TitleSamplerTests` (+6: answered/not and when, a stream that stops answering, HLS and unplayed
      unread, outlives the title state and titles off, URL change and leaving the list, the record holds no string or
      Uri), `HealthRouteTests` (+3: only listed plain streams report, an earlier URL's read is not reported, Health with
      reads serialised carries no URL part), `TileViewersTests` (6: the rule over eight standings, equals a grant pass's
      "already visible", nobody named, errors, the route and its fixed error, the attributes), `AdminRoutePipelineTests`
      (7, real MVC pipeline: a signed-in non-admin gets 403 from Viewers and from Health, CarryAccess GET/POST,
      GrantAccess and Probe, with the user store never read; not signed in is not 200; an admin gets the count).
      Page harness: L13 A (4 scenarios), D (1), E (2).
    - **Mutants** (scratchpad `l13/mut_page.py`, `l13/mut_server.py`): page A 17 of 17 (each banner always shown or
      never cleared, the green tile line back, Advanced open always / never on a collision / open by default, the tile
      name outside the fold, the second Save always / never / doing nothing, its note always shown, the note only at the
      bottom, the rename banner staying yellow, the intro naming the field); D server 13 of 13 (LastRead ignoring the URL,
      KeepReads keeping all / ignoring a URL change / never called, titles off or Expire clearing reads, HLS recorded, a
      failure counted as answered or not recorded, Health not reporting / reporting a SUB/WAVE read / carrying a URL, the
      record keeping the URL); D page 5 of 5; E server 10 of 10 (Viewers anonymous or on a weaker method policy, names
      inverted / everyone / users who cannot see it, the rule without the bridge, unsorted, counting on a collision, a
      save during the read, exception text in the error); against `AdminRoutePipelineTests` alone 2 of 3 ([AllowAnonymous]
      on Viewers, the class without `RequiresElevation`); the "survivor", `[AllowAnonymous][Authorize]` on the method,
      was not equivalent as first reported: it did not build (analyzer ASP0026 is an error here), and the runner then
      counted only `error CS` as a build failure (corrected in the fix round); E page 7 of 7.
    - **Checks:** dotnet 1162/1162, 0 warnings, format clean, configPage 397, stationPage 433, `node --check` ui.js ok,
      check-manifest only ok. Checklist: new (i) 1–17; moved steps (b)6, (c)1, (e)34, (e)43, (e)64, (f)4, (h)1, (h)13 and
      (g) follow the new page.
    - Not checked on a host: the page's look (banner edge colours in both themes, the fold on a phone); (i) covers it.
  - **Fix round (RA FIX: R1, R2; suggested S1–S5, nits N1–N5; MI decisions 2026-10-29)** (8ae2695 code and tests,
    and this docs commit). RA: security sound.
    - **R1 (option a):** behaviour kept. `GET /Jellywave/Viewers`, like the grant and the rename button, goes through
      `LibraryChannelLocator.LocateAsync`, which creates every registered channel's entity
      (`GetChannelsInternalAsync`) when the tile's entity does not exist yet and a station is listed: the same work a My
      Media load does; it writes no user. Said in DESIGN, the THREAT-MODEL L13 row ("reads policies, writes no policy;
      may create the tile's channel entity as a My Media load does"), README and the locator comment (which named the
      grant button only; CarryAccess already used it).
    - **R2, S4:** checklist (i) rewritten (1–23): the transcoding steps are a [me] API step (policy write, Viewers
      shows the name), a human step (the exact yellow text; the box unticked under Profile → Media playback), a [me]
      restore and a human reload; the count is [me] from `GET /Users` policies against `Viewers` (N and M reported),
      then a human step reads the line.
    - **S1:** the form is `novalidate`; `save()` checks `NowPlayingPollSeconds.checkValidity()` first and, when it
      fails, opens Advanced and calls `reportValidity()`, for both Saves (the bottom one too: inside a closed fold the
      browser's own bubble could not show).
    - **S2 (MI decision; HI may override):** a read ends `Answered` (a success status and its headers, even when the
      title then does not arrive in the budget or the stream breaks off: `SampleIcyTitleAsync` calls back right after
      `EnsureSuccessStatusCode`), `Moved` (a redirect) or `NoAnswer` (everything else, an unexpected failure included).
      Health: `LastReadOutcome` ("Answered", "Moved", "NoAnswer") replaces `LastReadAnswered`. Row texts "✓ answered N
      min ago" (green), "↪ moved N min ago: Edit → Check → Use it to read titles" (yellow), "⚠ no answer N min ago"
      (yellow). Still no URL stored (the record holds the enum, the hash and the time).
    - **S3:** tests for the RA survivors: a capturing logger shows `ViewersAsync` logs nothing (tile found, missing,
      collision; a control shows the grant pass does log); an unexpected exception is `NoAnswer` (with its Error line);
      a user deleted since the snapshot is not counted; a cancelled Viewers request throws instead of answering 200;
      the second Save keeps the failed-load guard; a good load clears both notes.
    - **Sketch alignment (MI):** table header "On", no Format column (the format stays in Edit → Advanced; the table
      keeps its scrolling wrapper on a phone); "Live panel" with HI's option C text and link; "Who can see it" keeps
      HI's L5c option C text; banners in the sketch's words ("✕ Another plugin's tile is also called "<saved name>".
      Jellywave does nothing until you rename it (Advanced → Tile name).", "⚠ Tile renamed: N users could see "<old>"
      but not "<new>" yet.", "⚠ Jellywave could not read its settings file and runs on defaults."); tile name help
      "Shown in My Media. Renaming makes a new tile; see README first."; the transcoding line "⚠ <names> can see the
      tile but can't play stations: "Allow audio playback that requires transcoding" is off for them (Dashboard → Users
      → <user> → Profile → Media playback)." ("each user" in the path when there are several); SUB/WAVE row "✓ on air
      · 12 s ago"; the h3 "Now playing" stays. The collision banner names the saved tile, not the field being typed in.
    - **E (MI):** disabled users (`PermissionKind.IsDisabled`) are left out entirely: "N of M active users can see the
      tile.", never named; the equality test compares with a grant pass over the enabled users. At most ten names,
      then "and N more". The second Save keeps following the station list only (MI agrees).
    - **Nits:** N1 `ago()` has days from 48 h on ("3 d ago"), and tests for 47 h and a future time ("0 s ago"); N2
      Viewers reads are numbered, only the newest answer is shown; N3 the "finish the dialog" note goes when the dialog
      closes (Cancel, Escape, Apply), and a good load clears both notes; a failed-save note is not about the dialog
      and stays. N4, N5: done by the MI decisions above.
    - **DA reading:** with the sketch's banner the server's `ChannelStatus` text no longer shows on the page (the
      banner says the same in the page's words); `DefaultsWarning` likewise (its detail is in the server log and the
      README). `IcySample` is unchanged; only the sampler's outcome uses the new callback.
    - **Mutants** (now in a scratch worktree, `l13/wt`; `l13/mut_server.py` takes `MUT_ROOT` and counts any `: error`
      as a build failure, which the first pass missed): server 12 of 12 for the new checks (redirect as no answer,
      headers ignored, callback before the status check, callback never called, unexpected failure answered, Health
      outcome fixed, Viewers logging the skip lines or the names, disabled users counted, disabled users named but not
      counted, a deleted user counted, cancellation answered as an error); page 20 of 20 (S1 four ways, A1, A3, N3 two
      ways, N2, N1 two ways, moved three ways, the cap two ways, "active", the collision naming the field, the Format
      column back, "ok" back); the one first-pass page survivor (the collision naming the field) got its check.
    - **Checks:** dotnet 1167/1167, 0 warnings, format clean, configPage 428, stationPage 433, `node --check` ui.js ok,
      check-manifest only ok. Checklist: (i) 1–23; (b)6, (c)1, (c)6, (c)7, (c)11, (e)55, (h)1, (h)11, (h)13 follow.
- **L13 accepted 2026-10-29** (MI, after the fix round 8ae2695, 224e19a; build 56b03ed, 7fc13f1, 94505fd, 0367845):
  dotnet 1167/1167, 0 warnings, format clean, config page 428/428, station page 433/433, `node --check` ok,
  check-manifest only ok, no host names in tracked files, working tree clean (the DA's first server mutant runner
  edited the repo in place; it now runs in a worktree). RA: FIX (docs said Viewers only reads, though it can create
  the tile's channel entity as a My Media load does; checklist (i)14 mixed API and browser), security sound. MI
  decisions in the fix round, HI agreed 2026-10-29: a redirecting stream reads "↪ moved", a 2xx with headers counts
  as answered; the sketch's banner and tile-name texts, HI's option C texts kept; the second Save follows the station
  list only; disabled users left out of E ("active users"), names capped at 10. Host check: checklist (i), on 12.4
  after U1.
- **Host results 2026-10-29 (HI, web, L11 build):** look list passed, including step 10 redone through the browser's
  media controls (pause → stop). Step 12 skipped (no music library on the host). D3 look done: passed.
- **U1 Stack refresh: Jellyfin 12.4, File Transformation 3.0.3.0, SUB/WAVE 1.28.0 (HI 2026-10-29).** HI: keep in step
  with what Jellywave talks to before releasing. **v0.5.0.0 supports Jellyfin 12.4 and later only** (HI: keep it small;
  people should stay up to date).
  - Facts (MI 2026-10-29): Jellyfin 12.4 (2026-10-26) changelog is one line, ".NET 10.0.14"; jellyfin-web 12.4
    changes only `package.json`/`package-lock.json` (`@jellyfin/sdk` 1.0.2), so the web code our facts come from is
    unchanged. `Jellyfin.Controller`/`Jellyfin.Model` 12.4.0 are on NuGet. File Transformation's catalog moved to
    `https://www.iamparadox.dev/jellyfin/plugins/manifest.json` and offers 3.0.3.0 for 12.0–12.4; HI installed it,
    and the station page registered with "3.0.3.0" (15:28 UTC). SUB/WAVE 1.27.0 changed skins only; 1.28.0 adds
    `GET /api/lyrics/current` (absent in 1.26.0, which explains the IHateRadio lyrics panel showing a sentence),
    `state.lyrics.enabled`, `offsetMs`, `synced`, and an LRCLIB fallback; no requests on/off flag yet. Reference
    clone of SUB/WAVE: `~/DEV/subwave` (MI's machine, untracked).
  - HI: upgrade the Jellyfin test host to 12.4 and the SUB/WAVE test host to 1.28.0. The installed 0.5.0.0 (targetAbi 12.3) keeps
    loading on 12.4 meanwhile.
  - DA: packages 12.4.0 in both csproj files, `targetAbi` 12.4.0.0, README support matrix and "requires 12.4"; the
    File Transformation texts (README matrix and station-page section, plugin page grey line) say it is in its
    catalog at the new URL; DESIGN/README facts tagged "12.3" re-dated to 12.4 where the 12.4 diff shows them unchanged
    (server: release notes; web: the compare above), checked again on the host bundle after the upgrade; the 0.5.0.0
    changelog in `build.yaml`.
  - Lyrics, step 1 (test, no code): after the SUB/WAVE test host runs 1.28.0, MI plays IHateRadio and reads `/Jellywave/Lyrics`
    through the plugin and `/api/lyrics/current` at the station; compare the plugin's parser with `docs/api.md`
    "Lyrics" in the SUB/WAVE clone (tag v1.28.0). Step 2 (extras such as synced highlighting, hiding the panel from
    `state.lyrics.enabled`) is explored with HI first.
    - **Step 1 done (MI 2026-10-29, SUB/WAVE 1.28.0):** the station's `/api/lyrics/current` gave synced lyrics, 45 lines,
      for the on-air track; `/Jellywave/Lyrics` as testB gave the same 45 lines, `Status` ok, `Synced` true; a song id
      not among the recent plays gave `Status` ended ("That track is no longer on air."). The parser matches the 1.28.0
      contract (403 off, 409 ended, 503 and others use the station's `error`; `songId`, `synced`, `lines`, `pending`).
      The old panel failure was 1.26.0 lacking the route, not our code.
    - **Step 2 dropped (HI 2026-10-29):** "just display them; simplicity wins". No synced highlighting; the panel keeps
      showing plain lines. `startMs`/`offsetMs` stay unread.
  - Host: staged build (U1 + L13) on 12.4; `verify-jellyfin.sh`; log scan; checklist (i) and a short U1 look.
  - **Code part built by DA 2026-10-29** (f91c0a0, 918e966 and this PLAN commit). Restore pulls 12.4.0 for every
    `Jellyfin.*` package; the built assembly references `MediaBrowser.Common`/`Model`/`Controller`, `Jellyfin.Data`
    and `Jellyfin.Database.Implementations` 12.4.0.0; dist `meta.json` carries `targetAbi` 12.4.0.0. The 0.5.0.0
    changelog opens "Requires Jellyfin 12.4 (0.4.x stays available for 12.3 servers)." README: matrix row "Jellyfin
    12.4.x supported", "12.3 and older not supported" (0.5.0.0 is built for 12.4; the catalog keeps 0.4.2.0 for 12.3
    and 0.3.4.0 for 12.0–12.2), status and intro lines; File Transformation row and station-page section: 3.0.x in
    its catalog for 12.0–12.4 at the new repository, "tested with 3.0.3.0" (server version left to MI). Plugin page:
    the grey line reads "Not installed: add the repository https://www.iamparadox.dev/jellyfin/plugins/manifest.json
    (Dashboard → Plugins → Repositories), install File Transformation, then restart." The URL is plain text, and the
    line keeps the grey "off" style without a mark (L11: off has no symbol; the brief's "○" was not added). The L5c
    harness check that the URL left the page now checks it shows only in that line. Re-dating rule (DESIGN header): a
    present-tense server fact says 12.4 or "12.3 and 12.4"; provenance stays 12.3 (12.3 source line numbers, bundle
    reads on the 12.3 host, "checked on a 12.3 server", "seen in 12.3's log"). Code comments that cite the 12.3
    source keep "12.3" (provenance). `check-manifest.sh`'s comment follows the target; no test, script or workflow
    pinned 12.3 otherwise (`AbiTests` reads `build.yaml`, the workflows read `meta.json`).
    - Mutants (scratch worktree): 5 of 5 killed (the old grey text, the URL also in the help text, "and restart"
      for "then restart", `targetAbi` back to 12.3.0.0 with 12.4 packages: `AbiTests` and check-manifest, one test
      csproj package back to 12.3.0: check-manifest).
    - Checks: dotnet 1167/1167, 0 warnings, format clean, configPage 428, stationPage 433, `node --check` ui.js ok,
      check-manifest only ok, `build.sh` meta.json `targetAbi` 12.4.0.0.
    - For MI on the 12.4 host: the staged build loads (log: no assembly load error); README "tested with 3.0.3.0"
      gets "on Jellyfin 12.4" after the look. Web bundle facts now dated 12.4 from the jellyfin-web compare alone, to
      re-check on the host bundle: Add User leaves "Enable access to all channels" unticked (DESIGN "Who can see the
      channel"), `viewshow` bubbles on every view, jellyfin-apiclient 1.11.0 is bundled, `playbackManager` never
      re-fires a player's `pause`, the view manager ignores the `viewbeforehide` result, `GET /Items` default
      `SortBy=IsFolder,SortName`. Bundle details that may move with a rebuild even without code changes (chunk
      `14245`, module `74550`, commit c06cb17b27) stay dated 12.3.0; update them if the 12.4 bundle names differ.
      Server facts re-dated from the release notes (policy save ignores `BlockedChannels`, ffmpeg `-i "<url>"`
      quoting, `EnableCaseSensitiveItemIds` default): a re-test is optional. The grey "Not installed" line shows
      only without File Transformation; the harness covers it, a host look would need FT disabled.
- **U1 accepted 2026-10-29, with L13's host part** (MI; code f91c0a0, 918e966, c66622c, be8cfe8, c8309a3). Test hosts:
  Jellyfin 12.4.0, File Transformation 3.0.3.0, SUB/WAVE 1.28.0 (HI upgraded both).
  - MI on the 12.4 host: the bar Stop is still hidden by `@media (max-width:80em)` and wired to `playbackManager.stop`
    in chunk `14245`; module `74550` is still in the main bundle; Add User still sets `chkEnableAllChannels` unchecked;
    only the players (and SyncPlay) trigger `pause`, the main bundle does not. Bundle names unchanged, so no ui.js
    change for 12.4.
  - Staged build (U1 + L13, `targetAbi` 12.4.0.0, dll sha256 636a31f6…) deployed by HI; meta status Active.
    `verify-jellyfin.sh`: 45 ok, 0 FAIL, ALL CHECKS PASSED, no key in the output. Log since the 0.5.0.0 load on 12.4.0:
    0 Jellywave ERR/WRN; the one WRN is Jellyfin's own WebRootPath line.
  - Checklist (i): MI [me] steps 10 (Health: no `http`, port, `.mp3` or host), 16 (N=45 M=46, Viewers equal), 18
    (`NoAudioTranscoding` ["testB"]), 20 (flag restored, list empty), 22 (testA 403, no names) pass. HI: every other
    step passed (HI 2026-10-29: "anything I didn't mention is a pass"), lyrics panel shows lines on 1.28.0.
- **L14 Ask a SUB/WAVE station only while someone listens (HI 2026-10-29, before release).** L9 ships the public
  SUB/WAVE demo to every fresh install, and the poller asks every SUB/WAVE station every update interval (20 s) whether
  or not anyone listens. HI: "only when someone is listening … i don't want to DDOS the very thing we love!"
  - HI decisions: Q1 (a) the rule covers **every** SUB/WAVE station, not only the demo; Q2 (a) an idle station's item
    goes back to the station's own look (its picture and tagline text, as a plain stream shows), not a stale track.
  - Sketch (approved):

    ```
     every update interval, for each SUB/WAVE station
         ├─ someone is playing it? ───────────────────────────yes──► ask the station (as today)
         ├─ someone had its station page open in the last 60 s? ─yes──► ask the station
         └─ otherwise: nothing leaves the server
     Play pressed on an idle station ──► ask at once (the track shows within a second or two)
    ```
  - MI decisions: the plugin page's Status shows an idle SUB/WAVE station as idle (nobody listening), never an old
    "on air" time; Reload status (admin) asks each SUB/WAVE station once, on purpose. Reuse L6's `IPlayingItems`.
  - **Built by DA 2026-10-29** (7c749f4 server, 9fa3365 pages, then the bounded concurrent-check test and this docs
    commit). `StationDemand` (singleton) decides once per poll: needed = a session plays the item (paused counts) or
    did ≤ `ListenerGrace` (30 s) ago, or a station route was called for it ≤ `PageWindow` (60 s) ago (recorded by
    `StationRouteAttribute` after the gate and the lookup, receipt routes included, the title route not). Idle: no
    request; state cleared, default text and embedded logo (the failure look), `PollerHealth.Idle` (`IdleSinceUtc`), no
    failure, no warning; one Information line per in-use/idle change. Wake: a one-slot `SemaphoreSlim` released by
    `PageOpened` or `IPlayingItems.PlaybackStarted` (from `ISessionManager.PlaybackStart`, external id = station id) for
    an idle station; the loop waits `WakeGap` (1 s) after every poll, then on the semaphore for the rest of the
    interval; a woken poll asks only stations not asked since they became needed and keeps the interval's timing. A
    failed item lookup keeps the last played set. First poll after startup follows the rule. `POST
    /Jellywave/Health/Check` (`StationCheck`): every listed SUB/WAVE feed once, ≤ 4 at once, one check at a time, not
    with now playing off; `PollerHealth.Checked` keeps the idle mark. Page: "idle: nobody listening" (grey), after a
    check "✓ answered when checked N s ago · idle: nobody listening" / "⚠ failing: … when checked …"; Reload status posts
    the check. ui.js asks an unheard feed again after 2 s (≤ 3 per station) and the schedule again when the first heard
    feed arrives. `verify-jellyfin.sh`: header note only (it checks no poller field and wakes nothing). Host check:
    checklist (k); (j)3 and (j)4 updated.
  - Checks: 1213/1213 .NET tests (baseline 1187), configPage 450 (431), stationPage 443 (433), build 0 warnings,
    `dotnet format` clean, check-manifest only "ok", `node --check` ui.js, `bash -n` verify-jellyfin.sh.
  - Mutants (DA, scratch worktree of 9fa3365, removed after): 34 of 34 caught: M01–M25 (C#: page window off or 61 s,
    no grace, wake never/always, start never wakes, demand keeps unlisted, woken poll asks all, every station needed,
    idle as failure, idle keeps state or asked mark, lookup failure idles, success/check idle mark, check records
    nothing / no guard / runs with now playing off, route skips check, Health without `IdleSinceUtc`, filter or receipt
    marks no page, session view drops the id or never unsubscribes, not registered), P01–P04 (page) and S01–S05
    (ui.js). M17 first hung the run; the test now bounds the second check (5 s) and fails cleanly.
  - **Fix round after RA (DA, 2026-10-29;** 7097ffd server, a518e13 pages, 2074425 docs, eb579b5 and 1f3d4b0 tests,
    then this PLAN commit). RA: FIX.
    - R1: once the poll interval is up the loop runs the regular poll without looking at the wake semaphore
      (`left > 0 && WaitForWakeAsync(left)`); before, a stream of wakes (a start and a stop of an idle station's item
      every second) kept running woken polls, which skip stations already asked, so a played station's track froze. A
      woken poll with nobody new to ask now fetches and writes nothing. First tests of `LoopAsync`: a started host
      (`DispatchProxy`) and a clock ten times as fast; a start and stop of "b" every 20 ms while "a" plays → "a" asked
      ≥ 3 times within 10 s (before the fix: once); a wake asks the woken station within seconds and not the others.
      THREAT-MODEL's "cannot make the server poll faster" names the test.
    - R2 (HI Q2): an idle SUB/WAVE station's item shows its own text, `JellywaveChannel.IdleOverview`: its `Tagline`
      (the editor's Description), else its `Notes`, else the default text, `<` and `>` dropped and cut at 280 like a
      plain stream's notes; the picture stays the embedded logo (no logo field; MI: matches the approved sketch). A
      failing station keeps the default text. README, DESIGN "Idle look", changelog, checklist (k)4.
    - S1: no code change needed. ui.js asks a feed only while a station plays in that client (the header follows
      playback; a pause becomes a stop, L11 P; the timer stops with the playback), so a background tab never polls
      silently near the 60 s edge, and its playing session keeps the station needed on its own (an audible tab is not
      slowed either). `PageWindow` stays 60 s (HI's number). stationPage scenario: a hidden tab asks nothing while
      nothing plays, keeps the bar's feed while a station plays, and nothing after the stop.
    - S2: verified as far as the 12.4 packages allow: `SessionManager` is not in them, so `OnPlaybackStart` cannot be
      decompiled; `PlaybackProgressEventArgs` carries `Users` and `Session` (`UserId`, `AdditionalUsers`) and no access
      decision, and `/Sessions/Playing` takes an item id. Fixed rather than documented: `StationAccess.SessionUserGate`
      (the station routes' gate without the claim rules) filters the sessions' playing items and the playback starts
      (`IPlayingItems.PlayingItemIds(Func<Guid,bool>)`, `PlaybackStartedEventArgs.UserIds`); a failing gate wakes
      nothing and keeps the last lookup; the title sampler still reads every session. DI builds `StationDemand` with
      `StationAccess` (registration test). THREAT-MODEL's L14 row names the playback-report path and the bounded rate.
    - S3: `StationCheck.Cooldown` (10 s) from the end of the last check; a press then asks nothing and the page shows
      the health as it stands (doc and behaviour now agree, nit 1). Checklist (k)6.
    - Nits: only a regular poll records `LastFetchSeconds` and ends a slow streak; a regular poll that asked nothing
      clears it (`PollerHealth.NothingAsked`), and the page line then says "No SUB/WAVE station is in use …, so none is
      asked" when every listed SUB/WAVE station is idle. Top-level Health has `IdleSinceUtc`. With no channel of ours
      (missing or a collision) nothing of ours plays, so the last played set is dropped at once; a failing lookup keeps
      it at most `LookupFailureHold` (2 min). `reloadStatus` re-enables the button on failure too (the harness records
      a rejection the page leaves unhandled instead of ending the run). None skipped.
    - Not done here: the channel's own item creation still applies the default text to a new idle SUB/WAVE item; the
      next poll writes the station's own text (≤ one interval).
    - Checklist (k): 4 (own text), 6 (two presses in 10 s ask once), new 15 (a blocked user's playback report wakes
      nothing) and 16 (a start/stop stream does not stop the regular poll).
    - Checks: 1230/1230 .NET tests (baseline 1213), configPage 456 (450), stationPage 446 (443), build 0 warnings,
      `dotnet format` clean, `node --check` ui.js, check-manifest only "ok".
    - Mutants (scratch worktree, foreground, reset before each, removed after): M01–M24 (C#: R1 reverted, woken poll
      writes, idle default text, notes before tagline, tagline not stripped or not cut, playing items or starts not
      gated, gate without visibility, earlier-tile block or collision, session view without added or reported users,
      no cooldown, 9 s, from the start, woken poll records the time, nothing-asked keeps it, top level without
      `IdleSinceUtc`, collision keeps last played, failing lookup kept for good, gate failure escapes the event, DI
      without the gate, failing station shows the tagline), P01–P05 (page: button back on success only, all-idle never,
      on any idle, with no SUB/WAVE station, counting left-out stations) and S01 (ui.js timer never stops): 30 of 30
      caught. First runs: M16 and P05 survived and got tighter tests; M02 and M08 first read as caught only because the
      runner missed a build failure (`if (false)` is a warning, so an error) and tested the previous mutant's binaries.
  - **Fix round 2 after RA (DA, 2026-10-29;** 470e415 F1, 2760316 F2, d4a16f9 F3/F4/F6, 55e6472 flaky tests, then this
    PLAN commit). RA: FIX.
    - F1: a woken poll with nobody to ask returned before `StationIdle`, although `NeededStations` had already moved a
      station whose grace ended to idle; it stayed in `_asked`, kept its old track and was not woken when played again
      (up to one interval late). The early return now applies only when no asked station went idle in this poll (the
      RA's first option, so "a woken poll with nothing to ask fetches and writes nothing" stays true otherwise). Test:
      play a, poll, stop, 31 s, start/stop b, woken poll (a idle text, idle mark), play a, woken poll (asked twice).
    - F2: the loop tests wait up to 30 s (the wake test 20 s, under its 30 s real interval) and print the elapsed time;
      `WakeGap` stays fixed (a shorter test gap thins the start reports per gap that the R1 check relies on). R1
      reverted: caught 3 of 3, each at the 30 s deadline.
    - F3: `TitleSampler` applies the tile gate (`StationAccess.SessionUserGate(Channel)` on the sessions, after its
      "anything playing?" fast path); DI passes `StationAccess` (registration test). Test: a blocked user's playback
      samples nothing, a failing gate samples nothing. DESIGN "Sampler", THREAT-MODEL L6 row and the L6 accepted risk.
    - F4: THREAT-MODEL item-description bullet names the idle SUB/WAVE overview (Tagline or Notes, 280, `<` `>`
      dropped, Markdown remark).
    - F5: checklist (k)16 reworded (the stop straight after each start; S1 still asked is the check; a demo "is in use"
      line is fine). New (k)17 (F1 on the host) and (k)18 (F3: a blocked user's report leaves the stream's
      `LastReadUtc` unchanged).
    - F6: done. `StationDemand.PlayingItemIds(Channel)` takes the poll's channel and walks the sessions once (the gate
      built at the first playing session); `StationAccess.SessionUserGate(Channel)` is the gate for a resolved channel.
      A failing channel lookup still idles every station when nothing plays at all (`StationDemand.AnythingPlays`), as
      before. Test: a failing channel lookup holds while something plays and idles once nothing does.
    - Flaky tests (out of the RA's scope): `IcySampleSocketTests.A_sample_closes_at_once_after_its_block` asserted the
      station sent its whole body before the close; reproduced pinned to one core (`taskset -c 0`, 1 in 8 full runs:
      49152 of 81569 sent). Test timing assumption, no product change: it now asserts the body exceeds the read cap and
      builds the client before the clock. `StationListRouteTests.A_blank_plain_stream_is_named_by_its_host_alone`
      banned "8443" in JSON holding random item ids (a Guid can contain it); not reproduced (about 1 in 1000 runs: four Guids), found
      by reading. Ports are now banned with their colon there, in `Never_carries_a_url` and the feed test ("7700"), and
      in two `HealthRouteTests` (timestamps' fractions).
    - Checks: 1234/1234 .NET tests (baseline 1230), build 0 warnings, `dotnet format` clean, check-manifest only "ok";
      pages untouched. Repeated runs: 3 normal, 12 on two cores, 10 on one core and 10 on two cores with two CPU burners
      after the fixes, all green.
    - Mutants (scratch worktree, foreground, reset before each, removed after): M01 F1 reverted, M02 any idle station
      blocks the early return, M03–M05 R1 reverted (3 runs), M06 sampler without the gate, M07 a failing sampler lookup
      samples every stream, M08 channel gate without the earlier-tile block, M10 demand's poll without the gate, M11/M12
      failing channel lookup holds always / idles always, M13 the station client drains (socket tests), M14 a blank
      stream named with its port: 13 of 13 caught. M09 (the parameterless gate ignoring `Collides`) survived and is
      equivalent: a collision's `Channel` is always null. Not covered: a DI constructor that takes `StationAccess` but
      ignores it (the registration test checks parameter types only; as for `StationDemand` in round 1).
  - **Code part accepted 2026-10-29** (MI; build 7c749f4, 9fa3365, d7c5379, 4c8fe49; fix round 7097ffd, a518e13, 2074425,
    eb579b5, 1f3d4b0, 5992d63; fix round 2 470e415, 2760316, d4a16f9, 55e6472, 42b3863). RA first pass FIX (R1 a wake
    stream stopped the regular poll, R2 idle text not the station's own; S1–S3, nits), second pass FIX (F1 an empty
    woken poll left a station marked asked; F2–F6), both fixed. MI: dotnet 1234/1234 on 3 runs and 2 runs pinned to two
    cores, 0 warnings, format clean, config page 456, station page 446, check-manifest only ok, no host names in the
    changed files; read the F1 and gate diffs. Accepted gap: the registration tests check constructor parameter types
    only. L9 fresh-install part of checklist (j) 1–8 and 10 done by MI on a throwaway Jellyfin 12.4 container
    (`jellyfin/jellyfin:12.4.20261026-211555`, empty config, removed afterwards): "New configuration with 5 starter
    station(s)" once; five stations in order, WJCU on https, ids unique, schema 1; all five items got their picture;
    PlaybackInfo 200 and relayed audio for all five (WRUW 48 kHz); the demo idle from the start, Reload status
    answered and kept it idle; KEXP removed stayed removed after a restart; an emptied list stayed empty over two
    restarts (one WRN: "No station is enabled", expected). Host check on the test host: (j)9 and (k).
  - **Host check passed 2026-10-30** (build sha256 1c293536…, deployed by HI 02:01 UTC on Jellyfin 12.4.0):
    `verify-jellyfin.sh` 45 ok, ALL CHECKS PASSED, no key printed; 0 Jellywave WRN/ERR since the load. HI: (j)9 (list
    unchanged, no starters) and the web look steps of (k) passed; Android (k)13–14 deferred to the end-of-phase Android
    pass (HI). MI, counting the Jellywave User-Agent in S1's own access log and reading the Jellyfin log:
    - Idle: 0 requests to S1 before HI's play and after it went idle; "is idle" about 75 s after the stop (page window
      plus one tick), with the logo and S1's own Description as the item text. While playing: now-playing and state every
      20 s (6 each in about 2 min) plus the page's cover, like, avatar and schedule reads.
    - (k)5/6: two Reload status presses 3 s apart sent one `/api/now-playing`; S1 stayed idle.
    - (k)15/18: testC (blocked from the tile) reported playback of S1 for 30 s, then of WRUW for 60 s: no request to S1, no
      "is in use" line, WRUW's `LastReadUtc` unchanged.
    - (k)16: testA played S1 from one session while a second session sent 104 start/stop pairs of the demo in 120 s: S1
      was asked every 20 s throughout (02:09:05 … 02:10:47); the demo went in use and idle a few times (allowed).
    - (k)17: S1 went idle by a regular poll before the demo's report (the 45 s wait covered grace and a tick), so the
      F1 path itself was not hit on the host (the unit test reproduces it); playing S1 again logged "is in use" and S1
      got `/api/now-playing` within 1 s of the report.
    - (k)12: with "Show what's playing" off, Reload status sent 0 requests; the setting was restored.
  - **L9 and L14 accepted 2026-10-30** (MI). Open before release: the Android pass (HI, with Chromecast).
- **L7 Docs + release candidate.** (Also: the 0.5.0.0 changelog names the ffmpeg input quoting fix, and THREAT-MODEL
  **Accepted 2026-10-27** (1092ba3, 6739970, 4134238, 1df7b60, 88b02f5, 37006e8; fix round bc87e41, 2be4404, 5c3ba64,
  5f1dbec; README wrap and LESSONS in f536545). RA: first pass CHANGES REQUIRED (the scheduled task is "Refresh
  Channels", not "Refresh Internet Channels", in every admin-facing place; F1: a station list with only plain streams
  could not create the channel for the admin buttons; the `Items` rule blocked reads only; two checklist steps not doable
  and the grant button never pressed), second pass ACCEPT. 901/901 .NET tests, configPage 68, stationPage 131. Every
  expected text in checklist (b)6–(b)10 verified against the page and the code. Phase 4 is code-complete; v0.5.0.0 waits
  for the HI host test and the Hard Stop review of L1 and L5.
  moves it from "Known issues" to Controls.) README (Live Radio, adding stations, one gate for the tile, upgrade keeps
  the rename and the "Carry access over" button), DESIGN, THREAT-MODEL (plain-stream URLs, logos, the
  one-gate limit, query disclosure, the rename bridge replacing the "name-derived id" accepted risk),
  `docs/dev/PLAN.md`, LESSONS. Release v0.5.0.0; the changelog says the tile is renamed and the
  admin should press "Carry access over". **Built by DA 2026-10-27** (1092ba3, 6739970, 4134238, 1df7b60, 88b02f5 and
  this PLAN commit); not tagged: HI host-tests the branch first. 891/891 .NET tests, configPage 68 checks, stationPage
  131 checks.
  - **Code items (L7 notes):**
    - S-a (L5 review): `StationRouteIlTests` forbids `HttpContext.Items` (`get_Items` on `HttpContext` or on
      `IItemsFeature`) in the controller except in `CallerId`, `CallerStation` and `CallerChannel`, and only in the
      controller's own methods of those names (a helper type's `CallerId` is no exemption). Control bypasses
      `ItemsWrite`, `ItemsRead`, `ItemsFeature` and `Helper.CallerId`; the control test asserts that the three accessors
      do read `Items`, so the rule cannot pass by matching nothing.
    - L3 carried, page: editing a SUB/WAVE station, changing its URL and pressing Apply without a Check now gives the
      SUB/WAVE warning ("This is a SUB/WAVE station, and its new URL has not been checked … now playing, covers, likes and
      requests stop for it"), not the generic plain-stream one. One confirming press covers both (it sets both confirm
      flags); a new Check or a typed URL takes the confirmation back. A new station keeps the generic warning.
    - L3 carried, harness: scenarios "L7 a SUB/WAVE station whose URL changed without a check" and "K4 a confirmed
      downgrade, then Check again, then one press" (and a re-check that finds SUB/WAVE applies with one press). Against
      the page before this card the L7 scenario fails 3 checks.
  - **Mutations (DA, fresh worktree of 4134238; specs in the session scratchpad `tools/`):**
    - `l7-da.json`, run with `mut-filter.py` and `FILTER='FullyQualifiedName~StationRouteIlTests|FullyQualifiedName~StationRouteCoverageTests'`
      (the rule tests alone): the RA's RX5 (from `l5-ra3b.json`: `StationList` hands out a station as `object`, the
      controller writes it into `Items`, then `CallerStation`) and five own bypasses, I01 (Feed copies an object from
      `Items` into the station slot), I02 (Lyrics through `IItemsFeature`), I03 (Schedule inside a lambda), I04
      (RequestStatus through a private helper) and I05 (Avatar reads the user id itself): **6 of 6 caught**. Control: the
      same six against d00f71d (before S-a) all pass the rule tests (0 of 6). Full suite with `mut.py`: 6 of 6 caught.
    - `l7-da-page.py` (page mutants against the committed harness): Q01 (the old rule: SUB/WAVE warning only after a
      check found a plain stream), K4 (a new check keeps the confirmed downgrade), Q03 (three presses), Q04 (generic
      text), Q05 (a typed URL keeps the confirmation), Q06 (no downgrade warning), Q07 (a re-check that finds SUB/WAVE
      still warns), Q08 (the warning applies at once), Q09 (the SUB/WAVE warning for a new station): **9 of 9 caught**.
      First run 7 of 9: Q05 and Q09 survived and were killed by tighter checks (4134238).
  - **Docs (L7 notes):** README rewritten for Live Radio (what it is, install, upgrade from 0.4.x with a before/after
    table for "Carry access over", adding stations, supported streams with the "no secrets in stream URLs" rule, who can
    see it, now playing and logos, the station page with the picker, limits); every "Fetch from station" mention gone.
    THREAT-MODEL rewritten for 0.5.0.0 (assets, boundaries, controls L1–L5 and L7, accepted risks, the quoting issue
    moved to Controls with a one-line Known issues entry). DESIGN consistency pass (single-station wording, the finder's
    lazy channel list, Health fields, the Items rule, the downgrade warning, Android force-close after an update).
    SECURITY scope and button names. `build.yaml` changelog, overview and description (one line each, no embedded double
    quotes). CLAUDE.md: four rules. `scripts/verify-jellyfin.sh`: its missing-item wording still said a renamed station
    keeps its old name, which is no longer true since L3 (comment and FAIL text only).
  - **Finding F1** (found by DA, fixed in the fix round): `LibraryChannelLocator.LocateAsync` created a missing channel
    entity only when `StationUrls.TryGetBaseUrl(config)` succeeded, which reads the primary SUB/WAVE station. With only
    plain streams, "Add the tile to every user now" and "Carry access over" answered "the channel does not exist yet
    (set the station URL and save)" until a My Media load created the entity.
  - **Fix round after RA (DA, L7):** the RA found the code items sound (15 of 15 mutants caught) and asked for:
    - Task name: Jellyfin's task is "Refresh Channels" (category "Internet Channels", key `RefreshInternetChannels`,
      default trigger every 24 hours; 12.3 `RefreshChannelsScheduledTask.cs` and en-US `TaskRefreshChannels`). Fixed in
      README, the configuration page, the changelog, DESIGN and this checklist; older PLAN sections keep the old words
      with a note.
    - F1 fixed: the locator creates the entity when any station is listed (`StationSources.Publish(config).Count == 0`
      is the "nothing to create" test). `MissingChannelError` reads "the channel does not exist yet (add a station and
      save)", the log line "(is a station listed?)". Health's primary-station texts (finding 9) now say "no SUB/WAVE
      station URL configured" and "the SUB/WAVE station URL is set but not allowed: …", so a list of plain streams
      reads correctly; the field names stay. Tests: plain streams only (created once), only a refused `.pls` or a
      disabled station (`Missing`, nothing created), a refused SUB/WAVE base URL next to a listed plain stream
      (created), the error text, the Health text for plain streams only.
    - S-a also blocks writes: `set_Items` on `HttpContext` and `IItemsFeature`, and any token, generic argument or
      constructor of `IItemsFeature`/`ItemsFeature`, outside the accessors; an accessor counts only by name, declared on
      the controller, parameterless and returning a value. New controls `ItemsReplace`, `ItemsFeatureSet`,
      `ItemsFeatureNew` and the overload `CallerStation(object)`.
    - Checklist: (a)1 and (a)2 swapped; C's block set through a full policy GET/POST; D has "Enable access to all
      channels"; a fifth user E and a grant-button step (b)9; (b)8 is the immediate second press with the exact text;
      (e)7 notes the request id for (e)8.
    - README: the block-list warning (saving the Access page clears the carried block too); "blocked by Jellyfin on
      every route" hedged to what the code guarantees (`Channel.IsVisible` refuses; host step (b)7 checks the raw API);
      the cover-art label; wrapping. DESIGN: the same hedge. THREAT-MODEL: timeouts per call kind, logos 8 s + 8 s, the
      S-b row also covers a helper or an accessor that writes `Items`, a new accepted risk "Station load driven through
      the server", the receipts' 30-minute lifetime. Page: "has not been checked successfully".
    - Mutations (fresh worktree of 2be4404): the RA's `ra-l7.json` with `FILTER=FullyQualifiedName~StationRouteIlTests`
      (rule tests alone): R1 (Items replaced), R2 (an overload named `CallerStation` writes Items) and R4 (a new item
      feature) caught; R3 (a helper type outside the controller) and R5 (an accessor that also writes) survive, as
      agreed: they are the THREAT-MODEL S-b row. `l7-da.json` again: 6 of 6 caught by the IL tests alone. `l7-da2.json`
      (full suite): F1a (the old condition), F1b (always create), F1c (create whenever any station exists, listed or
      not), F1d (the old error text) and H1 (the old Health text): 5 of 5 caught. 901 .NET tests.

### HI host checklist for v0.5.0.0

One pass, top to bottom, on the test host. It merges the L3 and L5 checklists and the L0 client matrix. ★ = repeat
in the Android app. Placeholders: `<jellyfin-host>` (the Jellyfin server), `<station-host>` (the SUB/WAVE stations).
Write down a result for every step id; section (g) says what to send back.

You need: Jellyfin 12.3 running Jellywave 0.4.2.0 with its SUB/WAVE station; a second SUB/WAVE station; File
Transformation installed; public test streams (an http MP3, an https MP3, an AAC stream, an HLS `.m3u8`, a `.pls`
playlist; the L0 list in "Cards" names some); the Android app; a Chromecast if one is available; the branch build
(`scripts/build.sh`, `dist/Jellywave_0.5.0.0.zip`).

**(a) Before deploying**

1. Write down the old tile's name and channel id (`GET /Channels` as an admin).
2. On 0.4.2.0, prepare five non-admin users and write down which is which:
   - **A**: "Enable access to all channels" ticked.
   - **B**: that unticked, the old tile ticked by name (its id in `EnabledChannels`).
   - **C**: blocked from the old tile. Jellyfin 12.3 cannot set a block: jellyfin-web has no screen for block lists,
     and the policy API ignores `BlockedChannels` (host test 2026-10-28: the POST answers 204 and stores nothing).
     Write the stored preference instead, with Jellyfin stopped and the database backed up: the `Preferences` row
     with the user's id and `Kind = 1` (`PreferenceKind.BlockedChannels`) gets the old channel id (dashed form) as
     its `Value`. The test host used an untracked helper script for this.
   - **D**: "Enable access to all channels" ticked; used for the favourite check in (c) and (f).
   - **E**: neither "Enable access to all channels" nor the old tile; used for the grant button in (b)9.
3. ★ In the Android app, signed in as A, open the old tile once, so the app holds 0.4.2.0's `index.html` and `ui.js`.
   Do not force-close the app until step (b)3.
4. Deploy 0.5.0.0 (the zip, or the plugin repository) and restart Jellyfin. The server log shows one Information line
   "Configuration moved to Live Radio (schema 1): 1 station(s); the tile <old name> is now Live Radio. …" and no ERR
   from Jellywave.
5. As admin in the web client, add the second SUB/WAVE station at once (Dashboard → Plugins → Jellywave → + Add
   station: paste its base URL, Check, Add to list, Save). Its full checks follow in (c)7.
6. ★ **Old-page check (L5 step 1), before any force-close.** In the Android app, signed in as A, open the "Live Radio"
   tile **with nothing playing**. The old script shows a header (with Play) even when nothing plays; the new script
   (card L5b) shows none until a SUB/WAVE station plays. That is how to tell the old one ran. Its header shows the
   primary (migrated) station, because it sends no station id. Caveat: the server answers the current `ui.js` whatever
   `v` the page asks for, so this only shows something while the app still holds the old `index.html` and the old
   `ui.js` is still in the WebView's HTTP cache (a day at most); no header means the new script ran, and the check says
   nothing then.

**(b) Upgrade from 0.4.2.0**

1. The tile is now "Live Radio". As A (web), it shows at once (`EnableAllChannels`).
2. The migrated station shows as "<old name> — Live", plays (through Jellyfin, also if 0.4.2.0 had it on Direct), and
   gets its track covers and now-playing text.
3. ★ Force-close the Android app and open it again; from here on it runs the new script.
4. As C (blocked from the old tile): no "Live Radio" tile. `GET /Jellywave/Feed` answers 403, and so does
   `/Jellywave/Feed?stationId=nope` (the gate answers before the station id is looked at). Record what Jellyfin's own
   `GET /Items?ParentId=<new channel id>` returns for C now (the accepted raw-API window: expected to list the items).
5. As B (granted by name): no "Live Radio" tile yet.
6. As admin, the plugin page shows at the top, in a yellow notice, "⚠ Tile renamed: N users could see "<old name>" but
   not "Live Radio" yet." (card L13 fix round: the sketch's text).
   Press **Give them access to the renamed tile**: "Done: carried over …, blocks copied …, already current …, not affected …". Write the counts
   down.
7. As B: the tile shows now. As C: still no tile, `Feed` still 403, and record `GET /Items?ParentId=<new channel id>`
   again (expected: refused or empty, since C's block list now names the new id).
8. Press **Give them access to the renamed tile** again at once, without reloading: "Done: carried over 0, blocks copied 0, already current 0,
   not affected 0." Then reload the page: the notice is gone.
9. Under "Who can see it", press **Add the tile to every user now**: "Done: granted ≥1, already visible …, blocked by a
   block list ≥1." (E is granted; C counts as blocked.) As E: the tile shows now. As C: still no tile.
10. Run Dashboard → Scheduled Tasks → **Refresh Channels** (under "Internet Channels"; it also runs by itself once a
    day): the old tile is gone for every user.

**(c) Configuration page** (Dashboard → Plugins → Jellywave, web client, as admin; ★ steps again in the Android app)

1. The page opens with its sections in order (card L13): Stations, Who can see it, Live panel, then a closed
   "Advanced" fold (tile name and now playing), then Save. The
   migrated station shows as `<old name> · SUB/WAVE`, On ticked, status "✓ on air · … ago". The table has no Delivery
   column (L10) and no Format column (L13 fix round; Edit → Advanced shows the format).
2. ★ + Add station: paste an **http** MP3 URL, Check: ✓ Audio line, ✓ "Radio stream." (L11), the name
   filled from `icy-name`. Add to list, Save: the row's status turns "in the tile · …logo…", and the tile shows the new item.
3. Add an **https** MP3 stream the same way (needed for (d)).
4. Add station with a `.pls` URL: "Playlist read → …" then the audio line; the saved station's URL is the entry.
5. Add station with an AAC URL: the Audio line says AAC and there is no ⚠ line. The dialog has no Delivery choice, and
   Advanced holds the format fields only (no client stream URL). Add to list, Save: the row shows "✓ in the tile · …" with no
   warning (L10).
6. Add station with an HLS (`.m3u8`) URL: ⚠ "HLS: codec assumed AAC …"; Add to list, Save; Edit it: Advanced shows
   Container HLS (L13: the table has no format column). Cancel.
7. Edit the second SUB/WAVE station from (a)5 and press Check: ✓ "SUB/WAVE detected", name and description filled.
   After Save it polls (status "✓ on air · …") and gets covers.
8. Add station with an `http://` URL that redirects to `https://`: ↪ line with "Use it"; pressing it fills the field;
   Check again succeeds. Cancel.
9. Paste `http://user:pw@host/x.mp3`, then a URL with a `#fragment`: "URL not allowed: …", nothing requested. Paste an
   unreachable host: "not reachable: …" within about 8 s.
10. Add station with an unreachable URL and press Add to list without a successful Check: the first press warns, the second
    adds it as a plain stream. Save. (This throwaway station is used in steps 11 and 12.)
11. Untick On for the throwaway station, Save: status "disabled"; the item leaves the tile.
12. Remove the throwaway station: the row asks "Remove X?"; Keep restores it; Remove then Save deletes it from the
    list (its item goes after a "Refresh Channels" run or the next listing).
13. ★ Keyboard only: Tab to ↑/↓, Enter moves the row and focus stays on the moved row's arrow; Save; the grid order in
    the tile follows within one update interval.
14. As D, mark the http MP3 station as a favourite. Then, as admin, rename that station and Save: within one poll
    interval its card shows the new name. (The favourite is checked after a restart in (f)2.)
15. Edit a station, change nothing, Apply to list, Save: nothing changes (no new item, same status).
16. A station name or `icy-name` with `<b>` in it shows the angle brackets as text, never bold.
17. With the dialog open, press Save at the bottom: it refuses with "Finish the station dialog first".
18. Escape inside the dialog closes it without applying.
19. Check a slow URL (an unreachable host), press Cancel at once, then Edit another station: when the slow answer
    arrives nothing in that station's dialog changes, and its Check button works straight away.
20. Edit a SUB/WAVE station, change its URL to a plain MP3 stream and Check: Apply asks once more ("found a plain
    stream"). Press Check again: Apply asks again (K4). Cancel keeps it SUB/WAVE.
21. (L7) Edit a SUB/WAVE station, change its URL and press Apply **without** Check: the warning starts "This is a
    SUB/WAVE station, and its new URL has not been checked" (not the generic plain-stream text). Cancel keeps it
    SUB/WAVE.
22. (L8) Add station with a plain stream that sends `icy-genre` or `icy-url` (most Icecast streams do) and press
    Check: Genre and Website are filled (a bare host gets `http://`), and typing in Notes updates "N of 280
    characters". Cancel.

**(d) Playback matrix** (from L0). Fill one row per stream: web (Firefox or Chrome) and ★ Android; Chromecast where
available. Play at least 30 s, then Stop.

Every station plays through Jellyfin (L10: there is no Direct mode).

| Stream | Web | Android | Chromecast |
|---|---|---|---|
| http MP3 | | | |
| https MP3 | | | |
| AAC | | | |
| HLS | | | |
| the `.pls` station from (c)4 | | | |
| http MP3, Jellyfin opened over https (skip if there is no https address) | | | |
| SUB/WAVE (both stations) | | | |

Then, as admin, untick "Allow audio playback that requires transcoding" for user A (Dashboard → Users → A), and as A
play the http MP3: it does not play (expected, the admin's choice; write down what the client shows). Tick it again.

**(e) Station page** (cards L5b, L8 and L6: a header shows only while a station plays, for that station; a plain
stream's header comes from the station list and asks only for its title). Two
SUB/WAVE stations: **S1** is your own station (the migrated one, on a host you control); **S2** is the second one, the
public SUB/WAVE demo, which you cannot change. At least one plain stream enabled. Web client as non-admin user A;
★ steps again in the Android app. Each step: one action, then what you should see.

1. ★ Stop anything that plays. Open the Live Radio tile. → The grid only, no header.
2. ★ Play S1 from the grid. → A header for S1 above the grid: cover, track, DJ, Stop. No station picker, no Play button.
3. ★ Wait for a track change. → The header and the playing bar show the new track within a few seconds.
4. ★ Pause (space bar on the web, the lock screen or notification on Android). → Within a second playback stops and
   the header goes (L11: Stop instead of Pause).
5. ★ Play S1 from the grid again. → The header for S1 shows.
6. ★ Play S2 from the grid. → The header switches to S2 (cover, track, DJ, panels); the playing bar shows S2's track.
7. ★ Press Stop in the header. → Playback stops; the header goes.
8. Play S1 from the grid. → The header for S1 shows.
9. Play a plain stream from the grid. → The header switches to the plain stream's: logo, name, a "Now:" line, format
   line, Stop; no track, DJ or request box. The playing bar keeps Jellyfin's own text until the stream's title
   arrives (L6, steps 50–62).
10. Play S2 from the grid. → The header for S2 shows.
11. Go to Home. → The playing bar keeps following S2's track.
12. Open the Live Radio tile again. → The header for S2 shows at once.
13. In S1's SUB/WAVE admin, write down whether likes are on, whether lyrics are on, and whether a schedule is published.
14. In S1's SUB/WAVE admin, switch lyrics off (or, if they are already off, switch likes off) and save. Write down
    what you switched.
15. Outside Jellyfin, open S2's own web player. Write down whether it shows a like button, lyrics and a schedule.
16. Back in Jellyfin, look at S2's header (S2 still plays). → It matches step 15: a heart only if S2 has likes, a
    Lyrics panel only if it has lyrics, "Today on the station" only if it publishes a schedule. Write down S2's
    track and its like count.
17. Play S1 from the grid and look at its header. → It matches steps 13 and 14: the feature you switched off shows
    nothing (no Lyrics panel, or no heart); the others show as written down in step 13.
18. If S1's heart shows, press it. → S1's count goes up by one.
19. Play S2 from the grid. → If the track from step 16 is still on, S2's like count is the one from step 16 (another
    listener of the public demo may have liked it in between; note it if so).
20. In S1's SUB/WAVE admin, switch the feature from step 14 back on and save.
21. With S2 playing, send a request in S2's header. → S2's DJ answers; the page shows the acknowledgement.
22. In the browser's devtools (Network), note the request id from the `POST /Jellywave/Request` response.
23. As user B, poll user A's request id (`GET /Jellywave/Request/<id>`). → 403.
24. As user A, send requests on S2 quickly until the station refuses. → The station's message (with the wait, if it
    gives one) shows under the box; the box stays.
25. Raw API, non-admin session: `GET /Jellywave/Stations`. → No stream address and no stream path in it (a station's
    website, from step 34 on, is the only `http` link it may hold).
26. Raw API, non-admin session: `/Jellywave/Feed` with a plain stream's id, a disabled station's id, then `nope`. →
    404 each time.
27. Raw API with an API key: `/Jellywave/Stations` and `/Jellywave/Feed?stationId=<id>`. → 403 both. (C's 403 was
    checked in (b)4 and (b)7.)
28. Name a SUB/WAVE station with `<b>` in it, Save, and play it. → The playing bar's second line shows the angle
    brackets as text. Rename it back.
29. Play S2 and stay on the tile page. In another tab, disable S2 and Save. → Within a few seconds the header goes;
    within about a minute the playing bar stops being repainted (it keeps its last text).
30. Check the server log. → No ERR or WRN from Jellywave for S2.
31. Enable S2 again and Save.
32. Disable both SUB/WAVE stations and Save, then play a plain stream. → Its plain header shows; the drawer link
    (legacy layout) stays.
33. Enable both again and Save.
34. (L8) As admin, edit the https MP3 station from (c)3: Genre `Test genre`, Notes `Notes with <b>tags</b>`, Website
    `https://example.org/`; Apply to list, Save. → The row's head is still green: "✓ in the tile", or "✓ answered …
    ago" if someone played it since the restart (card L13).
35. ★ As A, play that station from the grid. → A plain header: logo, name, "Test genre", "Notes with <b>tags</b>" (angle
    brackets as text, not bold), a format line like "MP3 · 128 kbps" (no "Relay" since L10), "example.org ↗", Stop.
36. Click "example.org ↗". → example.org opens in a new tab; the Jellyfin tab keeps playing.
37. In devtools (Network), filter on `Jellywave` and watch 30 s while the plain stream plays. → Only
    `GET /Jellywave/Stations`, at most once a minute, and (L6) `GET /Jellywave/Title?stationId=<its id>`, about
    every 5 s; no Feed, Like, Lyrics, Schedule or Request call.
38. Open the station's item details page (Jellyfin's own). → Within one update interval of the Save, the description reads
    "Notes with btags/b": the server drops `<` and `>` from the item's description. (The station page header in step 35
    shows the notes as typed, "Notes with <b>tags</b>", as text.)
39. Play S1 from the grid. → The SUB/WAVE header for S1; one header only.
40. Play the plain stream again. → Its plain header; one header only.
41. Press Stop in the header. → Playback stops; the header goes.
42. As admin, edit the same station and type `javascript:alert(1)` into Website. → The hint "Website: only http:// and
    https:// are allowed; the header leaves it out." shows.
43. Apply to list, Save. → The row's head is still green: "✓ answered … ago" (played in 35 and 40; card L13).
44. As A, wait a minute, then play the station. → The header has no website link.
45. Raw API, non-admin session: `GET /Jellywave/Stations`. → The plain station has `"Kind":"Stream"`, its `Genre` and
    `Notes`, and no `Website` field (Jellyfin leaves out null fields); no http stream address anywhere.
46. As admin, open + Add station and look at the Notes box. → One label, three rows, styled like the other fields.
    Cancel.
47. As admin, set the station's Website back to `https://example.org/` and Save.
48. **Android only.** In the Android app as A, wait a minute, play that station and tap "example.org ↗" in the header.
    → Note whether a browser opens, and whether the app stays on Jellyfin and keeps playing.
49. As admin, clear the station's Genre, Notes and Website and Save.
50. (L6) ★ As A, play the http MP3 station from (c)2 (an Icecast stream; most send titles) from the grid and look at
    the header at once. → A line "Now: Querying upstream…" under the name (or already a title).
51. Wait 25 s. → "Now: <the title>", as the station's own player or website shows it. Write it down.
52. ★ Look at the playing bar. → First line "Artist – Title" (en dash, split at the first " - "), second line the
    station's name.
53. Keep it playing until the track changes. → The header line and the bar show the new title within about 30 s.
54. Raw API, non-admin session: `GET /Jellywave/Title?stationId=<that station's id>`. → JSON with `"State":"Title"`
    and the title; no stream URL in it.
55. As admin, untick On for one plain stream and Save. Raw API, non-admin session: `GET /Jellywave/Title?stationId=`
    with S1's id, then the disabled stream's id, then `nope`. → 404 each time. Tick On again and Save.
56. Raw API, non-admin session: `GET /Jellywave/Feed?stationId=<the MP3 station's id>`. → 404 (the SUB/WAVE routes
    still refuse a plain stream).
57. ★ Play the HLS station from (c)6. → "Now: Not supplied by the station" at once, and it stays.
58. As admin, + Add station with the `http://` URL that redirects to `https://` from (c)8; do not press "Use it".
    Name it `Redirect test`, press Add to list (twice if it warns), then Save. → The row says "✓ in the tile".
59. ★ As A, play that station. → It plays (ffmpeg follows the redirect); within about 20 s the header reads "Now:
    Delayed response: is the stream up?".
60. Check the server log. → One WRN line "Title sampling of station Redirect test failed: answered with a
    redirect; …" (no address in it), and still only one after another minute.
61. Press Stop. As admin, remove that station and Save.
62. Play nothing for one minute, then `GET /Jellywave/Title?stationId=<the MP3 station's id>` (non-admin). →
    `"State":"Waiting"`: the server forgets a title 45 s after the last listener and reads no stream while nobody
    plays.
63. ★ Play the http MP3 station from step 50, open the tile, wait until its title shows, then go to Home. → The
    playing bar keeps following the title (it changes with the next track, step 53's wait).
64. As admin, open Advanced, untick "Show what's playing" under "Now playing" and Save. → The help text above it names plain-stream
    titles and the extra listener per sample.
65. ★ As A, play the http MP3 station and open the tile. → The plain header has no "Now:" line (allow up to 15 s after
    the Save); `GET /Jellywave/Title?stationId=<its id>` answers `"State":"Off"`.
66. As admin, tick "Show what's playing" again and Save. → Within about 30 s the "Now:" line and the title are back.

**(f) Hygiene**

1. `JELLYFIN_URL=http://<jellyfin-host>:8096 JELLYFIN_TOKEN=<admin key> scripts/verify-jellyfin.sh <user A>` prints
   `== ALL CHECKS PASSED` (every enabled station's item and PlaybackInfo).
2. Restart Jellyfin. As D (non-admin), the favourite set in (c)14 on the renamed station is still set (the restart
   empties the user-data cache, so the row really comes from the database).
3. With every station enabled and one playing, 30 minutes of server log with 0 ERR and 0 WRN from Jellywave.
4. Last, because it makes a new channel (L3 step 15): rename the tile on the plugin page (Advanced → Tile name) and Save. The rename notice and
   "Give them access to the renamed tile" work again; the station list is unchanged. Then rename it back to "Live Radio", Save, press
   "Give them access to the renamed tile" if offered, and run "Refresh Channels".

**(h) Card L11: first-run fixes and plain words** (web client, as admin; S1 and the http MP3 station as in (e))

1. Open Dashboard → Plugins → Jellywave, then open Advanced. → No tile status line at the top (card L13: shown only
   when something is wrong); under Advanced "Now playing updates: ✓ running…"; no result text right under
   "Add the tile to every user now" before it is pressed (card L13's transcoding line may follow; see (i)); the Live panel line is grey "Not installed: File Transformation is not in its
   catalog for Jellyfin 12.3 yet; see README." (or green "✓ Active").
2. Press + Add station. → No ⚠ line in the box; the button reads "Add to list".
3. Type `http://u:p@example.org/a.mp3` into Stream URL. → A hint shows: "Stream URL: it contains credentials…" (no
   blank-name hint before a Check).
4. Replace the Stream URL with the http MP3 URL from (c)2 and press Check. → "✓ Audio: …" and "✓ Radio stream."; no line names SUB/WAVE;
   "Type: stream (checked).".
5. Clear Name, press Add to list (twice if it asks), then Save. → The row's name is the stream's host; its status
   "✓ in the tile · …"; within one update interval the tile's item reads "<host> — Live".
6. Edit that station. → "⚠ Hint: Name is blank: the station shows as "<host>"."
7. Press Cancel, then remove that station and Save.
8. Press ↓ on the first station, then reload the browser tab (F5). → The browser asks before it leaves; stay.
9. Press ↑ on the same station, then reload the tab. → No question; the page reloads.
10. Press ↓ again, then open Dashboard from Jellyfin's own menu. → No question (jellyfin-web cannot be stopped).
    Back on the plugin page the saved order shows.
11. Devtools → Network → block the request URL `*Plugins/6ca437a0*`; untick On for one station, then Save. → The
    spinner goes; red "✕ Not saved: Request failed: … Your changes are still on this page; press Save to try again."
    next to Save; the row still says "not saved yet". Unblock, press Save → saved; tick On again and Save.
12. Edit S1 and press Check. → "✓ SUB/WAVE detected …"; "Type: SUB/WAVE station (detected)."; the button reads
    "Apply to list". Cancel.
13. Open Advanced and read the page top to bottom. → "tile" everywhere, "channel" only in "where Jellyfin calls the tile a channel";
    "Type" and "On" columns; "Update every (seconds)"; "Reload status"; status words "in the tile"; a station waiting for its
    first update is grey.
14. ★ As user A, play S1. → The playing bar shows Stop and no play/pause button (on a phone, Stop at the right).
    On the web, narrow the browser window to about 1000 px → Stop still in the centre, no play/pause; then to about
    800 px → Stop at the right, no play/pause. Widen it again.
15. ★ Press the bar's Stop. → Playback stops and the bar goes.
16. ★ Play the http MP3 station, then pause it from the lock screen or notification (Android), or on the web from the
    browser's media controls (Chromium: the ♫ button in the toolbar) or a keyboard's play/pause media key. → Within
    about a second playback stops and the bar goes. Not the space bar: jellyfin-web 12.3 maps no play/pause to it
    outside the video player, so it presses whichever button has focus, often the grid's play button, which restarts
    the station (HI host run 2026-10-29; MI read the bundle's key map, 74550 in `main.jellyfin.bundle.js`).
17. ★ Play the http MP3 station, then play S2 from the grid. → S2 plays; nothing stops it.
18. ★ Right after (h)17, with S2 playing, play a music track from any library, then pause it. → Jellyfin's play/pause
    is back in the bar; the track stays paused and resumes.
19. (Chromecast, if available) Cast S1, then pause it from the cast controls. → The station stops. Cast S1 again, then
    play S2 from the grid and wait 30 s. → S2 plays and keeps playing.

**(i) Card L13: page layout, plain-stream health, who can see it** (web client, as admin unless a step says
otherwise; the http MP3 station from (e)50, the HLS station from (c)6, S1, users A and B from (a)2; [me] = MI does it
through the API and reports; fix round 2026-10-29: texts from the approved sketch)

1. Open Dashboard → Plugins → Jellywave. → From the top: "Internet radio in a "Live Radio" tile in My Media.", the
   Stations table (columns Order, Name, Type, On, Status, no Format), Who can see it, Live panel, a closed "▸ Advanced",
   Save. No tile status line and no notice above the table.
2. Look for Tile name, "Show what's playing", the cover art box, "Update every (seconds)" and "Now playing updates". →
   None shows until you open Advanced; then all five show there, with the values they had before the update, and the
   tile name's help reads "Shown in My Media. Renaming makes a new tile; see README first."
3. Close Advanced and reload the tab. → Advanced is closed again.
4. Look beside "Reload status". → No "Unsaved changes." and no second Save.
5. Press ↓ on the first station. → "Unsaved changes." and a Save appear beside "Reload status". Press ↑ on the same
   station. → Both go.
6. Untick On for the HLS station and press the Save beside the table. → "Settings saved"; the second Save goes; the
   row says "disabled". Tick it again and save with the same button. → The row is back to "✓ in the tile".
7. With Advanced closed, change nothing in the table and press the bottom Save. → Saved; still no second Save.
8. Open Advanced, type `3` into "Update every (seconds)", close Advanced, press ↓ on the first station, then press the
   Save beside the table. → Nothing is saved; Advanced opens and the browser points at the field (5 is the least).
   Close Advanced and press the bottom Save. → The same. Type `20`, press ↑ on the station, press the bottom Save. →
   Saved.
9. Press ↓, then Edit on any station, then the Save beside the table. → "Finish the station dialog first (Apply to
   list or Cancel), then Save." beside both Saves. Press Cancel. → The note goes from both. Press ↑.
10. [me] Read `GET /Jellywave/Health` as admin. → Every station's fields; no `http`, no host name, no port, no path in
    the body (`grep -c http` prints 0).
11. Look at S1's row. → "✓ on air · N s ago" in green.
12. ★ As A, play the http MP3 station for at least 30 s, then Stop. As admin, press Reload status. → Its row reads
    "✓ answered N s ago" (or "N min ago") in green, not "in the tile".
13. As admin, look at the HLS station's row (play it as A first if you like). → "✓ in the tile", never "answered".
14. Restart Jellyfin, open the page again. → The http MP3 station's row is "✓ in the tile" again (memory only).
15. Add the redirecting station as in (e)58 (`Redirect test`), Save. ★ As A, play it for at least 30 s (it plays),
    then Stop. As admin, press Reload status. → Its row reads "↪ moved N s ago: Edit → Check → Use it to read titles"
    in yellow (not "no answer"). Then remove it and Save.
16. [me] From `GET /Users` (admin), count the users without `IsDisabled` (M) and, of those, the users whose policy has
    `EnableAllChannels` true or the tile's id in `EnabledChannels`, and no `BlockedChannels` entry naming the tile or
    an earlier tile name (N). Compare with `GET /Jellywave/Viewers` (`Users`, `Visible`). → Equal; MI reports N and M.
17. Under "Who can see it". → "N of M active users can see the tile." with the N and M MI reported in (i)16, above
    "Add the tile to every user now".
18. [me] `POST /Users/<B's id>/Policy` with B's policy and `EnableAudioPlaybackTranscoding: false`, then
    `GET /Jellywave/Viewers` as admin. → `"NoAudioTranscoding":["<B's name>"]`.
19. Reload the plugin page. → Under the button, in yellow: "⚠ <B's name> can see the tile but can't play stations:
    "Allow audio playback that requires transcoding" is off for them (Dashboard → Users → <B's name> → Profile → Media
    playback)." Then open Dashboard → Users → B → Profile. → Under "Media playback", "Allow audio playback that
    requires transcoding" is unticked.
20. [me] Turn the setting back on for B (same route), read the policy back. → `EnableAudioPlaybackTranscoding: true`;
    `GET /Jellywave/Viewers` shows `"NoAudioTranscoding":[]`.
21. Reload the plugin page. → The yellow line is gone.
22. [me] As a non-admin user (A), `GET /Jellywave/Viewers`. → 403, no user names in the body.
23. Rename the tile to the name of another plugin's tile, if one exists on the host (skip otherwise), and Save. → A
    red notice at the top: "✕ Another plugin's tile is also called "<that name>". Jellywave does nothing until you
    rename it (Advanced → Tile name).", and Advanced opens. Rename it back and Save. → The notice goes.

**(j) Card L9: starter stations** (a fresh install can only be seen on a throwaway Jellyfin 12.4 whose configuration
folder is new and empty, such as a new container or VM, never the test host's own configuration; the test host is
used for the upgrade steps 9–10; [me] = MI does it through the API or the shell and reports)

1. [me] On the throwaway server, install the branch build (`dist/Jellywave_0.5.0.0.zip` unpacked into
   `<jellyfin-data>/plugins/Jellywave_0.5.0.0/`) and start Jellyfin. → The log shows "New configuration with 5 starter
   station(s); remove any of them in Dashboard > Plugins > Jellywave" and no ERR from Jellywave.
2. Open Dashboard → Plugins → Jellywave. → Five stations, in this order: SUB/WAVE demo, KEXP, WRUW 91.1 FM, WJCU
   88.7 FM, XCSB. All have On ticked. No notice at the top.
3. Wait one minute, then press Reload status. → SUB/WAVE demo "✓ answered when checked N s ago · idle: nobody
   listening" (card L14: nobody plays it); the other four "✓ in the tile".
4. Open My Media → Live Radio. → Five items. Within a minute: KEXP's orange logo, WRUW's logo, WJCU's "88", XCSB's
   globe. The SUB/WAVE demo shows the Jellywave logo (card L14: idle until someone plays it).
5. ★ Play each station for about 20 s, then Stop. → Each one plays.
6. Open the station page while XCSB plays. → Header: genre "Freeform", "Cleveland's Alternative to FM.", a website
   link to xcsb.live.
7. [me] Read `<jellyfin-data>/plugins/configurations/Jellyfin.Plugin.Jellywave.xml`. → Five `StationDefinition` entries, each
   `Id` once; `SchemaVersion` 1.
8. Remove KEXP (✕, confirm in the row) and press Save. [me] Restart Jellyfin. → Four stations; KEXP is not back.
   Remove the other four, Save. [me] Restart Jellyfin twice. → "No stations yet. Add one with its stream URL."; the
   tile is empty both times.
9. On the test host (its own configuration, already on a 0.5.0.0 build), deploy this build and restart. → The
   station list is exactly as before; no starter added. [me] The log has no "New configuration" line.
10. [me] On the throwaway server, stop Jellyfin and delete it (container and volume).

**(k) Card L14: a SUB/WAVE station is asked only while someone listens** (on the test host; S1 is its own SUB/WAVE
station; nobody else plays anything during these steps; [me] = MI through the API, the Jellyfin log or S1's own log.
Card L14 changes (i)11: S1's row reads "idle: nobody listening" unless someone plays it)

1. Stop every playback and close every Jellyfin tab except the plugin page. Wait 2 minutes, then reload the plugin
   page. → S1 and the SUB/WAVE demo read "idle: nobody listening" in grey, no symbol, no "on air" time.
2. [me] Count S1's `/api/now-playing` and `/api/state` requests from the Jellyfin server over one quiet minute (S1's
   log). → 0.
3. [me] The Jellywave log since step 1. → At most one "Station <S1> is idle (nobody plays it or has its page open); not
   asked until someone does" line per SUB/WAVE station; no WRN, no ERR from Jellywave.
4. Open My Media → Live Radio. → S1 and the demo show the Jellywave logo and the station's own text: its Description
   from the station editor, else its Notes, else "Live broadcast. One shared stream; everyone hears the same thing at
   the same time." Not an old track. (Fix round R2: give S1 a Description first, e.g. "S1 test station", and Save;
   S1's item then reads exactly that within one update interval.)
5. Press Reload status. → The button waits a moment; S1 then reads "✓ answered when checked N s ago · idle: nobody
   listening" in green, and the demo too.
6. [me] S1's requests for that press. → Exactly one `/api/now-playing`; then 0 in the minute after. Press Reload
   status twice within 10 s. → [me] One `/api/now-playing` for both presses (fix round S3).
7. ★ As A in the web client, play S1. → The station page header shows the live track within a few seconds (for a
   moment it may read "The server has not heard from the station yet.").
8. [me] The Jellywave log. → "Station <S1> is in use (someone plays it or has its page open); asking it for now
   playing" within about a second of the Play press. S1's log: `/api/now-playing` and `/api/state` every 20 s while
   it plays.
9. As admin, press Reload status. → S1 "✓ on air · N s ago" in green; the demo still idle.
10. ★ As A, pause S1 for one minute. → [me] S1 is still asked every 20 s (paused counts as playing).
11. ★ As A, press Stop, then go to Home. Wait 2 minutes. → [me] S1's requests stop within about a minute of the stop;
    the log has one "is idle" line for S1. Reload the plugin page. → S1 "idle: nobody listening".
12. Advanced → untick "Show what's playing", Save, press Reload status. → [me] No request to S1 for the press. Tick it
    again and Save.

Android (★ as A, in the Jellyfin Android app):

13. Play S1 (the app has no station page). → [me] The "is in use" line for S1 within about a second; S1 asked every
    20 s.
14. Stop. → [me] The "is idle" line for S1 within about a minute; then no request to S1.

Fix round (all [me], through the API with the users' own tokens):

15. As C (blocked from the tile), report playback of S1's item (`POST /Sessions/Playing` with its item id), then
    wait one minute. → No "is in use" line for S1, no request to S1 (fix round S2). Report the stop.
16. As A, play S1 in the web client; meanwhile, as A through the API from a second session (its own device id, so the
    web client's session keeps playing), report a start of the demo's item and its stop straight after, once a second
    for two minutes. → S1 is still asked every 20 s (S1's log) (fix round R1). The demo may get an "is in use" line now
    and then (a poll can see its item between a start and its stop); that is fine, what counts is S1.

Fix round 2 ([me] through the API and the logs; ★ steps in the web client as A):

17. ★ Play S1 for 30 s, press Stop, wait 45 s. [me] From the second session, report one start and stop of the demo's
    item; within 5 s, ★ play S1 again. → [me] The "is idle" line for S1 comes with the demo's report (not at the next
    regular poll), and the "is in use" line for S1 within about 2 s of the Play press; the header shows S1's live track
    within a few seconds (fix round 2, F1).
18. Nobody plays the http MP3 station from (c)2. [me] Note its `LastReadUtc` in the admin `GET /Jellywave/Health`. As C
    (blocked from the tile), report playback of its item (`POST /Sessions/Playing`) and wait one minute. → Its
    `LastReadUtc` has not changed: the server did not sample the stream for C (fix round 2, F3). Report the stop.

**(g) What to send back**

- A pass or fail for every step id ((a)1 … (f)4, then (h), (i), (j) and (k)), with what you saw for each fail.
- The "Give them access to the renamed tile" counts from (b)6 and (b)8, the grant counts from (b)9, and the status codes and item counts
  from (b)4 and (b)7.
- The filled playback matrix (d), with the browser, Android app and Chromecast versions.
- The `verify-jellyfin.sh` output and the Jellywave log lines from (f)3 (counts, and every WRN/ERR line in full), with
  host names and IPs replaced by `<jellyfin-host>` / `<station-host>`.
- Jellyfin and File Transformation versions; whether the old-page check (a)6 showed the old script or said nothing.
- Screenshots of the plugin page with the rename notice and of the station page while a SUB/WAVE station plays and
  while a plain stream plays (no addresses in view).
- Anything else that surprised you.

### Decisions for HI at the test stop

1. **L6 (ICY titles for plain streams):** HI 2026-10-28: build it, after L8 (header for plain streams).
2. **Upstream Jellyfin report** for the unescaped remote input (`-i "<url>"`, `EncodingUtils.GetInputArgument`): HI
   2026-10-28: HI handles it in a separate session, from a note kept outside this repo. Jellywave is fixed either way
   (0.5.0.0); other plugins and M3U tuners may not be. Nothing has been filed.
3. **S-b, the IL scan's helper gap:** add an allow-list of the plugin's static and extension helpers the controller may
   call (closes the deny-list limit listed in THREAT-MODEL "Accepted risks"), now or as a follow-up. HI 2026-10-28: build it
   as card S-b, after L8 and before L6 (MI's slot: L6 changes the station checkpoint).
4. **Merge and tag:** after the host test, review the Hard Stop changes (L1 rename bridge and policy writes, L5 station
   routes), merge `live-radio` into main, and tag v0.5.0.0 (push, merge and tag are HI's). HI 2026-10-28: L5c, L8
   and L6 go in first; the release number is not a concern until then.
5. **Admin "remove block" button** (12.3 cannot unblock from the dashboard): HI 2026-10-28: noted, deferred.
6. **SUB/WAVE "requests on/off" flag:** HI 2026-10-28: deferred, a SUB/WAVE-side change.

(Finding F1 from L7 is fixed in the L7 fix round, so it is no longer a decision.)

### Code to reuse

- `Channels/JellywaveChannel.cs`: `BuildLiveItem`, `ResolveClientUrl`, `ComputeDataVersion`
- `Services/StationUrls.cs`: card 14 rules for SubWave stations; a new sibling for whole stream URLs
- `Services/StationClient.cs`: the no-redirect client and its bounds; probe, cover, avatar, requests
- `Services/ImageSniffer.cs`, `Services/LiveItemArtwork.cs`: logos and covers
- `Services/LiveItemFinder.cs`: derived-id resolution and the collision check
- `Services/StationAccess.cs`, `Api/StationAccessAttribute.cs`: the channel gate, unchanged
- `Services/RequestReceipts.cs`, `Services/ScheduleCache.cs`, `Services/PollerHealth.cs`: key by station
- `Services/AccessGranter.cs`: the grant button is unchanged; it gains the one-shot "carry access over" pass (same lock, add-only)
- `Web/ui.js`: after card 18, extend the state key with the station id

### Risks

| Item | Severity | Handling |
|---|---|---|
| ffmpeg input quoting in 0.1–0.4.2 (`Uri.ToString()` un-escapes `%22`; Jellyfin quotes `-i "<url>"` without escaping) | Fixed in L2; admin-only before | HI 2026-10-27: no 0.4.x hotfix, tracked in THREAT-MODEL "Known issues"; L7 changelog names it; consider an upstream Jellyfin report |
| Renaming the tile on upgrade drops grants and **lifts blocks** | Blocker without the bridge | HI chose the rename. Rename bridge: old blocks honoured read-only; one-shot "Carry access over" button. Hard Stop review |
| Old-blocked user reaches the new item ids through the raw API before "Carry access over" | Accepted, short window | Tile hidden (`IsEnabledFor`, verified) and station routes refuse; the press copies the block so Jellyfin enforces it everywhere; changelog tells the admin to press it right after upgrading |
| Blocks cannot be set or cleared in Jellyfin 12.3 (corrects the earlier "an Access page save clears blocks" note) | Accepted | Host test 2026-10-28: a policy save ignores `BlockedChannels` (`UserManager.UpdatePolicyAsync`), so blocks carried over stay until a database edit; README, DESIGN and THREAT-MODEL say so |
| Config migration loses an upgrader's station | Required | L1 tests on real old XML; old fields readable for one release |
| One gate for many stations (no per-station hiding) | Accepted (HI) | Config page and README say so; Jellyfin has no per-item visibility for channel items |
| Station id on routes lets a user reach a disabled or plain station's endpoints | Required | Resolve against enabled SubWave stations only; 404 otherwise; reflection test |
| Admin-pasted URLs become ffmpeg inputs; ffmpeg follows redirects | Accepted (already in threat model) | Same boundary as today: admin config is trusted; the probe shows redirects |
| Query strings in plain-stream URLs are disclosed through PlaybackInfo | Accepted (HI) | Allowed; README and threat model say to keep secrets out of stream URLs |
| A generic name ("Live Radio") collides with another radio plugin's tile | Suggested | Fails closed (card 15); the config page shows it; pick a unique name |
| AAC breaks Chromecast | Suggested | MP3 first; L0 decides AAC timing |
| One ffmpeg per listener per station in Relay | Nit | README line |
| jellyfin-web selectors | Known | Unchanged; the page degrades to doing nothing |

### Verification (when built)

- `set -o pipefail`, then build, test, `dotnet format --verify-no-changes`, `node --check`,
  `bash -n scripts/*.sh`, `scripts/check-manifest.sh` (only ok), and the host-name grep (empty).
- Unit tests: migration (diagram 9 cases), station list → items, `DataVersion` follows the list,
  probe parsing (`.pls`, `.m3u`, ICY headers, a redirect), per-station state isolation, routes with an
  unknown, disabled or plain station id → 404, a receipt polled by another user → 403, and a receipt
  that routes to its own station.
- Test host, upgrade from v0.4.2.0:
  - The tile becomes "Live Radio".
  - A user blocked from the old tile sees no tile, and gets 403 from `/Jellywave/Feed`.
  - A user who had the old tile by name sees nothing until "Carry access over" is pressed, then
    sees it. A second press reports 0 carried.
  - An `EnableAllChannels` user sees it at once.
  - "Refresh Channels" removes the old tile.
- Test host, playback: a second SUB/WAVE station and two plain MP3 streams play in web
  and Android; each SUB/WAVE item gets its own covers; the page, likes and requests work per
  station; `verify-jellyfin.sh` ALL CHECKS PASSED; 30 min log with 0 Jellywave ERR/WRN.

## Phase 5: release hardening before v0.5.0.0 (plan approved 2026-10-30)

Source: the outside review of 19be155 (Codex Astra, started by HI, no brief), its second look at the plan brief, and
host probes run while checking it. Branch `live-radio`; cards one at a time; DA builds, a separate RA reviews; MI
accepts. R1 is elevated (data integrity).

### HI decisions (2026-10-30)

| # | Decision |
|---|---|
| D1 | No 0.4.x installs besides HI's host (release downloads 0 to 3 each): the 0.4 upgrade path is not a release concern. The migration code stays |
| D2 | A later tile rename asks for confirmation before saving; the "Give them access to the renamed tile" button stays |
| D3 | The whole-user save race is accepted and documented (it can revert unrelated user settings and permissions, not only channel access) |
| 3/4/5 | Fix: an unreadable settings file is never overwritten; a save is written before it is applied; the server stamps the change time |
| A | Settings are written to a new file in the same folder, then swapped in as one step, keeping one previous copy (HI agreed after the reviewer's second look) |
| B | Every station whose id is publicly known (`live`, the five starter ids) gets a new random id, once, on every install; new seeds and migrations get random ids |
| C | Plugin page warning near the station list and the Check warning: texts below, HI approved |
| D4 | Releases 0.1.0.0 to 0.4.2.0 are removed from GitHub and the catalog (catalog entries first, then the downloads; git tags stay) |
| D6 | Document that tile access hides but does not lock, as Jellyfin behaviour for all media |
| D8 | GitHub settings (immutable releases, a `v*` tag rule) deferred: HI does them with `gh` later; R3 must keep working when they are on |
| D9 | Animated images are refused, and the docs say why |
| D10 | A per-user cap on song requests and on station calls in general; a station's own lower limit still wins |
| D11 | Staging and order: MI |

### Facts found (Jellyfin 12.4, source and test host, 2026-10-30)

- Tile access hides the tile but does not lock it. As a user blocked on both the old and the new tile id, and as a
  user with the tile unticked: My Media and `/Channels` hide it; `/Items/{item}` and `PlaybackInfo` answer 404; but
  `/Channels/{tileId}/Items?fields=MediaSources` answers 200 with every station and its stream URL, and
  `/Audio/{item}/stream?static=true` answers 200 with audio. With **no sign-in at all**, `/Audio/{item}/stream`
  answers 200 audio/mpeg. `AudioController` has no `[Authorize]` (upstream jellyfin/jellyfin#5415 lists it);
  `ChannelsController.GetChannelItems` does not check channel visibility. Our `/Jellywave/*` routes answer 403.
- A channel item id is `MD5(UTF-16LE(typeof(T).FullName + externalId + channelName + "16"))`, as a `Guid`
  (`ChannelManager.GetIdToHash`, `LibraryManager.GetNewItemId`; lowercased only when `EnableCaseSensitiveItemIds`
  is off). All 10 item ids on the test host were reproduced from the tile name and the station ids. Fixed public
  station ids (the starters, the migrated `live`) therefore make item ids, and the no-sign-in audio, reachable to
  anyone on every install that keeps the default tile name.
- The plugin cannot close the signed-in part: returning no items for one user makes Jellyfin's reconciliation delete
  the items for everyone.
- Static playback of a remote source goes through Jellyfin's default named `HttpClient`
  (`AudioHelper`, `FileStreamResponseHelpers.GetStaticRemoteStreamResult`), not ffmpeg, and follows redirects.
- `BasePlugin<T>` (12.4): `LoadConfiguration` catches any read error and saves defaults over the file;
  `UpdateConfiguration` assigns before it saves; `SaveConfiguration(T)` and `UpdateConfiguration` are virtual;
  `ConfigurationChanged` is a public delegate property (a derived class can invoke it); `Configuration` has a
  protected setter; `XmlSerializer` is a protected property.
- SUB/WAVE (local clone): request ids are `randomUUID()`; `/api/request` allows 1 per 20 s and 8 per hour per source;
  like status is per caller IP (`controller/src/routes/likes.ts`), so to a station the whole server is one caller.
- Probes are `local/jwtest.py raw`, `raw2`, `raw3`, `raw4 <plugin guid>` (untracked).

### Card order

```
R1 settings safety ─► R2a server tightening ─► R2b page ─► R3 CI/release (MI) ─► R4 threat model + DESIGN
   ─► HI host check + Android/Chromecast pass ─► R5 README ─► squash to main, tag v0.5.0.0 ─► retire old releases
```

### R1 Settings safety (elevated)

- **One atomic writer.** Override `SaveConfiguration(PluginConfiguration)`: serialize to a new uniquely named temp
  file in the same folder (`FileMode.CreateNew`), flush to disk and close it, then `File.Replace(temp, target,
  <target>.previous)`, or `File.Move(temp, target)` when there is no target yet. A failure at any point deletes the temp
  file (best effort) and throws; the target is the old complete file or the new complete file, never partial. Every
  save goes through it: page saves, carried names, the migration, the fresh-install save.
- **Load without the destructive fallback.** When the settings file exists, deserialize it with the plugin's own
  `XmlSerializer` before anything reads `Configuration`, and assign it with the protected setter, so Jellyfin's
  `LoadConfiguration` never runs. On a read failure:
  - copy the file aside as `<file>.unreadable-<UTC yyyyMMddTHHmmssfffZ>.xml` with `FileMode.CreateNew`, adding
    `-2`, `-3`… on a clash, so no copy overwrites earlier evidence;
  - run on defaults in memory (`RunningOnDefaults`) and save nothing;
  - only after the copy succeeded is it named as kept (Health field and page text: the file name, not the path);
  - if the copy failed, set `PreservationFailed`: the **server-side** save path refuses every save (page save,
    carried names) with a clear reason until a copy succeeds, and each save attempt retries the copy first; no
    restart needed.
- **Save order.** `UpdateConfiguration` builds a separate candidate (the incoming object after `PrepareSave`), stamps
  `LastChangedUtc = max(UtcNow, previous + 1 s)` (the client's value is ignored), writes it with the atomic writer,
  and only then assigns `Configuration`, clears `RunningOnDefaults`/`RunningUnsaved`, and invokes
  `ConfigurationChanged`. It never calls `base.UpdateConfiguration` (that would assign before a second write). An
  exception from a `ConfigurationChanged` handler is logged and the save still counts as saved.
- **Carried names** modify a copy of the configuration (deep copy), write it, then assign it. Never the live object.
- **Tests** (real temp directory, real XML serializer; failure points injected through a seam in the writer):

  | Failure point | Required outcome |
  |---|---|
  | Before writing | Old memory and old file remain |
  | After some bytes of the temp file | Old file loads; memory unchanged; the save reports failure |
  | During the swap | Target is the old or the new complete file |
  | A `ConfigurationChanged` handler throws after the write | Saved; new configuration active; no "Not saved" |
  | Restart after any failed save | Loads the previous complete configuration |
  | Unreadable file, writable folder | Original bytes untouched, one unreadable copy, no save, Health names the copy |
  | Unreadable file, copy fails | No copy named; every save refused server side; a later save retries and, once the copy works, saves |
  | Two unreadable starts | Two copies, the first unchanged |
  | Browser clock behind the previous save | The effective item `DateModified` (with the `MediaSourceSchemaDate` floor) still advances |
  | Fresh install, normal migration | Unchanged behaviour |

- **Accepted 2026-10-30 (MI).** Built in f52b789 and 90491ba. The separate RA review found one Required issue:
  on Windows, a `ReplaceFile` that renames the target to the backup and then fails left no settings file, so the
  next start seeded the starters. It also made four suggestions: keep `.previous` on the first save after an
  unreadable file; keep Unix permissions; word the refusal as "read the file and write its folder"; require a
  regular file. The fix round (eaed595) did all of these. A half-finished swap is now completed, or failing that the
  backup is restored, and a completed swap counts as saved (MI agreed: memory and disk match). The folder is not
  fsynced: DESIGN says a power loss right after a save may bring back the previous complete file. RA verdicts:
  judgement calls 1 to 5 agreed; Jellyfin 12.4 sends no exception text on a failed configuration POST (500 "Error
  processing request."), so the page reads the refusal from Health.
- **Checks at eaed595 (MI re-run):** build 0 warnings; 1256 tests passed; format ok; configPage 467 and stationPage
  446 passed; `bash -n` ok; publishRelease 79 passed; `check-manifest.sh` all ok; host-name grep empty.

### R2a Server tightening

- **Public ids re-keyed (B).** `StarterStations.Seed` and the 0.4.x migration give random ids
  (`Guid.NewGuid().ToString("N")`). At startup, any station whose id is in `KnownPublicIds` (`live` and the five
  former starter ids) gets a new random id once, saved with the atomic writer (in memory only when running on
  defaults or unsaved). Tests: two seeds differ; a restart keeps the ids; a re-keyed config is not re-keyed again;
  `DataVersion` changes once.
- **Station call budget.** One shared limiter for user-driven station calls (like GET/POST, lyrics, avatar, request
  POST, request status): at most 2 at a time per station and 8 in total. Over budget is refused at once (503 with
  `Retry-After`); no waiting queue. A permit is held until the response body is read and disposed, and released on
  cancellation and on every exception. The station permit is taken first and the global one without waiting, so
  callers stuck on one station never hold global capacity. The poller, title sampler and Check keep their own
  paths.
- **Per-user budgets (D10).** Per Jellyfin user, checked and reserved atomically before the station is called:
  - every user-driven station call: 60 per minute (count the page's normal use in the harness; it must stay under a
    third of that);
  - song requests: 1 per 60 s and 4 per hour across all stations;
  - like POST: 10 per minute.

  Over a budget: 429 with `Retry-After`. A station's own refusal does not use the user's quota; an uncertain outcome
  (timeout, connection lost after sending) does. Counts live in memory and reset on restart (accepted).
- **Caches.** Served only after the route's access gate and station resolution. Keys include the station key
  (id and origin, the existing `StationKey` rule). Bounded by entry count and total bytes. Concurrent misses for one
  key share one fetch.
  - Lyrics per (station, song id): 10 min for lines; 15 s for errors and "pending"; at most 64 entries per station.
  - Avatar per (station, image URL): 10 min; at most 32 entries and 8 MB in total.
  - Like status per (station, song id): 5 s; dropped after a successful like POST and when the song changes.
- **Receipts.** Our own opaque receipt token (random, never the station's id) maps to the user, the station key and
  the station's request id; the page polls with our token. At most 20 pending per user, the user's own oldest
  evicted first, never another user's. When the store is full (1000) and the user owns no receipt, the request is
  refused before it is sent to the station. Tests: the same station request id from two stations and two users
  never crosses.
- **Animated images refused (D9)** for covers, logos and avatars, by a bounded structural parse: GIF blocks walked
  (more than one image descriptor = animated); PNG chunks walked (`acTL` before `IDAT` = animated); WebP RIFF chunks
  walked (VP8X animation flag or an `ANIM` chunk = animated). Malformed or contradictory structure is refused. The
  existing byte and dimension limits stay.

- **Accepted 2026-10-30 (MI).** Built in 8a2c465..86f906b (six commits).

  **The separate RA review found one Required issue.** A station's 4xx or 503 refunded the whole budget, so one user
  could loop lyrics calls with made-up song ids (409) without limit and hold a station's slots.

  **Fix round** (cb5c451, 8cbc1f9, aa7f446, 00d1743):
  - a station's refusal gives back only the request or like window, and the general 60/min slot stays spent;
  - only a call that never left, the plugin limiter's own 503, or a full receipt store gives everything back;
  - a lyrics 409 is never cached, and caches evict the soonest-expiring entry first;
  - limits are now 4 per station and 16 in total, with a separate cap of 4 portrait fetches (MI decision, RA agreed:
    the page's first render fires three calls at one station);
  - the budget is checked before the receipt slot;
  - there is an origin-in-key test for each cached kind;
  - progressive JPEGs with more than 100 scans are refused;
  - a page save re-keys public ids;
  - the doc wording is fixed (ProblemDetails body; the limiter key is the whole base URL).

  **RA facts:**
  - no permit leak, no cache served before the gate, and no cross-user or cross-station leak (mutations killed);
  - the image walk classified 120 real files correctly and survived 210,000 mutated inputs without an exception;
  - the page's busiest minute is 17 station calls per user.

- **Checks at 00d1743 (MI re-run):** build 0 warnings; 1395 tests; format ok; configPage 467 and stationPage 449
  passed; publishRelease 79 passed; `check-manifest.sh` all ok.
- **For R5 (README and changelog):**
  - user-visible changes:
    - re-keyed stations get new items (favourites reset);
    - animated images, and JPEGs with more than 100 scans, show as no art;
    - per-user limits: 1 song request a minute, 4 an hour, 10 likes a minute, 60 station calls a minute;
    - "busy" or "too many" answers carry Retry-After;
    - like counts lag up to 5 s, and request progress up to about 8 s;
  - README's "The plugin adds no limiter" line and its favourites/migrated-station line are now stale.

### R2b Page

- **Warning near the station list (C):**

  > Tile access only hides the tile. On Jellyfin 12.4, any signed-in user can still read every station's address and
  > play it through Jellyfin's own API. Anyone who has a station's item id can play it without signing in. Jellyfin
  > works this way for all media, not only Jellywave. Don't add private streams, or addresses that contain passwords
  > or keys.

- **Check warning** when the stream URL's query has a parameter named like a secret (token, key, auth, sig,
  signature, secret, pass, password, session, sid; case-insensitive): "Every signed-in user can read this address,
  even users the tile is hidden from. Leave secrets out of it." A warning only; Add still works.
- **Rename confirm (D2):** Save with a changed tile name first asks: "Renaming makes a new tile. Users who had
  "<old>" ticked by name will not see "<new>" until you press Give them access to the renamed tile. Rename?"
- **Real HTTP refusals.** The station page handles 429 and 503 answers from like, lyrics, avatar and request routes:
  "Try again in N s" from `Retry-After`; the request text stays in the box; busy states are released; a station or
  user change in between does not apply the old answer. A song request POST whose outcome is uncertain is never
  re-sent automatically. Harness tests for each.

- **Code accepted 2026-10-30 (MI); host check passed (below).** Built in e046598, fd37d42 and 76656ce. The separate RA
  review found no Blocker or Required issue, and it confirmed against the v12.4 sources:
  - jellyfin-apiclient 1.11.0 rejects with the fetch Response, and never re-sends a POST;
  - jellyfin-web's request-fail handler ignores 429 and 503;
  - Jellyfin 12.4's CORS exposes no headers, so `Retry-After` is read only same-origin, which is where ui.js runs.

  The fix round (cdbad23, dbd82f2, b37df5c) took its suggestions:
  - a refused portrait hides the previous DJ's image;
  - joined and acronym secret names are caught (apikey, APIKey, sessionid, token2 …);
  - polled retries back off by doubling up to 60 s;
  - the heart's notice is also a visible line;
  - clearer request failure texts;
  - lyrics say "Asking again in N s.";
  - rename focus;
  - the tile name is compared the way the server reads it;
  - tests that kill three surviving mutants (the fourth, the epoch check in `later()`, is equivalent and kept as
    defence).

  Checks at b37df5c (MI re-run): build 0 warnings; 1395 tests; format ok; configPage 544 and stationPage 557
  passed; `check-manifest.sh` all ok.

- **Host check passed 2026-10-30** (R1, R2a and R2b together; build f4ffda9, dll md5 3241dd9d…, deployed by HI 11:28
  UTC on Jellyfin 12.4.0, web client). Before the deploy, 1 of 10 stations sat on a public id (`live`) and streamed
  with no sign-in (200).
  - One log line for the re-key at startup; no Jellywave WRN or ERR.
  - 0 of 10 stations on a public id; all six public ids answer 404 to a no-sign-in stream.
  - Plugin page (HI): the exposure warning, the secret-parameter warning on Check, rename Cancel keeps the name, a
    no-op Save asks nothing. The save left `Jellyfin.Plugin.Jellywave.xml.previous` beside the file.
  - Station page as testA (HI): header, DJ portrait, heart and lyrics with no "busy" text; a request is taken.
  - 70 lyrics calls as testA: 200 for 60, then 429 from call 61 with `Retry-After` 58.
  - Not testable by hand: a second request at once. The request box stays locked while the first waits for the DJ,
    so the 1-per-minute window has passed when it unlocks. Covered by the stationPage harness; HI chose not to
    chase it.

### R3 CI and release (MI)

- `verify.yml` (`workflow_call`, input `ref`) holds the CI checks; `ci.yml` calls it; `release.yml` resolves the tag
  to a commit SHA once and verifies and builds that same SHA.
- Publication moves into `scripts/publish-release.sh`, run by the publish job:
  - one run at a time (workflow-level `concurrency: release`, `queue: max`, never cancelled);
  - creates the release as a draft, uploads the zip, then publishes (works with immutable releases);
  - when the release already has the zip, downloads it and validates its plugin GUID, version, ABI and file list,
    and uses those bytes (checksum computed from the zip) instead of the new build;
  - when the manifest already has an entry for the version with another checksum, fails;
  - never `--clobber`.
- Tests on this machine against a stand-in `gh` and a local bare git remote: a draft with the asset already there; a
  published release without a manifest entry; a failed manifest push; a re-run after success (zip and entry
  unchanged).
- Dependabot gains a `nuget` entry.

- **Accepted 2026-10-30 (MI).** Built in 486b42f. The separate RA review found two Required issues:
  - the draft was published before the manifest was compared, so a conflicting entry left a public zip that the
    catalog's checksum did not match;
  - a job-level concurrency group without `queue: max` cancels a *waiting* run.

  It also made suggestions:
  - treat only gh's "release not found" as no release;
  - re-upload an unfinished asset on a draft;
  - test the version, ABI, post-check, rebase and retry paths, and failures after gh's side effect;
  - refuse a moved tag;
  - Dependabot should skip the `Jellyfin.*` packages;
  - check the tag's shape.

  Fixed in c0eeb75. Concurrency moved to the workflow level, because GitHub documents `queue` only there. The test has
  79 checks, and 8 deliberate script faults are each caught. The changelog no longer offers 0.4.x (D4). Not proven
  until the first real run: the workflow files on GitHub. Push `main`, watch CI go green, then tag.

### R4 Threat model and DESIGN pass (DA)

- The facts above replace the "raw-API window" row ("tile access hides, does not lock", Jellyfin-wide), and drop the
  claim that copying a block makes Jellyfin enforce it everywhere.
- "Refuses redirects" covers the plugin's own client only. Playback goes through ffmpeg or, for static requests,
  through Jellyfin's own HTTP client; both follow redirects.
- "Does nothing" becomes "is meant to do nothing". The per-listener cost names Jellyfin's per-user limits (exact 12.4
  setting names verified). The user-save race names unrelated settings and permissions.
- The accepted-risk table follows R1, R2a and R2b; the old-catalog line follows D4.

- **Accepted 2026-10-30 (MI)** (66c7a3a, eaa9e3b; fix round a4ec098). Docs only: THREAT-MODEL, DESIGN, one LESSONS
  entry. The separate RA checked every changed claim against Jellyfin v12.4 (83d0d0e), jellyfin-web v12.4 (0a580ce)
  and ffmpeg's `http.c`: CHANGES REQUIRED, one Required item. The docs said the stream routes check no permission. In
  fact, without "Allow audio playback that requires transcoding" a signed-in user's `/Audio/{id}/stream` is forced to
  stream copy (`EncodingHelper.TryStreamCopy`). It is still not a refusal; MI checked the source. The fix round took
  the eight Suggested and Nit items as well:
  - the whole-user save can also undo a password change or a lockout;
  - no cap on streams per user;
  - "Allow media playback" only hides Play in clients;
  - read item ids can be passed on;
  - #5415 is closed as a duplicate;
  - "Phase 5 D3" label;
  - "a completely uploaded zip";
  - "blocked on its current id".

  Verified 12.4 labels (Profile tab): "Allow audio playback that requires transcoding", "Maximum number of
  simultaneous user sessions", "Internet streaming bitrate limit (Mbps)", "Allow media playback". Build ok;
  `check-manifest.sh` ok; no host names.

### Device pass (HI, 2026-10-30, Jellyfin 12.4.0, build f4ffda9)

| Client | Result |
|---|---|
| Android app 2.7.3, Pixel 9 (GrapheneOS) | All features work |
| Android app 2.7.3, Galaxy Tab S6 Lite (Android 14) | Audio plays; the station card did not show at first. Closed, see below: playback, card and Stop work in both orientations |
| Jellyfin Roku 3.1.0, Roku Express (3960X) | Tile, station list and playback work. No station pictures, no station page |
| Chromecast | Deferred: HI has no device at hand. Not tested on 0.5.0.0 |

- **Tablet, open.** Read against jellyfin-web v12.4 and jellyfin-android: the app asks for the mobile layout on phone and
  tablet alike (`getDefaultLayout`), both reach the same `list` route, and `ui.css` hides nothing at tablet widths. So
  no code path differs by device; what can differ is a stale page (no force-close since the plugin changed) or a saved
  layout. HI's next check tells those apart: after a force-close, a Stop button in the playing bar in place of pause
  means `ui.js` runs.
- **Tablet result (HI, same day).** Clear cache (not Clear data) in Android brought the card in: a stale copy in the
  app's WebView, which a force-close does not clear. The server sends `no-cache` and correct ETags, so where the stale
  copy came from is not known. README Troubleshooting has the step. Chrome on the tablet: card correct.
- **Tablet Stop, a bug of ours (L11), fixed.** With the script running the bar showed only Next: pause was gone and
  our Stop did not show. `browser.mobile` makes jellyfin-web `hide` the centre group at every width, and our Stop showed
  only below 56em, so a mobile browser wider than that had neither. Pixel 9: Stop shows in both orientations on the old
  build too, so its landscape width is below 56em (inferred from that result, not measured). A phone wider than 56em in
  landscape would have had the gap. The injected CSS now also shows our Stop when the centre group has `hide`.
- **Accepted 2026-10-30 (MI), cbec939** (build `d3c47fe6…`, deployed 17:46 UTC, checked with `md5sum`). HI on the tablet:
  Stop appears and works in either orientation. A first retest ran on the old build and is not evidence for the fix; it
  matched the old rule exactly (portrait yes, landscape no). The tablet's card in the app was reported once, and HI did
  not report it missing on the final retest, so it counts as a pass. ui.js has not changed since the test.
- **Roku pictures: a client bug, no plugin fix** (from reading the source, not tested on a device). In jellyfin-roku
  3.1.0 and 3.2.4, `LoadItemsTask2.bs` assigns a URL string to `MusicSongData.image`, a `node` field, and `setPoster`
  reads `.url` from it, so an Audio item in a grid gets no poster. This hits every Audio item in a Roku grid, not only
  stations. Making stations another item type would break playback everywhere else. The station page cannot show there
  (no web page), as the README already says. HI: not worth chasing now. The README has it under Known limitations, and
  [ROKU-AUDIO-POSTERS.md](ROKU-AUDIO-POSTERS.md) holds the detail for a possible pull request after the release.

### R5 README (last, DA)

- Order: supported version → install → play a starter → add a station → access for other users (with the warning) →
  optional panel → links to upgrade notes, troubleshooting, advanced settings and security. The 0.4.x upgrade section
  shrinks to a short note.
- "Tested on": one row per plugin/server/client combination, with a date.
- Found in R4:
  - README:27 says older versions stay in the catalog (contradicts D4);
  - README:103–105: a downgrade to 0.4.2.0 is possible only from a kept zip;
  - README:437 "does nothing" → "is meant to do nothing";
  - README:208 and :233, the transcoding setting: not a lock (it forces stream copy on the raw stream route), and
    "Allow media playback" only hides Play.
- Also from earlier cards:
  - the user-visible changes from R2a and R2b (acceptance notes above);
  - stale lines: "The plugin adds no limiter", the favourites line for migrated stations;
  - the manifest header description ("Adds a SUB/WAVE radio station tile…").

- **Accepted 2026-10-30 (MI), except the tablet row** (b5ac5f8, 4fa2dce, 523aed1, cac796c; fix round 5237c42). The
  README is rewritten in the order above; release steps moved to DESIGN Packaging; the changelog is 1567 characters.
  `update-manifest.sh` never touches the manifest header, so the two header fields were set by hand to match
  `build.yaml`; `check-manifest.sh` does not compare them. The separate RA checked the claims, constants and page texts
  against the code: CHANGES REQUIRED, 4 Required, all wording:
  - "on every client" overclaimed;
  - the withdrawal wording was false between the `main` push and the retire step;
  - the redirect fix steps skipped Check again and Apply to list;
  - "closed to the world" contradicted the exposure warning.

  The Suggested and Nit items were taken too. Build ok; `check-manifest.sh` ok; 43 links resolve; no host names.
  The Galaxy Tab row was filled in from HI's result (see Device pass). The full test set is run again before the squash.
- **For after the release:**
  - the log line for a refused logo (`StationLogos.cs:201`) does not name animated images;
  - `configPage.html:97` says "every user who can play the station can read it", which understates "any signed-in
    user" (a page change: both harnesses);
  - `check-manifest.sh` could compare the manifest header with `build.yaml`.

### Release (MI and HI)

1. MI squashes `live-radio` into `main` as one commit and checks that `main`'s history has no host names.
2. HI pushes `main` only (one command from MI).
3. Tag v0.5.0.0; MI checks that the manifest checksum matches the zip.
4. MI retires 0.1.0.0 to 0.4.2.0: removes the catalog entries first, then deletes the releases. Existing installs are
   not touched. Git tags stay.
5. `live-radio` is deleted.

- **Released 2026-10-30.** Squash 3154159 (it adds no host names), CI green, tag v0.5.0.0 on that commit, release run
  green on all four jobs, manifest entry 0028268. The published zip's MD5 equals the live catalog entry
  (`D418703286B9271A35781D9223F4ED21`). The catalog now lists 0.5.0.0 only; the ten older releases are deleted once that
  is live. Git tags stay.
