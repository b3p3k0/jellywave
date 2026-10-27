# Jellywave — notes for agents

Read `docs/PLAN.md` (decisions, cards) and `docs/DESIGN.md` (how it works) before changing
anything. `docs/LESSONS_LEARNED.md` records what each card taught; append, do not rewrite.

## Build and verify

```bash
dotnet build Jellyfin.Plugin.Jellywave.slnx -c Release   # warnings are errors (StyleCop, analyzers)
dotnet test Jellyfin.Plugin.Jellywave.Tests -c Release
scripts/build.sh            # dist/Jellywave_<version>/ + zip; manifest.json is owned by the release workflow
scripts/check-manifest.sh   # must print only "ok" lines
```

## Rules that bit us

- `IChannel` must be registered in `PluginServiceRegistrator`; Jellyfin 12 will not find it otherwise.
- Keep static media sources only; do not add `IRequiresMediaInfoCallback` (duplicate sources).
- Bump `MediaSourceSchemaDate` in `JellywaveChannel` whenever the media source shape changes.
- Never write `Artists` on the live item (creates Music-library artists).
- No hostnames, IPs or credentials in tracked files; test-host helpers live in `local/` (gitignored).
- One type per file (StyleCop SA1402). Named constants for timeouts and thresholds.
- `build.yaml` values are single-line; `scripts/lib.sh` is the only YAML parser.
- The deploy helper is untracked on purpose; users install through the plugin repository.
- When piping `dotnet build` or `dotnet test` output through `grep`, run with `set -o pipefail`
  (or check the exit code first). A masked build failure got committed once (e8dea54).
