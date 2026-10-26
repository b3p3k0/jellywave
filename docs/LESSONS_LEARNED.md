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

## Card 2: channel and stream (5d64cb3, 1cf1837, f428331, b9117ef, 6d3afaf, eab1af0)

- Jellyfin 10.9+ does not discover `IChannel` exports by reflection; 10.8's `AddParts(GetExports<IChannel>())` is gone and `ChannelManager` takes `IEnumerable<IChannel>` from DI. A channel plugin must register itself in its `IPluginServiceRegistrator` (`AddSingleton<IChannel, …>()`) or it never exists. Verify "the server picks this up" claims by finding the registration line, not the consumer.
- Jellyfin's audio HLS path has no live mode: a media source with `RunTimeTicks == null` makes `DynamicHlsHelper` emit `Audio/{id}/live.m3u8`, a route that only exists under `Videos/`. Advertise a nominal runtime on the media source (the item keeps `IsLiveStream`) and document "do not seek".
- jellyfin-web plays audio through `Audio/{id}/universal` with `mediaSource.StreamUrl`, not through PlaybackInfo's `TranscodingUrl`. A PlaybackInfo trace is necessary but not sufficient evidence for audio; follow the universal endpoint's branch too.
- Jellyfin only re-saves a channel item's static media sources when `DateModified` advances. Carry a schema date in the plugin and use `max(configChanged, schemaDate)` for `DateModified` and `DataVersion`, so a rebuilt plugin replaces stale sources without an admin re-save.
- Scrubbing hostnames with search/replace rewrote file names and command lines in the plan (`deploy-nordflix.sh` became a path with spaces). Re-read every changed line after a mechanical replace; grep for the old token proves absence, not correctness.
- Server-side URLs in `MediaSourceInfo.Path` and `EncoderPath` are returned to every user who can play the item; Jellyfin strips neither. Decide and document the exposure up front rather than relying on a "client URL" field to hide it.
