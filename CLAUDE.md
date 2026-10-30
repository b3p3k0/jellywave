# Jellywave — notes for agents

Read `docs/dev/PLAN.md` (decisions, cards) and `docs/DESIGN.md` (how it works) before changing
anything. `docs/dev/LESSONS_LEARNED.md` records what each card taught; append, do not rewrite.

## Build and verify

```bash
dotnet build Jellyfin.Plugin.Jellywave.slnx -c Release   # warnings are errors (StyleCop, analyzers)
dotnet test Jellyfin.Plugin.Jellywave.Tests -c Release
node Jellyfin.Plugin.Jellywave.Tests/js/configPage.test.js   # config page script against a fake DOM (plain node)
node Jellyfin.Plugin.Jellywave.Tests/js/stationPage.test.js  # station page script (ui.js) against a fake DOM (plain node)
bash Jellyfin.Plugin.Jellywave.Tests/sh/publishRelease.test.sh  # release publishing against a stand-in gh and a local git remote
scripts/build.sh            # dist/Jellywave_<version>/ + zip; manifest.json is owned by the release workflow
scripts/check-manifest.sh   # must print only "ok" lines
```

## Rules that bit us

- `IChannel` must be registered in `PluginServiceRegistrator`; Jellyfin 12 will not find it otherwise.
- Keep static media sources only; do not add `IRequiresMediaInfoCallback` (duplicate sources).
- Bump `MediaSourceSchemaDate` in `JellywaveChannel` whenever the media source shape changes.
- Never write `Artists` on the live item (creates Music-library artists).
- No hostnames, IPs or credentials in tracked files; test-host helpers live in `local/` (gitignored).
- Exception: public internet radio station URLs may be tracked (the starter list in `StarterStations.cs`, its tests
  and the docs that describe it). Test hosts, private or LAN addresses and credentials stay forbidden everywhere.
- One type per file (StyleCop SA1402). Named constants for timeouts and thresholds.
- `build.yaml` values are single-line; `scripts/lib.sh` is the only YAML parser.
- The deploy helper is untracked on purpose; users install through the plugin repository.
- When piping `dotnet build` or `dotnet test` output through `grep`, run with `set -o pipefail`
  (or check the exit code first). A masked build failure got committed once (e8dea54).
- Build every URL that reaches `MediaSourceInfo.Path`/`EncoderPath` with `Uri.AbsoluteUri`, never `ToString()` (ffmpeg `-i "<url>"` quoting).
- Station routes reach a station only through `[StationRoute]` and `CallerStation()`; `StationRouteIlTests` enforces it.
  A new plugin call from the controller or `StationList` needs a line with a reason in `StationRouteAllowList`, and any
  plugin type they hand to the framework must be a listed data type (both walked for configuration sinks, card S-b).
- No station-supplied or probe-supplied value reaches a request without the `StationUrls` rules (redirects, playlist entries, mounts).
- Run both node page harnesses (`configPage.test.js`, `stationPage.test.js`) before every commit that touches the web pages.
