# Jellywave: SUB/WAVE radio as a "My Media" entry in Jellyfin 12.x

Status: plan for HI approval. Role: PA. Track: full process (new public interface, multi-module).
Research performed 2026-10-26 against Jellyfin v12.3 source, jellyfin-web, jellyfin-roku, jellyfin-androidtv, the plugin template, the SUB/WAVE repo, and live probes of nordflix and mimir.

## Context

Kevin runs Jellyfin 12.3.0 on host `nordflix` and a SUB/WAVE internet-radio station ("IHateRadio") on host `mimir`. Goal: a tile in Jellyfin's My Media row, alongside Live TV, that plays the station with transport controls on web, Android, and (bonus) Roku / Android TV. Stream audio is the must-have; the SUB/WAVE web player is a later phase.

Decisions already made by HI (2026-10-26):

| Decision | Choice |
|---|---|
| Network | A public domain fronts Jellyfin; some clients cannot reach mimir. Default delivery = relay through Jellyfin, with Direct as a config option. |
| v1 scope | Stream tile only. Embedded web player page is phase 2. |
| Repo / name | `~/DEV/jellywave`, plugin "Jellywave", assembly `Jellyfin.Plugin.Jellywave`. `git init` now, commits on `main`, HI owns push and any GitHub remote. |
| Now playing | Best effort in v1: poll the station and update the item's metadata. |

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
- nordflix: Jellyfin 12.3.0 as systemd service, plugins in `/var/lib/jellyfin/plugins/<Name>_<ver>/` owned `jellyfin:jellyfin`, `kevin` has sudo with password, ffmpeg at `/usr/lib/jellyfin-ffmpeg/ffmpeg`. Existing `meta.json` shape: see SeerrFin's (`targetAbi` "12.0.0.0", `assemblies` list, `status` Active).
- mimir SUB/WAVE 1.26.0: station "IHateRadio", public (no listener auth, no private player), MP3 192 kbps 44.1 kHz stereo at `http://mimir.banjo-tiaki.ts.net:7700/stream.mp3` (LAN `192.168.1.242`), CORS `*`, ICY headers present. Public JSON: `GET /api/now-playing` (`nowPlaying.title/artist/album/subsonic_id/duration`, `context.activeShow.name`), `GET /api/state` (`station.name`, `privacy`), `GET /api/health` (`{"status":"on-air"}`). Tune-in files at `/listen.pls`, `/listen.m3u`.
- Access: `kevin@mimir` works (docker group). `claude@mimir` needs `~/.ssh/claude_framework`, absent on this machine. Not needed for v1; SUB/WAVE is not modified.

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
│   ├── Services/StationClient.cs          # typed HTTP client for /api/state, /api/now-playing, /api/health
│   ├── Services/NowPlayingService.cs      # IHostedService poller
│   └── Assets/logo.png                    # embedded; channel Primary image (temporary mark; swap for station art later)
├── scripts/
│   ├── build.sh                           # dotnet publish -c Release → dist/Jellywave_<ver>/{dll, meta.json, thumb.png} + zip
│   ├── update-manifest.sh                 # writes the version entry (checksum, sourceUrl, timestamp) into manifest.json
│   └── deploy-nordflix.sh                 # HI runs with sudo on nordflix: backup, install, restart, verify (dev path)
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
| `NowPlayingPollSeconds` | `20` | Clamp 5..300. |
| `LastChangedUtc` | Unix epoch until first save | Drives `ChannelItemInfo.DateModified` and `DataVersion`, so config edits re-save media sources and bust the 3 h cache. |

### `JellywaveChannel : IChannel`

