# Jellywave

SUB/WAVE internet radio as a "My Media" tile in Jellyfin 12.x.

Jellywave is a Jellyfin channel plugin. It registers one channel named after
your [SUB/WAVE](https://github.com/perminder-klair/subwave) station, which
Jellyfin shows beside your libraries and Live TV on every client. Open it,
press play, hear the station.

Status: v0.3.0.0; v1 (stream tile, now playing, visible to every user) plus cover art and the optional station page. See `docs/PLAN.md` and `docs/DESIGN.md`.

## How it works, and what users can see

- **Relay** (default): Jellyfin fetches the station's MP3 stream and transcodes it
  to the client with ffmpeg. Clients only need to reach Jellyfin. Users need the
  "Allow audio playback that requires transcoding" permission (on by default).
- **Direct**: clients fetch the stream URL themselves. Every client must reach the
  station host, or the URL you put in *Client stream URL*.
- In both modes the station URL you configure is visible to any signed-in user
  who opens the item (it is part of Jellyfin's playback info). SUB/WAVE streams
  are public by design, so this is a hostname disclosure, not a credential one.
  Do not point the plugin at a private station until listener auth is supported.
- Jellyfin shows a channel only to users whose policy allows all channels or lists this one
  (Dashboard → Users → user → Access → **Channel access**). Users created from a template
  or by a provisioning tool often have neither. Tick **Make the station visible to every
  user** on the plugin page: the plugin then adds this channel to each user's channel list,
  immediately, at startup, and whenever a user is created or a user's policy is saved
  (creation flows save a policy right after creating the user, which would otherwise undo it). It changes nothing else in a
  policy, respects explicit block lists, and removes nothing when turned off. While it is on,
  un-ticking the channel for one user is undone on the next save; exclude a user by adding the
  channel to that user's block list instead.

## Configuration

Dashboard → Plugins → Jellywave.

| Setting | Meaning |
|---|---|
| Station base URL | SUB/WAVE as reached from the Jellyfin server, e.g. `http://your.host:7700` |
| Fetch from station | Reads the name, tagline, stream path and stream format from the station's public API |
| Tile name | Shown in My Media; renaming creates a new channel entry |
| Description | Optional channel description; "Fetch from station" fills it with the on-air DJ's tagline |
| Stream path | Mount under the base URL, normally `/stream.mp3` |
| Delivery | Relay through Jellyfin (default) or Direct |
| Client stream URL | Direct only: the URL clients use when it differs from the server's |
| Bitrate, sample rate, channels | How the stream is described to clients (not what ffmpeg produces); filled by "Fetch from station", defaults 192 kbit/s, 44100 Hz, stereo |
| Now playing | Poll the station and show the current track, DJ, show and what is up next on the live item |
| Show cover art | On by default. The live item's image follows the current track (the station logo when a track has no art) |
| Station page | Optional, needs the File Transformation plugin (see below). The channel's own page gets a live header with cover, track, DJ, show, what's next, listeners, Play/Stop and a request box; the playing bar and the grid card follow the track while it plays |
| Make the station visible to every user | Off by default. Adds this channel to every user's channel list; "Run now" shows the counts |

After saving, the tile updates on the next reload. If you renamed it, run the
"Refresh Internet Channels" scheduled task to drop the old entry.

## Known limitations

- **Do not seek.** In Relay mode Jellyfin's audio HLS path has no live mode, so the
  plugin advertises a nominal 6-hour runtime to get a working playlist. The player
  therefore shows a timeline; dragging it restarts the transcode with a seek on a
  live input. Play, pause and stop are fine. After 6 hours press play again.
- Track titles appear in the item's description and refresh when clients reload the list or start playback; the playing bar does not update mid-track.

## Install (once released)

Dashboard → Plugins → Repositories → add
`https://raw.githubusercontent.com/b3p3k0/jellywave/main/manifest.json`,
then install **Jellywave** from the catalog and restart Jellyfin.

## Station page (optional)

Jellyfin's web client has no hook for plugin pages that ordinary users can reach, so the station
page is injected into the web client by the third-party
[File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin
(repository `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`). Install it, restart, and
the plugin page's "Station page" section reads "Active". Without it everything else works; only the
page is missing. What users get, in the web client and the Android app (which wraps it):

- The channel's library page (the tile, or the top-bar entry in the default layout, or the drawer
  entry in the legacy layout) shows a header: cover, track, DJ with tagline, show, what's next,
  listener count, Play and Stop through Jellyfin's own player.
- While the station plays, the playing bar's thumbnail and text, the item's card in the grid and the
  browser's media session follow the track. Jellyfin itself copies the item when playback starts and
  never re-reads it; the page repaints those spots every five seconds.
- A request box: text and a name (prefilled with the Jellyfin user name), sent to the station's DJ.
  The station's own limits apply, a short wait between requests and a few per hour, and everyone on
  the Jellyfin server counts as one caller to the station. The page explains that under the box.

The page talks only to the Jellyfin server (`/Jellywave/Feed`, `/Jellywave/Request`), never to the
station, so the station can be closed to the world while Jellyfin users keep access.

## Tested on

| Client | Result | Notes |
|---|---|---|
| Web, Firefox and Chrome on Ubuntu 26.04 (Intel and AMD) | works | tile, playback, cover and now-playing rotation, station page |
| Jellyfin Android 2.7.3 on a Pixel 9 (GrapheneOS) | works | same as web (the app wraps the web client) |
| macOS, iOS, Windows, Roku, Android TV | untested | reports welcome |

## Build

```bash
scripts/build.sh          # dist/Jellywave_<version>/ and .zip (manifest.json is written by the release workflow)
scripts/check-manifest.sh # GUID and version agree across all declarations
dotnet test               # unit tests (URLs, data version, now-playing text, feed fixtures)
```

Requires the .NET 10 SDK, `jq`, and `zip`.

## Release

1. Bump `<Version>` in `Directory.Build.props` and the `changelog` line in `build.yaml`.
2. Commit, then tag that commit `v<version>` (for example `v0.1.0.0`) and push the tag.
3. The `Release` workflow builds the tagged commit, attaches `dist/Jellywave_<version>.zip`
   to a GitHub release, and commits the updated `manifest.json` to `main`.

The workflow fails on purpose if the tag version does not match `Directory.Build.props`.

## License

MIT.
