# Lessons learned

Owned by RA. One short block per accepted card; append, do not rewrite.

## Card 1: scaffold, build, manifest (d7dfc27, 0111528)

- Hand-rolled YAML parsing bites on the first value with `: ` in it. `awk -F': '` split the changelog `Fix: thing` to `Fix` silently. Match on the key prefix, keep a single parser in `scripts/lib.sh`, and add a value with `: ` to the test when touching it. Values are still single-line and must not need escaped quotes.
- A tag-triggered release workflow must build the tagged commit, not `main`. Check out `github.ref`, build and verify there, and only then switch to `origin/main` for the manifest commit. Make release creation and asset upload idempotent so a re-run after a failed manifest push is safe.
- No environment values in source. The station hostname belonged in the config file, not the `PluginConfiguration` constructor; the plan's defaults table said otherwise and the Development SOP won. Update the plan when a SOP overrides it, so the next card does not reintroduce it.
- A `DateTime.UtcNow` default in a config constructor is a hidden cache-buster: the serializer calls the constructor on every load, so an unsaved field changes on every restart. Default to a fixed value (`DateTime.UnixEpoch`) and set the real timestamp on save.
- Jellyfin 12.3 facts worth keeping: the install folder is `<manifest name>_<version>` (`InstallationManager.PerformPackageInstallation`), the MD5 checksum compare is case-insensitive, `sourceUrl` must end in `.zip`, and a local `meta.json` wins over the repository manifest for everything but `version`.
- Consistency scripts should check the built artifacts too, not only the sources. `check-manifest.sh` initially compared five declarations and skipped the one that ships (`dist/.../meta.json` and the zip MD5).
- Empty lookups fail loudly. A missing `build.yaml` key produced `dist/_` paths and a bad `-p:Version=`; `require NAME "$VALUE"` guards cost one line each.
