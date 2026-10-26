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

## License

MIT.
