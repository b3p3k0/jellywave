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
- Scrubbing hostnames with search/replace rewrote file names and command lines in the plan (`deploy-<jellyfin-host>.sh` became a path with spaces). Re-read every changed line after a mechanical replace; grep for the old token proves absence, not correctness.
- Server-side URLs in `MediaSourceInfo.Path` and `EncoderPath` are returned to every user who can play the item; Jellyfin strips neither. Decide and document the exposure up front rather than relying on a "client URL" field to hide it.

## Card 3: configuration page and station probe (44e678f, 3bca9aa, e8dea54, d4d8cd6)

- `HttpClient.Timeout` only bounds the request up to the headers when the call uses `HttpCompletionOption.ResponseHeadersRead`; the timeout token source is disposed when `SendAsync` returns, so a body read afterwards can hang on a slow peer. For small JSON feeds use the default `ResponseContentRead` plus `MaxResponseContentBufferSize`, which makes one timeout cover the body and rejects oversized answers before buffering.
- Build and test steps piped through `grep` (or anything else) need `set -o pipefail` or an explicit exit-code check; `dotnet build | grep` reported success for a build that failed on a StyleCop error, and e8dea54 was committed broken.
- Check framework behaviour against the installed runtime, not memory: .NET 10's `ReadFromJsonAsync` no longer validates `Content-Type` (`strings System.Net.Http.Json.dll` shows no `ValidateContent`), so an HTML 200 surfaces as `JsonException`, not `NotSupportedException`. Catch filters that enumerate exception types should be widened to the whole family the call can throw.
- Jellyfin 12.3 facts worth keeping: plugin assemblies containing a `ControllerBase` become MVC application parts automatically (`ApplicationHost.GetApiPluginAssemblies`); `Policies.RequiresElevation` is the `Administrator` role claim and API keys carry it; API output is PascalCase, omits nulls, and converts enums to strings in both directions, so a `<select>` whose option values are the enum names round-trips with no page-side mapping.
- An admin-only probe endpoint may fetch any URL the server can reach; that is acceptable when the admin already controls the same URL through configuration, but the response to the browser should be a category ("timed out", "HTTP 403") with the exception text only in the server log.
- Adding configuration fields is a docs change in the same wave: the PLAN configuration table and the field-source notes drifted in the first cut and were caught in review, not by the implementer's self-review.
- When re-verifying with `--no-build`, compare the binary timestamps with the commit time; the test assembly on disk may come from a later tree than the commit under review.

## Card 4: now-playing poller (a99daf3, 56028f5, 4c165e6)

- `LibraryManager.UpdateItemsAsync` does not raise `ItemUpdated` for items whose `SourceType` is `Channel` ("live tv guide … too much noise"), and `LibraryChangedNotifier.FilterItem` would drop them anyway. A channel plugin cannot push metadata changes to clients; write the row and document "shows on next list load".
- A 200 with a non-JSON body (proxy or maintenance page) is a station failure, not a bug. Which exception `ReadFromJsonAsync` throws depends on the runtime: .NET 10 no longer validates `Content-Type` and surfaces `JsonException` (card 3 lesson), older runtimes throw `NotSupportedException`. Catch the whole family on the "station failed" path, or the loop logs an Error every poll.
- A failure filter on a polling loop must exclude the loop's own stopping token: `HttpClient` surfaces cancellation as `TaskCanceledException`, which otherwise logs a spurious warning and attempts a DB write during shutdown.
- Do not write `Artists`/`AlbumArtists` on channel `Audio` items. `ArtistsValidator` builds Music-library artists from `GetAllArtistNames()`, which has no channel filter; every played track would become an artist entry.
- `IChannelManager.GetChannelsInternalAsync` creates channel entities on demand (`CreateItem` + `RefreshMetadata`) for every registered channel. A hosted service that needs its channel entity should query `GetItemList{IncludeItemTypes=[Channel]}` and wait for `IServerApplicationHost.CoreStartupHasCompleted`; hosted services start before `RunStartupTasksAsync`.
- "Off air wins over the track" is only safe when the online flag is known to deserialize; a non-nullable bool on a mis-keyed field silently hides every track. Verify feed key names against a real response before gating on a boolean (now: nullable flag plus a fixture test of a real-shaped feed, using the same serializer options as `StationClient`).

## Card 5: docs, release, closeout (cbb1113, 8f3b1a9, cce884c, 21080b2, abc1017, f5950c8)

- Channel visibility is per-user policy (`Channel.IsVisible`: `EnableAllChannels` or the channel in `EnabledChannels`, no administrator exemption). Users created by templates or provisioning tools often have neither, so a channel plugin that "works for the admin" may be invisible to everyone else. Check a non-admin user before calling a tile done, and ship the bulk-grant script with the plugin.
- `POST /Users/{id}/Policy` replaces the whole policy: `UserManager.UpdatePolicyAsync` assigns every field unconditionally, so a partial object silently resets the rest. Always round-trip the full `GET /Users/{id}.Policy` and change one field. Jellyfin serializes GUIDs as 32-hex "N" on every endpoint, so ids from `/Channels` and from policies compare directly.
- "Pushed to clients" is a claim about `LibraryManager`, not about the plugin: `UpdateItemsAsync` only raises `ItemUpdated` for `SourceType.Library`. Verify such claims at the event gate before writing them into a README.
- A hostname scrub must grep every tracked file, including the lesson that describes the scrub; the last host name in the repo was in `LESSONS_LEARNED.md` itself and only the closeout hygiene grep caught it.
- A reboot of the test host kills in-flight reviewer sessions, so runtime acceptance evidence (counts, log lines, commit hashes) must live in the repo (PLAN status table, commit bodies), not in a chat transcript.
- A docs claim about a guard ("the workflow fails if X") needs the guard to exist in one direction as well as the other; `check-manifest.sh` caught a tag newer than props but not an older one until `build.sh` compared `VERSION` with `Directory.Build.props` explicitly.
