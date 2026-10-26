# Jellywave

SUB/WAVE internet radio as a "My Media" tile in Jellyfin 12.x.

Jellywave is a Jellyfin channel plugin. It registers one channel named after
your [SUB/WAVE](https://github.com/perminder-klair/subwave) station, which
Jellyfin shows beside your libraries and Live TV on every client. Open it,
press play, hear the station.

Status: in development. See `docs/PLAN.md`.

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
- Users see the channel only if their policy allows all channels or lists this one
  (Dashboard → Users → user → Access).

## Known limitations

- **Do not seek.** In Relay mode Jellyfin's audio HLS path has no live mode, so the
  plugin advertises a nominal 6-hour runtime to get a working playlist. The player
  therefore shows a timeline; dragging it restarts the transcode with a seek on a
  live input. Play, pause and stop are fine. After 6 hours press play again.
- Track titles are not shown yet (planned: polling the station's now-playing API).

## Install (once released)

Dashboard → Plugins → Repositories → add
`https://raw.githubusercontent.com/b3p3k0/jellywave/main/manifest.json`,
then install **Jellywave** from the catalog and restart Jellyfin.

## Build

```bash
scripts/build.sh          # dist/Jellywave_<version>/ and .zip, refreshes manifest.json
scripts/check-manifest.sh # GUID and version agree across all declarations
dotnet test               # unit tests (URL handling, config clamps)
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