- `Name` = `StationName`; `Description` = station tagline or "Live radio from SUB/WAVE"; `DataVersion` = short hash of (base URL, stream path, client URL, delivery, name, LastChangedUtc); `HomePageUrl` = base URL + `/listen`; `ParentalRating` General.
- `GetChannelFeatures`: `MediaTypes=[Audio]`, `ContentTypes=[Song]`, `MaxPageSize=10`, `AutoRefreshLevels=1`.
- `IsEnabledFor` returns true. (Per-user hiding stays with Jellyfin's own channel permissions.)
- `GetChannelItems`: ignores folder id; returns exactly one `ChannelItemInfo`:
  - `Id="live"`, `Type=Media`, `MediaType=Audio`, `ContentType=Song`, `IsLiveStream=true`
  - `Name=$"{StationName} — Live"`, `Overview` = current now-playing line if the poller has one, `Artists`/`AlbumArtists` likewise
  - `DateModified=LastChangedUtc`, `ImageUrl` null (inherits channel image)
  - one `MediaSourceInfo`: `Path`=stream URL (base+path, or `ClientStreamUrl` in Direct mode), `Protocol=Http`, `IsRemote=true`, `IsInfiniteStream=true`, `Container="mp3"`, `Bitrate=192000`, `SupportsDirectPlay=(Delivery==Direct)`, `SupportsDirectStream=false`, `SupportsTranscoding=true`, `SupportsProbing=false`, `RunTimeTicks=null`, `MediaStreams=[audio: Codec "mp3", Channels 2, SampleRate 44100, BitRate 192000, Index 0]`. Bitrate and sample rate come from `/api/state` if present, else these defaults.
- `GetChannelImage(Primary)`: returns the embedded PNG as a `DynamicImageResponse` (`HasImage=true`, `Format=Png`, `Stream`). `GetSupportedChannelImages` = `[Primary]`.

### `NowPlayingService : IHostedService`

- Registered by `PluginServiceRegistrator` (`serviceCollection.AddHostedService<NowPlayingService>()`), uses `IHttpClientFactory`, `IChannelManager`, `ILibraryManager`, `ILogger`.
- Loop: every `NowPlayingPollSeconds`, GET `/api/now-playing`. Keep the last snapshot in a static accessor the channel reads.
- On track change: locate the Audio item with `_libraryManager.GetItemList(new InternalItemsQuery { ChannelIds=[channel.Id], ExternalId="live", IncludeItemTypes=[BaseItemKind.Audio] })`, where `channel` comes from `IChannelManager.GetChannelsInternalAsync(new ChannelQuery())` filtered by `Name`. Set `Overview = "Now playing: {artist} – {title} ({album})"`, `Artists=[artist]`, `AlbumArtists=[artist]`, `Album=title` is NOT set (keep Name stable; Album stays empty). Persist with `UpdateItemAsync(item, parent, ItemUpdateType.MetadataEdit, ct)`.
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
- Until the first push, nordflix cannot fetch the manifest, so the dev path below stays. Once `main` is on GitHub, the acceptance test for card 5 is installing Jellywave on nordflix from the repository URL in the dashboard.

### Deployment

`scripts/build.sh` (run here): `dotnet publish Jellyfin.Plugin.Jellywave -c Release -o dist/stage`, then assemble `dist/Jellywave_<version>/` with `Jellyfin.Plugin.Jellywave.dll`, `meta.json` (generated from `build.yaml` + version + timestamp, same field set as SeerrFin's), `thumb.png`, and zip it as `dist/Jellywave_<version>.zip`. Then runs `update-manifest.sh` so `manifest.json` always matches the last build.

`scripts/deploy-nordflix.sh` (HI runs on nordflix): modeled on `~/DEV/seerrfin-teststack/deploy/setup.sh`. Steps: confirm systemd Jellyfin is active; show what it found; `sudo systemctl stop jellyfin`; back up any existing `Jellywave_*` folder to `~/jellywave-plugin-backup-<ts>.tar.gz`; remove old `Jellywave_*`; copy the staged folder in; `chown -R jellyfin:jellyfin`; start; tail the log for `Jellywave` and `Loaded plugin`. `--rollback` restores the latest backup. MI copies `dist/Jellywave_<ver>/` and the script to `nordflix:~/jellywave-dist/` with `scp`; HI runs `bash ~/jellywave-dist/deploy-nordflix.sh`.

## Task cards (DA implements one at a time; RA accepts each)

1. **Scaffold + build + manifest.** Copy the template, rename to Jellywave, bump to net10.0 / 12.3.0, new GUID, `git init`, first `wip:` commit. Add `manifest.json`, `build.yaml`, `scripts/build.sh`, `scripts/update-manifest.sh`, `scripts/check-manifest.sh`, and `.github/workflows/release.yml`. Exit: `dotnet build -c Release` clean with warnings-as-errors; `scripts/build.sh` produces `dist/Jellywave_0.1.0.0/` and `dist/Jellywave_0.1.0.0.zip`; `manifest.json` carries the zip's MD5; `check-manifest.sh` passes.
2. **Channel + stream.** `JellywaveChannel`, embedded logo, config class with defaults (no page yet). Exit: deployed on nordflix; `GET /Channels` lists it; `GET /UserViews` includes it; web My Media shows the tile with logo; click → one item → play → audio in browser, Relay mode (ffmpeg job visible in dashboard / log).
3. **Config page.** `configPage.html` with the fields above and "Fetch from station"; `LastChangedUtc` on save. Exit: switching Delivery to Direct and back changes `PlaybackInfo` output (`SupportsDirectPlay`, `TranscodingUrl`) after cache bust.
4. **Now-playing service.** `StationClient`, `NowPlayingService`, registrator. Exit: after a track change on mimir, reopening the tile shows the new "Now playing" overview within one poll interval; log shows no errors over 30 min.
5. **Deploy script, docs, closeout.** `deploy-nordflix.sh` with rollback, `README.md` (including the repository URL and install steps), `docs/DESIGN.md`, `docs/PLAN.md`. Exit: HI runs the script end to end on nordflix; docs match behaviour. Once HI has pushed `main` and tagged `v0.1.0.0`: the release workflow succeeds, and installing from the repository URL in the nordflix dashboard works (this last check is after push, so it closes when HI reports it).

Cross-client checks after card 4 (HI performs, MI supplies the checklist): Android app (web wrapper) plays; Roku and Android TV open the tile and play. Roku/Android TV are bonus and may need a follow-up card.

## Verification (end to end)

```bash
# build
cd ~/DEV/jellywave && scripts/build.sh && ls dist/Jellywave_0.1.0.0/
# ship
scp -r dist/Jellywave_0.1.0.0 scripts/deploy-nordflix.sh nordflix:~/jellywave-dist/
# HI on nordflix
bash ~/jellywave-dist/deploy-nordflix.sh
# API checks (token from a browser session or an API key HI creates)
curl -s -H "Authorization: MediaBrowser Token=$TOKEN" http://nordflix:8096/Channels | jq '.Items[].Name'
curl -s -H "Authorization: MediaBrowser Token=$TOKEN" "http://nordflix:8096/UserViews?userId=$USERID" | jq '.Items[] | {Name, Type}'
curl -s -H "Authorization: MediaBrowser Token=$TOKEN" "http://nordflix:8096/Channels/$CHANNELID/Items?userId=$USERID" | jq '.Items[] | {Name, Type, RunTimeTicks, Overview}'
# playback info shows Relay (no SupportsDirectPlay, has TranscodingUrl)
curl -s -X POST -H "Authorization: MediaBrowser Token=$TOKEN" -H 'Content-Type: application/json' \
  "http://nordflix:8096/Items/$ITEMID/PlaybackInfo?userId=$USERID" -d '{}' | jq '.MediaSources[] | {Path, SupportsDirectPlay, SupportsTranscoding, TranscodingUrl, IsInfiniteStream}'
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
| Non-admin users need `EnableAllChannels` or the channel enabled | Suggested | Note in README; check on nordflix test users. |
| Private station mode (listener auth) on SUB/WAVE | Out of scope v1 | Would need `?auth=` on the stream URL and a secret in config → Hard Stop review when it comes. |

## Hard Stops in this wave

None expected in code. Restarting Jellyfin on nordflix is done by HI via the script. No auth, schema, or credential changes. `git push`, GitHub remote creation, and tagging the first release remain HI's; the manifest's `sourceUrl` and `imageUrl` assume the slug `b3p3k0/jellywave` until HI says otherwise.

## Post-approval housekeeping (MI)

- Commit this plan into the repo as `docs/PLAN.md` on the first checkpoint commit.
- Save memory notes: the Agent Charter URL and response contract; host roles (nordflix = Jellyfin 12.3 test server, user access + sudo password; mimir = SUB/WAVE, `kevin@mimir` works, `claude@mimir` needs `~/.ssh/claude_framework`).

## Phase 2 candidates (not in this plan)

- Embedded SUB/WAVE web player page served by the plugin, linked from a top-bar tab (web/Android).
- Plugin-hosted byte-for-byte stream proxy endpoint (keeps ICY, avoids ffmpeg).
- Multi-station support (SUB/WAVE profiles) as multiple channels.
