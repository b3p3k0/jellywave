# Jellywave

SUB/WAVE internet radio as a "My Media" tile in Jellyfin 12.3.

Jellywave is a Jellyfin channel plugin. It registers one channel named after
your [SUB/WAVE](https://github.com/perminder-klair/subwave) station, which
Jellyfin shows beside your libraries and Live TV on every client. Open it,
press play, hear the station.

Status: 0.4.0.0 (the security and integrity hardening of wave 3) requires Jellyfin 12.3; 0.3.4.0 and
older claimed 12.0. Stream tile, now playing, cover art, optional visibility for every user, and the optional
station page with its extras. [`docs/DESIGN.md`](docs/DESIGN.md) explains how it works,
[`SECURITY.md`](SECURITY.md) how to report a vulnerability, and
[`docs/THREAT-MODEL.md`](docs/THREAT-MODEL.md) what the plugin trusts. The plan and lessons for
contributors live in [`docs/dev/`](docs/dev/PLAN.md).

## Support matrix

| Component | Status | Notes |
|---|---|---|
| Jellyfin 12.3.x | supported | `targetAbi` is 12.3.0.0 from 0.4.0.0 on |
| Jellyfin 12.0–12.2 | not supported | The catalog still offers these servers 0.3.4.0, which gets no fixes |
| [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) 3.0.x | optional; needed only for the station page | Works when installed: tested with 3.0.0.0 on Jellyfin 12.3. As of 2026-10-27 its catalog offers nothing to 12.3 servers (the newest catalog entry targets 12.2.0.0), so a fresh 12.3 server cannot get it from the catalog: with a 12.3 user agent the catalog returns an empty list for every plugin. Upstream: [issue #87](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/issues/87) (filed for 12.2; 12.3 is reported in its comments), [releases](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/releases). Installing a release zip by hand is untested here |
| Clients | see [Tested on](#tested-on) | Web and the Android app tested; the rest untested |

## How it works, and what users can see

- **Relay** (default): Jellyfin fetches the station's MP3 stream and transcodes it
  to the client with ffmpeg. Clients only need to reach Jellyfin. Users need the
  "Allow audio playback that requires transcoding" permission (on by default).
- **Direct**: clients fetch the stream URL themselves. Every client must reach the
  station host, or the URL you put in *Client stream URL*.
- In both modes the station URL you configure is part of the live item's playback info, which
  Jellyfin returns to any signed-in user who can see the channel and to any API key. SUB/WAVE streams
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
| Station base URL | SUB/WAVE as reached from the Jellyfin server, e.g. `http://your.host:7700`. No credentials, query or fragment (see below) |
| Fetch from station | Reads the name, tagline, stream path and stream format from the station's public API |
| Tile name | Shown in My Media. Pick a name no other channel plugin uses; renaming creates a new channel (see below) |
| Description | Optional channel description; "Fetch from station" fills it with the on-air DJ's tagline |
| Stream path | Mount under the base URL, normally `/stream.mp3` |
| Delivery | Relay through Jellyfin (default) or Direct |
| Client stream URL | Direct only: the URL clients use when it differs from the server's |
| Bitrate, sample rate, channels | How the stream is described to clients (not what ffmpeg produces); filled by "Fetch from station", defaults 192 kbit/s, 44100 Hz, stereo |
| Now playing | Poll the station and show the current track, DJ, show and what is up next on the live item |
| Poller status | Read-only line under Now playing: station reachable, last success, last error, what the live item carries. "Refresh status" re-reads it |
| Show cover art | On by default. The live item's image follows the current track (the station logo when a track has no art) |
| Station page | Optional, needs the File Transformation plugin (see below). The channel's own page gets a live header with cover, track, DJ, show, what's next, listeners, Play/Stop and a request box; in the web client and the Android app the playing bar and the grid card follow the track while it plays |
| Make the station visible to every user | Off by default. Adds this channel to every user's channel list; "Run now" shows the counts |

The station base URL must not contain credentials (`user:password@`), a query (`?…`) or a
fragment (`#…`). Such a value is not used: the plugin treats the station as not configured. The plugin page warns "station URL is set but not allowed",
with the reason, on load and after saving. The plugin's own requests to the station never follow
a redirect (the audio stream is fetched by ffmpeg or the client, see
[`docs/THREAT-MODEL.md`](docs/THREAT-MODEL.md)). If the URL redirects (for example http to https, or to a new host), "Fetch from station"
says "the station redirected to …; use that address instead". Enter that address. Plain `http://`
is fine. A *Client stream URL* must not contain credentials either; it may name any host.

After saving, the tile updates on the next reload. If you renamed it, run the
"Refresh Internet Channels" scheduled task to drop the old entry.

**Pick a unique tile name.** Jellyfin derives a channel's id from its name, and its plugin API offers
no other identity, so the plugin cannot make the id collision-proof: two channel plugins with the same
name share one id. Whether names that differ only in case collide follows the server's item-id
setting (Jellyfin 12.3 keeps case by default). When the name collides with another plugin's channel,
Jellywave does nothing with it: the live item is not updated, no access is granted, the station page
answers 404, and the plugin page's status line says "channel name collides with another plugin's
channel". Change the tile name to fix it.

**Renaming changes the channel id.** Access given to the old id does not carry over. With "Make the
station visible to every user" on, the pass after saving grants the new id; otherwise allow the new
channel again under Dashboard → Users → user → Access. A block list that names the old channel does not
cover the new one either, so check users with an explicit block list after a rename.

## Known limitations

- **Do not seek.** In Relay mode Jellyfin's audio HLS path has no live mode, so the
  plugin advertises a nominal 6-hour runtime to get a working playlist. The player
  therefore shows a timeline; dragging it restarts the transcode with a seek on a
  live input. Play, pause and stop are fine. After 6 hours press play again.
- Standard clients do not update the playing bar mid-track. The track is in the live item's
  description and cover, which a client shows when it reloads the list or starts playback; the
  playing bar keeps what it copied at playback start. With the [station page](#station-page-optional)
  installed, the web client and the Android app repaint the bar every five seconds; Roku, Android
  TV and other native clients still do not.

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
page is missing. See the [support matrix](#support-matrix): as of 2026-10-27 the File Transformation
catalog does not offer it to 12.3 servers. What users who can see the channel get (see
[Who can use the station page](#who-can-use-the-station-page)), in the web client and the Android app
(which wraps it):

- The channel's library page (the tile, or the top-bar entry in the default layout, or the drawer
  entry in the legacy layout) shows a header: cover, track, DJ with tagline, show, what's next,
  listener count, Play and Stop through Jellyfin's own player.
- While the station plays, the playing bar's thumbnail and text, the item's card in the grid and the
  browser's media session follow the track. Jellyfin itself copies the item when playback starts and
  never re-reads it; the page repaints those spots every five seconds.
- The DJ's portrait, what is up next, a collapsible list of recently played tracks (with who
  requested them), a like button with the station's count, lyrics when the station has them
  switched on, and today's programme in the station's time zone with the current show marked.
- A request box: text and a name, sent to the station's DJ. See [Requests](#requests).

The page talks only to the Jellyfin server (`/Jellywave/Feed`, `/Jellywave/Request`,
`/Jellywave/Like`, `/Jellywave/Lyrics`, `/Jellywave/Schedule`, `/Jellywave/Avatar/{id}`), never to the
station, so in Relay mode the station can be closed to the world while Jellyfin users keep access. Likes and
lyrics follow the station's own switches; the station counts this server as one caller, so "liked"
is shared by everyone on it.

### Requests

- **One caller to the station.** The plugin sends every request from the Jellyfin server, so the
  station sees all Jellyfin users as one caller with one set of limits: a short wait between
  requests, one pending at a time and a few per hour for everyone together. A "try again later" can
  be about someone else's request. The page says so under the box. The plugin adds no limiter of
  its own.
- **The name is free text.** It is prefilled with the Jellyfin user name, but the listener can type
  anything; the plugin collapses its whitespace, cuts it at 40 characters and passes it to the
  station's DJ otherwise unchanged.
  Nothing checks it against the Jellyfin account.
- **The server knows who sent it.** Jellyfin keeps the real user id with each accepted request: in
  the receipt that lets only that user follow the request, and in one Information log line (request
  id and user id; never the name or the text). That is the accounting trail if a name is abused.

### Who can use the station page

Signed-in users who can see the channel in Jellyfin: the same rule Jellyfin uses for the tile (the
channel is allowed under Dashboard → Users → user → Access, or the user has a block list that does not
name it, and parental controls pass). Administrators get no exception. A signed-in user who cannot
see the channel, and every API key, gets 403 from the station routes (`/Jellywave/Feed`, `Request`,
`Like`, `Lyrics`, `Schedule`, `Avatar`; callers that are not signed in get Jellyfin's own 401). The
page then shows nothing (no header, no drawer link) and asks again only every five minutes. Until
Jellyfin has created the channel, and while the tile name collides with another plugin's channel
(see [Configuration](#configuration)), the routes answer 404. A station URL the plugin refuses counts
as no station: the page has nothing live to show and requests answer "The station is not
configured." A listener request can be followed only by the user who sent it; after a Jellyfin
restart the page says the station lost track of a pending request.

## Tested on

| Client | Result | Notes |
|---|---|---|
| Web, Firefox and Chrome on Ubuntu 26.04 (Intel and AMD) | works | tile, playback, cover and now-playing rotation, station page |
| Jellyfin Android 2.7.3 on a Pixel 9 (GrapheneOS) | works | same as web (the app wraps the web client) |
| macOS, iOS, Windows, Roku, Android TV | untested | reports welcome |

## Build

```bash
scripts/build.sh          # dist/Jellywave_<version>/ and .zip (manifest.json is written by the release workflow)
scripts/check-manifest.sh # GUID, version and targetAbi agree across all declarations, and targetAbi
                          # names the Jellyfin packages the plugin is compiled against
dotnet test               # unit tests (URLs, data version, now-playing text, feed fixtures)
```

Requires the .NET 10 SDK, `jq`, and `zip`.

## Release

1. Bump `<Version>` in `Directory.Build.props` and the `changelog` line in `build.yaml`.
2. Commit, then tag that commit `v<version>` (for example `v0.1.0.0`) and push the tag.
3. The `Release` workflow builds the tagged commit, attaches `dist/Jellywave_<version>.zip`
   to a GitHub release, and commits the updated `manifest.json` to `main`.

The workflow fails on purpose if the tag version does not match `Directory.Build.props`. It runs as
two jobs: `build` (tests, analyzers and packaging with a read-only token and no stored credentials)
hands the zip to `publish`, the only job allowed to write to the repository. The `CI` workflow runs
build, tests, formatting, `node --check`, `bash -n` and `check-manifest.sh` on every push and pull
request. Actions are pinned to commit SHAs; Dependabot
proposes updates.

## Security

Report vulnerabilities privately, as described in [`SECURITY.md`](SECURITY.md). What the plugin
trusts and which risks are accepted is in [`docs/THREAT-MODEL.md`](docs/THREAT-MODEL.md).

## License

MIT.
