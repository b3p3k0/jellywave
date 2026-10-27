# Jellywave: SUB/WAVE radio as a "My Media" entry in Jellyfin 12.x

Status: v1 implemented and accepted (cards 1–4: 3ebcd03, c63aa14, b096685, 9953caf); card 5 closeout in progress. Original plan approved 2026-10-26; sections marked "corrected"/"changed" record what implementation taught. Role at writing: PA. Track: full process.
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

`scripts/build.sh` (run here): `dotnet publish Jellyfin.Plugin.Jellywave -c Release -o dist/stage`, then assemble `dist/Jellywave_<version>/` with `Jellyfin.Plugin.Jellywave.dll`, `meta.json` (generated from `build.yaml` + version + timestamp, same field set as SeerrFin's), `thumb.png`, and zip it as `dist/Jellywave_<version>.zip`. Then runs `update-manifest.sh` so `manifest.json` always matches the last build.

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
| Non-admin users need `EnableAllChannels` or the channel enabled | Suggested | Note in README; check on the Jellyfin test host test users. |
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
| 5 docs, release | docs accepted ec8efde | release half needs HI: GitHub remote, push, tag v0.1.0.0, install from manifest on the test host |

Found during closeout: users need channel access (`EnableAllChannels` or the channel in `EnabledChannels`); 39 of 41 test-host users had neither. The interim bulk script was removed once card 6 (in-plugin grant) landed; HI wanted no scripts or manual passes for end users.

## Post-approval housekeeping (MI)

- Commit this plan into the repo as `docs/PLAN.md` on the first checkpoint commit.
- Save memory notes: the Agent Charter URL and response contract; host roles (the Jellyfin test host = Jellyfin 12.3 test server, user access + sudo password; the station host = SUB/WAVE, `kevin@<station-host>` works, `claude@<station-host>` needs `~/.ssh/claude_framework`).

## Phase 2 candidates (not in this plan)

- Custom in-Jellyfin station page (HI's pick): top-bar tab with a mini-player, cover, now playing, requests; likely via the File Transformation plugin as SeerrFin does. SUB/WAVE sends X-Frame-Options SAMEORIGIN, so no iframe; its API has CORS * so the page can read now-playing directly.
- Plugin-side "make visible to all users" control, and a hook for newly created users (authorization change; HI decision).
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
  tools (JFA-GO and friends) cannot leave new users out.
- HI decisions 2026-10-27: **off by default** (the admin ticks it once; the page explains what it does); turning it off **stops future grants and revokes nothing**.
- The config page shows the result of the last run: "n users can see the station; granted to m just now;
  k users are blocked by an explicit block list".
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
- Tests: pure grant logic (`Granter.Plan(users, channelId)` → list of users to update) with fakes of the
  user preference arrays; no network, no database.

### Cards

6a. `AccessGranter` service + tests + config field + startup and user-created hooks.
6b. Config page section + admin endpoint + README/DESIGN update. Exit: fresh non-admin user on the test
    host sees the tile without any admin action; counts shown on the page; LESSONS block.
