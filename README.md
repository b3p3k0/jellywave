# Jellywave

SUB/WAVE internet radio as a "My Media" tile in Jellyfin 12.x.

Jellywave is a Jellyfin channel plugin. It registers one channel named after
your [SUB/WAVE](https://github.com/perminder-klair/subwave) station, which
Jellyfin shows beside your libraries and Live TV on every client. Open it,
press play, hear the station.

Status: in development. See `docs/PLAN.md`.

## Install (once released)

Dashboard → Plugins → Repositories → add
`https://raw.githubusercontent.com/b3p3k0/jellywave/main/manifest.json`,
then install **Jellywave** from the catalog and restart Jellyfin.

## Build

```bash
scripts/build.sh          # dist/Jellywave_<version>/ and .zip, refreshes manifest.json
scripts/check-manifest.sh # GUID and version agree across all declarations
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
