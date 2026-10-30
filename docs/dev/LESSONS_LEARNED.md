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

## Card 6: visible to every user (13fe695, 0cba6d4, 3cd0539, 8fea7ea)

- A `UserCreatedEventArgs` hook alone does not keep new users covered: the dashboard and provisioning tools `POST /Users/{id}/Policy` right after creating a user, and `UserManager.UpdatePolicyAsync` assigns `EnabledChannels` from the posted policy unconditionally (`UserManager.cs:915`) and then publishes `UserUpdatedEventArgs` (`:925`). Hook both events. The plugin's own saves go through `UpdateUserAsync`, which publishes nothing (`:213-258`), and a re-grant on an already visible user writes nothing, so the pair cannot loop.
- `UserManager.UpdateUserAsync` re-reads the tracked row and overwrites it from the entity it is handed (`SetValues`, `SyncPermissions`, `SyncPreferences`, `:227-229`), so any stale snapshot silently reverts a concurrent edit. Snapshot ids only (`GetUsersIds`), re-read each user with `GetUserById` (the query is `AsNoTracking`, `:135-143`) immediately before writing, and skip a user that disappeared between the two.
- `Channel.IsVisible` (`MediaBrowser.Controller/Channels/Channel.cs:26`) checks a non-empty block list on its own and ignores `EnableAllChannels` and `EnabledChannels` when one exists; there is no administrator exemption. Mirror the gate in a pure function on the real `User` entity, test the four branches, and report "blocked" as the admin's decision in the counts rather than as a failure.
- Jellyfin derives a channel's id from its name (`ChannelManager.GetInternalChannelId` is `GetNewItemId("Channel " + name, typeof(Channel))`), so a name lookup of the `Channel` entity is by construction the plugin's own id, and two plugins publishing the same name would share one entity. The entity does not exist until something loads My Media; `IChannelManager.GetChannelsInternalAsync` creates it and is safe once `CoreStartupHasCompleted`, so the locator falls back to it rather than failing the first pass on a fresh install.
- Prove an authorization write is idempotent with the second run's counts, not with a code reading: on the test host the first pass logged 39 granted / 2 already visible / 0 blocked and the pass after the next restart 0 / 41 / 0, which also shows the totals equal the user count. One Information line per pass with those three numbers, no user names, is enough for an operator to audit what the plugin did.
- Plugin event consumers are registered `AddScoped<IEventConsumer<T>, …>()`, the same shape the server uses for its own (`EventingServiceCollectionExtensions.cs:64,69`); `EventManager.PublishInternal` creates a scope per publish and resolves `GetServices<IEventConsumer<T>>()`. A singleton dependency inside a scoped consumer is fine; what matters is that the consumer catches everything it can throw, because it runs inside user creation.
- `scripts/build.sh` rewrites the manifest entry for the current `Directory.Build.props` version on every run. After the v0.1.0.0 tag, a dev build at the same version replaced the released checksum (0cba6d4, repaired from the tag in 3cd0539), and in CI the same rewrite dirtied the tag checkout so the branch switch for the manifest commit failed (21da132). Bump the version before the first post-tag build, and have the workflow discard the build's manifest before it switches to `main`.
- An admin endpoint that may legitimately do nothing should say why instead of erroring: `POST /Jellywave/GrantAccess` returns `Enabled=false` (setting off, or no channel yet) and an `Error` category when the pass throws, and the page prints the counts or the reason. A 500 with the detail only in the server log leaves the admin guessing whether the setting took.

## Card 7: richer live item (0228d43, 8edfa75)

- `IProviderManager.SaveImage` (`MediaBrowser.Providers/Manager/ProviderManager.cs:260`, delegating to `ImageSaver.SaveImage`) writes the file and calls `item.SetImagePath` with the file's metadata (`ImageSaver.cs:175,377`); nothing reaches the database until the caller runs `UpdateItemAsync(..., ImageUpdate)`. For a channel item the parent `Channel` has a `ChannelId`, so both report `SourceType.Channel` (`BaseItem.cs:296`) and `LibraryManager.UpdateImagesAsync` takes its `isLiveTvShow` branch (`LibraryManager.cs:2565-2574`): no dimensions, no blurhash, straight to `RegisterItem`. The row is still written, because `ItemPersistenceService.SaveItems` deletes the item's image rows and re-inserts them (`ItemPersistenceService.cs:484,352`).
- The client image tag is `MD5(item.Path + image.DateModified.Ticks)` (`src/Jellyfin.Drawing/ImageProcessor.cs:435-440`) and `SetImagePath` copies `DateModified` from the file (`BaseItem.cs:2366`), so overwriting the file in place is enough to bust client caches. An extension switch (jpg<->png) deletes the old file and logs one Information line from `ImageSaver` (`ImageSaver.cs:184`); the test host logged none in three cover changes because every cover was a JPEG.
- Jellyfin's channel refresh never clears a saved Primary: `ChannelManager` applies `ImageUrl` only to an item without one (`ChannelManager.cs:1133`), and the default refresh options validate local files and fill missing types. What it can do is re-save the whole row from an older in-memory copy. Re-check `item.HasImage(Primary)` on the freshly queried instance every poll instead of trusting an in-memory "applied" marker, and reset the marker when the image is gone so the next poll downloads again.
- Every field in `Audio.GetUserDataKeys()[0]` is part of the user-data key: `AlbumArtists[0]`, `Album`, the index numbers and `Name` (`MediaBrowser.Controller/Entities/Audio/Audio.cs:100-127`). `UserDataManager.SaveUserData` writes a row per key and leaves rows under old keys alone (`UserDataManager.cs:58-67`), so a value that rotates every track would leave an orphan row per track and listener. Check the key function before repurposing a metadata field for display; `Album` = "Artist – Title" looked free and was not.
- `GetItemList` returns fresh instances while `ChannelManager` and the refresh queue work on the LRU instance, and both save whole rows. Keep the window short (download the cover before the item lookup, so the item is held for milliseconds) and reconcile on every poll rather than only on change, so a lost write costs one poll interval, not one track.
- Over-capacity on `MaxResponseContentBufferSize` is an `HttpRequestException`, the same family as a timeout, and the cap does not apply under `ResponseHeadersRead` at all (card 3 lesson). Judge the headers first (status, media-type allowlist, declared length) so "unusable body" is a final answer (logo, no retry), then read the body under its own linked timeout with `LoadIntoBufferAsync(max)` so a chunked body cannot grow past the cap; only transport failures propagate and retry.
- A config toggle that sits inside a change-driven update path (`CoverArtEnabled` under "cover id changed") needs its own change detection, or the path must reconcile every cycle. Reconcile-every-poll made the flag take effect at once and removed the special case.
- Know every reader of a field before repurposing it. jellyfin-web shows `Album` as the playing bar's line 1 only when `ArtistItems`/`Artists` are empty (`apps/legacy/features/playback/utils/itemText.ts:30-32`), as the card's parent title (`cardBuilder.js:411,452`, `cardHelper.ts:242,642`), on the detail page (`itemDetails/index.js:457`), in the remote-control view (`remotecontrol.js:124`), in the lock-screen media session (`mediaSessionSubscriber.ts:123`) and in the dashboard's sessions list (`getNowPlayingName.ts:31`). A display hack aimed at one place lands in six.
- Runtime evidence for an image path is the Information line per cover plus the absence of `ImageSaver` warnings; "it looks right in the browser" says nothing about tags, blurhash or orphan rows. The mini-player thumbnail not changing mid-stream is the documented playback-start snapshot (`playbackmanager.js:672`), not a cover bug; it belongs to the card 8 page script.

## Card 8: station page (d95f759, 4f92c23, 916d53d, b1f9fe9, 929992e, c920130, 13b9ebf)

- File Transformation 3.0 keeps one pipeline per pattern string (`WebFileTransformationService.AddTransformation` keys the dictionary by the pattern, `:132-140`) and runs only the first pipeline whose pattern matches a request: the exact key first, then `Keys.FirstOrDefault(regex)` (`:66-92`). An anchored regex for `index.html` never runs once any plugin has registered the plain name (SeerrFin does), which is why the first cut's injection never ran (fixed in 4f92c23). Register the plain names and chain; read the host plugin's matcher before choosing a pattern. A second registration with the same id is skipped (`:138`), so the startup task may run on every restart.
- Plugins cannot reference each other, and `PluginInterface.RegisterTransformation` takes a Newtonsoft `JObject` from the other plugin's load context. Take the parameter type from the method signature and call its static `Parse(string)` by reflection (`WebUiStartupTask.cs:84-85,108`): no Newtonsoft reference in Jellywave, nothing crosses an `AssemblyLoadContext`. Callbacks are found by assembly, class and method name and invoked with `obj.ToObject(parameterType)` on `{"contents": ...}` (`TransformationHelper.cs:33,59,73-74`), so a POCO with a `Contents` property (`TransformPayload`) is the whole contract. `IScheduledTask` needs no registration: `ApplicationHost` collects them with `GetExports<IScheduledTask>(false)` (`:424`).
- To drive jellyfin-web's player from injected code, define a window global in a synchronous `<script>` at the end of `head` (the bundles are `defer`, html-webpack-plugin 5's default) and add that name to `config.json`'s `plugins`. `pluginManager.loadPlugin` awaits `window[name]()`, expects a class, and instantiates it with `events`, `playbackManager`, `appRouter`, `ServerConnections` and friends (`pluginManager.js:67-93`). That is the only sanctioned handle on the player; a bare script would have to reach into bundles.
- A regex that inserts `x, ` after `[` emits `[x, ]` on an empty array, and the first test asserted that output, so it pinned the bug (929992e fixed the regex with a `close` lookahead and the test now pins the exact valid output for `[]` and `[ ]`). When a transform generates a document, assert that the result parses, not only that the insertion is present.
- The modern layout has no user drawer: `.customMenuOptions` is built only by `scripts/libraryMenu.js:327`, and `apps/modern` imports `libraryMenu` only to set the title. Attach a user-facing page to something both layouts render: both open a channel through the legacy list controller, whose `.itemsContainer` carries `data-parentid` (`list.js:813`). The header goes above the grid of that page (4f92c23); the drawer link is a legacy-only bonus (916d53d).
- `playbackManager.currentItem(player)` throws `player cannot be null` (`playbackmanager.js:734-737`), which killed the refresh loop until 916d53d asked `getCurrentPlayer()` first. `playbackstop` is triggered (`:3579-3580`) before the manager clears the stream info and drops the player (`:3587-3589`), so a "what is playing" check made on that event still sees the old item and the header's Stop lingered after the bar's stop; `isPlaying(player)` (`:984`) is the truth (13b9ebf). Wrap each tick step in its own try/catch so one selector miss does not stop the others.
- Clients snapshot the playing item at playback start and rebuild the bar text and the `MediaSession` metadata from `state.NowPlayingItem` on `playbackstart`, `statechange`, `pause` and `unpause` (`nowPlayingBar.js:476-477,734-741`; `mediaSessionSubscriber.ts` overrides those four and has no `timeupdate` hook). Repainting on a timer alone loses the text at every pause; repaint on those same events too (c920130), with `state.lastTag` reset so the thumbnail is re-applied.
- `[Authorize]` with no policy is Jellyfin's `DefaultAuthorizationRequirement` (`ApiServiceCollectionExtensions.cs:65-67`): any user token, and any API key unconditionally (`DefaultAuthorizationHandler.cs:51-55`). User-facing endpoints (`Feed`, `Request`, `Request/{id}`) use it; the admin status endpoint needs `RequiresElevation` like the rest of `JellywaveController`. The two embedded assets are `[AllowAnonymous]` because they load with `index.html`, before sign-in.
- A proxy that forwards free text for authenticated users logs one line per accepted request with the Jellyfin user id and the station's request id, never the text (`JellywaveWebController.cs:169`, claim `Jellyfin-UserId`). Traceability has to hold on this side, because the station sees one IP for every Jellyfin user; the same fact is why its per-IP budget is shared and the page says so.
- A base URL (`/emby`) on the server makes unauthenticated `curl` checks of `/web/` paths 302 silently (`BaseUrlRedirectionMiddleware` redirects any path outside the prefix); the spike concluded "transform not applied" from an empty body once. Follow the base URL (`curl -L`, or request the prefixed path) before concluding a transform did not apply.

## Card 8d: station page extras (a923124, 427ced9, af6e8cc, 4895824)

- Jellyfin 12.3 takes a token from the `Authorization` header, from `?ApiKey=` always, and from `?api_key=` only while `EnableLegacyAuthorization` is on (`Jellyfin.Server.Implementations/Security/AuthorizationContext.cs:103-111`), so an `<img src="...?api_key=">` gets 401 from a `[Authorize]` plugin endpoint on a current server. Fetch authorised bytes with `ApiClient.ajax` (it resolves the raw `Response` when no `dataType` is given), show them through `URL.createObjectURL` and revoke the previous blob URL on change (4895824). That also keeps the session token out of URLs, browser history and proxy logs, which the query form would not.
- A string-replace edit anchored on a line that occurs in two functions (`renderLike();` is in both `sendLike` and `render`) landed three lines in the wrong one: the panel reveals ran in the click handler, where `h` is not defined, and never in `render`, which kept the Lyrics and schedule panels hidden. Anchor edits on context unique to the target function and re-read the whole function afterwards; `node --check` does not catch a free variable.
- The station's grid is an object keyed by day number with 24-slot arrays, Sunday first (`schemas/schedule.ts:31-37`, `time.ts:73`), read with the station's wall clock, not the browser's (`settings/persona.ts:196-197`, `getStationTimezone` in `time.ts:68-70`). `(int)DayOfWeek` is Sunday = 0 as well, so the only work is the zone: `TimeZoneInfo.FindSystemTimeZoneById` takes the IANA name (Linux, and Windows with ICU) and an unknown name falls back to UTC rather than failing the page. Take `nowUtc` as a parameter so the day boundary is unit-testable (02:00 UTC Wednesday is Tuesday 22:00 in New York).
- A document every open page would ask for and that changes rarely (`/api/schedule`) belongs in a server-side cache with a short lifetime and stale-on-failure (`ScheduleCache`: five minutes, one `SemaphoreSlim` so concurrent first requests make one station call, `IDisposable` for the semaphore; the container disposes singletons). Note the trade: while the copy is stale every call retries the station until one succeeds.
- The station limits likes per caller IP (2 s cooldown, 60 per hour, `routes/likes.ts:18-19,26-42`) and records `liked` per IP, so through the proxy "liked" and the count are shared by every Jellyfin user, exactly like the request budget of card 8c. Send the song id the listener saw with the POST: the station answers 409 instead of liking the next track (`routes/likes.ts:77-80`), and a second tap on the same track returns `alreadyLiked` with the same count, so the page cannot double-count.
- Map the station's refusals into a 200 with a status word and a sentence (`off`, `unavailable`, `ended`, `pending`, `none`, `ok` for lyrics), as the request outcomes already did: the page shows the sentence as `textContent`, never sees a station status code, and a wrong-looking panel can be diagnosed from its text alone. `/api/lyrics/current` sits behind `requireStationAuth`, which fails closed on a private station (`middleware/station-auth.ts:24-56`), so `privatePlayer` or `listenerAuth` on the station turns the panel into "the station is private" until listener auth is supported.

## Card 9: station page layout (4d67e6f, c6ea146)

- `grid-template-areas` on the header, with each of its four direct children (`cover`, `body`, `request`, `panels`) pinned to a named area, keeps the injected markup order-independent: the two breakpoints (60em: the request box drops under the text; 40em: everything stacks) only re-map the area strings, and the panels row is its own `repeat(auto-fit, minmax(16em, 1fr))` grid so the three `<details>` share a row when there is room. The flex rules on `.jellywave-cover` and `.jellywave-body` are now inert; clear such leftovers with the next layout change rather than letting them accumulate.
- A header that "fills the width" needs a third column with real content; wider text only looks empty. The request box was that content, and it needs its own two-column grid (text spanning both columns, name and Send on one row, c6ea146) rather than one column, or it looks odd beside the body at 24em. HI's "looks weird" was about the box, not the grid.
- A like refusal reaches the page as `Enabled=true` with `Message` and no `SongId` (`MapLike` takes `SongId` from the station body, which refusals lack), so the page tells it from a status by the missing id and keeps the heart and count. With the `likeBusy` guard and the station's `alreadyLiked` answer a double tap is silent by design; HI read that as "no feedback", so write the expected silence into the acceptance notes before the tester reports it as a bug.
- Moving the cache's timestamp back (`_fetchedUtc = now - Lifetime + RetryDelay`) turns its one freshness check into a retry backoff without a second field; the trade recorded under card 8d (every stale call retries the station) is closed. The 8d review's Suggested items cost one `wip:` folded into the next card before its tag; a polish card of their own would have cost a release.

## Card 10: poller health (0505d94)

- A health snapshot written at the same points the loop logs (started, stopped, feed received, station failure, unexpected exception) makes the server log optional for an admin: `PollerHealth` is one locked object the poller writes and `GET /Jellywave/Health` reads, and the configuration page turns it into one sentence (`configPage.html` `loadHealth`). Keep it to the poller's own state and the live item's description and image key; the station URL and anything from the station body stay out, so the admin-only endpoint has nothing to leak beyond an exception message.
- Derive "now" from what the upstream says is on air rather than re-implementing its override rules: the station's feed already applies its timed takeovers (`context.activeShow`), so `BuildToday` only needs the active show's name, marks a grid block current when the hour matches and the names agree (whitespace collapsed, case-insensitive), and names a takeover when nothing matches. Reading the station's `override` schema and reproducing its precedence would have been a second source of truth to keep in step.
- A JSON signature (`JSON.stringify` of the response, stored on the container) is the cheapest "did the data change" guard for a timer-driven re-render: the schedule panel refetches every five minutes but rebuilds the DOM only when the text differs, and the hourly `Hour` field changes the signature exactly when the "now" mark must move. Pair a blob URL's revoke with clearing the `data-src` that gates the fetch, so hiding the element also drops an in-flight response.

## Card 11: card 10 nits (cf9d672)

- Expose multi-field state as one snapshot method rather than per-field locked getters: `PollerHealth.Snapshot()` copies the five fields under the one lock into a `PollerHealthSnapshot`, so the health endpoint cannot pair a `Reachable` from one poll with a `LastErrorUtc` from the next, and the class loses forty lines of getters. Make "not yet known" its own value (`Reachable` is `bool?`, null until the first `Success` or `Failure`) instead of a default that reads as a fact; the page then says "waiting for the first poll" rather than "station reachable · last success never".
- Match upstream entities by id, keep names for display: the station's schedule grid is a 7×24 array of show ids (`schemas/schedule.ts`) and `/api/now-playing` carries that id in `context.activeShow.id` (`routes/public.ts` spreads the context; the top-level `activeShow` is reshaped without it), so `BuildToday` marks a grid block current when the hour and the id agree and uses the name only for the takeover line (falling back to the id when the station names no show). A renamed show in its own slot is no longer a takeover, and the whitespace and case rules written for the name comparison are gone with it. When the upstream offers an id next to a name, read the id from the start; the name rule cost a card.

## Wave 3: second security review (2026-10-27)

An external review of v0.3.4.0 (main de8d8dd) found that the trust model had not grown with the station page: plain `[Authorize]` admits API keys and users the channel is hidden from, the station client follows redirects off the configured authority, the channel is found by display name, and the build referenced Jellyfin 12.3.0.0 assemblies while claiming targetAbi 12.0.0.0. MI verified each claim against the repo and the Jellyfin 12.3 source before planning. HI decisions: claim 12.3 only; deny API keys on every station route; the listener keeps a free-text name for the DJ while the server keeps the Jellyfin user id per request; keep the doc packet but move it to `docs/dev/`. After approval HI wants MI to orchestrate the cards end to end (DA and RA as separate subagents), stopping only for HI-only actions.

## Card 12: ABI, release pipeline, CI (ec952e7, review fixes in the acceptance commit)

- A check that two declarations agree proves consistency, not compatibility: `check-manifest.sh` compared `build.yaml` with the manifest for eleven releases while both claimed an ABI the DLL could not honour. Anchor the claim to the artifact: `AbiTests` reads the built assembly's `Jellyfin.*`/`MediaBrowser.*` references and fails when any is newer than `targetAbi`, and the script compares the csproj package versions with it. Prove both fail by setting `targetAbi` back to 12.0.0.0 once.
- Split a release so the write token never meets the build: restore, analyzers, tests and packaging run with `contents: read` and `persist-credentials: false`; the publish job gets the zip as an artifact and runs only `gh`, `jq` and the manifest script. Take the entry's metadata (changelog, timestamp, `targetAbi`) from the zip's own `meta.json`, not from whatever `build.yaml` main has.
- The publish job runs main's copy of the manifest script, so a tag released before its branch is merged would use the old script. The job now refuses an entry whose `targetAbi` differs from the zip's; merge first, tag second.
- Pin actions by resolving the tag with `gh api repos/<o>/<r>/git/ref/tags/<tag>` (dereference annotated tags) and keep the tag in a comment; the v4 lines warn that Node 20 is deprecated, and Dependabot is left to propose the major bumps as reviewable PRs rather than jumping four majors untested.

## Card 13: station route authorization (5614428, cfb334f, review fixes ac3ca88..52866ce)

- `[Authorize]` in Jellyfin means "authenticated", not "allowed to see this": `DefaultAuthorizationHandler` succeeds for every API key and never looks at channels. A plugin route that serves channel data needs its own check, and the authoritative one is Jellyfin's `Channel.IsVisible(user)`, which runs fine in unit tests for a channel with no tags or rating, so no seam was needed. An `IAuthorizationFilter` runs before model binding, so a refused caller's body is never read; parse the API-key claim fail-closed (anything but a parsable `false` is a key).
- A unit-level test of a guard is not a test of the route: the first cut tested `RequestReceipts` on its own, and removing both the `Record` call and the ownership check in the controller left all 134 tests green. Drive the controller with a stub station handler and assert what the station saw (no call for another user's id or an unknown id), then prove each test fails with the wiring removed.
- Distinguish "you may not" from "not yet": a missing channel entity (fresh install, rename) answers 404 so the page keeps its 15 s retry, while API key, no user and not visible answer 403 and the page backs off 5 min. Both serve nothing.
- A cache key tied to the assembly version goes stale when several cards ship under one version: browsers revalidate `?v=0.4.0.0` and get 304 for a changed `ui.js`. Hash the asset bytes instead, so host tests between releases see the script they deploy.

## Card 14: outbound hardening (91ff06f, 8d9e17c, f5e03e6; review follow-ups 6c69545, bd5c0c2)

- A plugin inherits the host's HTTP client policy unless it registers its own: Jellyfin's `NamedClient.Default` follows redirects, so a station (or anything that answers in its name) could bounce a GET or a 307/308 POST to another host. A named client with `AllowAutoRedirect = false` and one `CreateClient` helper closes it; `Microsoft.Extensions.Http` comes with the ASP.NET Core shared framework Jellyfin runs on, so no package reference and nothing extra in the zip. A redirect surfaces as a `HttpRequestException` subclass so every existing catch keeps working, and its `Location` is shown to administrators only, through `textContent`.
- "Unreachable" defence code needs a mutation, not a reading: the DA judged the `Uri.Compare` authority check dead behind the absolute-path check, and the RA found `\/host:8000/x`, which is not an absolute URI on Linux but resolves to the same host on another port. Say in a comment why a check is load-bearing, or the next cleanup removes it.
- Tightening validation strands existing configurations: a base URL saved with `?`, `#` or credentials worked before and now counts as unconfigured. Report a refused non-empty value as "set but not allowed: <reason>" (a fixed sentence, never the value) on the status line and the config page, and put the change in the release notes.
- Trust bytes over headers for images: magic bytes decide the type and header dimensions bound the decode (4096 per side). A sniffer that reads attacker-controlled offsets must never throw; the RA fuzzed about 1.7 million truncated and flipped inputs. Animation (GIF, APNG, animated WebP) stays an accepted risk.

## Card 15: channel identity, grant serialization, verify script (623bf69..9be708f; follow-up 354536c)

- Find a Jellyfin entity the way Jellyfin names it: the channel id is `GetNewItemId("Channel " + name, typeof(Channel))`, and whether names that differ in case collide depends on `EnableCaseSensitiveItemIds` (on by default in 12.3). Running other channels' names through the same call and comparing ids follows the server's setting instead of guessing a string rule. On a collision the finder returns no channel at all, so every consumer (poller, granter, station routes) fails closed without having to remember to check.
- A lock around a call into the host needs the host's re-entry paths checked: the grant gate wraps `IUserStore.SaveAsync`, and Jellyfin's `UpdatePolicyAsync` publishes `UserUpdated`, whose consumer calls `GrantAsync` inline. It is safe only because the plugin saves with `UpdateUserAsync`, which publishes nothing; that is now written on the interface, because nothing else would stop a future change from deadlocking every user save.
- A status shown to an admin should be read when it is asked for, not copied from the last background pass: the collision line followed the poller and lagged a poll interval after a rename until Health resolved it live.
- A verification script that picks "the first one" proves nothing about yours: verify-jellyfin.sh now selects the channel by exact name and fails when it is missing, duplicated (Jellyfin lists a shared entity once per provider), absent from the user's views, or without an audio item. A stub `curl` on PATH let the RA exercise it without a server.

## Card 16: docs and repo hygiene (283f1d5..48a1272; review fixes 1b0406a)

- A threat model has to name the boundary of each control, not just the control: "redirects are never followed" was true only for the plugin's own requests, while the stream itself is fetched by Jellyfin's ffmpeg (Relay) or the client (Direct). State where a control stops and list what lies beyond it as an accepted risk.
- Check upstream claims you cite: the File Transformation issue is filed for 12.2, with 12.3 reported in its comments, and a 12.3 user agent gets an empty catalog for every plugin, not just one. Cite it as related, with the date.
- A pointer to a reporting channel is a promise: SECURITY.md names GitHub private vulnerability reporting, which is off until HI enables it, so the merge waits for that switch.

## Host test of 0.4.0.0 (2026-10-27): the card 6 grant overrode the admin

- With "make the station visible to every user" on, an admin who unticked the station on a user's Jellyfin Access page saw it come straight back: Jellyfin publishes `UserUpdated` from the policy save and the plugin's consumer re-granted within milliseconds (the startup pass would also have re-granted after every restart). Card 13's route check was correct; it was handed a user the plugin had just re-granted. A background writer of someone else's policy must never be able to undo that owner's explicit choice; card 6 only tested granting, never an admin taking access away again. HI chose stability over the shortcut: the grant becomes a one-shot admin button (card 15b), and Jellyfin's own Channel Access setting is the only ongoing authority.
- Jellyfin's "Channel Access" and "Enable access to all channels" headings on the user Access page are jellyfin-web's own strings (`HeaderChannelAccess`, `OptionEnableAccessToAllChannels`), not the plugin's; a plugin cannot rename them.

## Card 15b: access grant as a one-shot admin action (4ec324b..6efa974; review fixes 4589d57, 7f742e1)

- Check a "default" claim against the screen admins actually use, not only the server: `AddDefaultPermissions` turns "all channels" on, but jellyfin-web's Add User form unticks it and posts the policy, so dashboard-created users do not see the station. The page now says so and names the dashboard with provisioning tools.
- A one-shot action cannot tell "never had it" from "an admin took it away"; say that next to the button ("each press adds the station to everyone who cannot see it right now, including anyone you unticked earlier") instead of promising the plugin will not undo an untick.
- "Nothing registers a background writer" is weaker than "nothing can reach the writer": the RA injected the granter into the poller's constructor and every registration test still passed. A reflection test over all constructors (allowed holders of the granter, the user store and IUserManager listed by type) caught it; service-locator calls stay outside what reflection can see.

## v0.4.0.0 host verification (2026-10-27)

- A verification script needs its own review against a real server, not just `bash -n` and a stub: verify-jellyfin.sh defaulted to the plugin's default tile name on a renamed station, and posted an empty PlaybackInfo body, which never yields a TranscodingUrl because the server has no device profile to transcode for. Both were invisible against the stub `curl`. Read defaults from the system under test (the plugin's own configuration) and send the smallest realistic client request (an mp3 transcoding profile).
- The host test earned its place again: every card passed review and the unit suite, and the one Blocker of the wave (the grant overriding an admin's untick) surfaced only when a person used Jellyfin's own Access page.

## Card 17: station data consistency (2b29bda..c4b7c12; review fixes d70cc97..a55c4e1, nits in the acceptance pass)

- Keying a cache by its source is only half the fix; every reader must check the key too. The poller kept state per station origin, but `GET /Jellywave/Feed` served whatever the last poll stored, so for up to one poll interval after a URL change it paired the old station's track with the new station's name. The route now compares the snapshot's origin with the configured one and answers "nothing heard yet" on a mismatch, and reads feed, state and origin as one snapshot under one lock.
- A negative cache needs the same key as the positive one: the missing-cover memory is (origin, cover id) with a 30-minute TTL on an injected clock, so another station's identical id, a new track, or time each lift it.
- Grep for the sibling of a bug before closing it: the duplicate-id 500 in `BuildToday` had two cousins (a null history entry in Feed, a null lyric line in Lyrics), all from trusting upstream collections.
- A source-text tripwire must be probed with real code, not comments: the scan deliberately skips comment lines, so a commented probe "passed" and looked like a hole. Probe with a code line, then delete it.

## Card 18: web client (89ef5a0, cd901b2, 0327e66; review follow-ups b6fced9, 9df922c)

- Use the host app's own navigation signal before watching the whole DOM: jellyfin-web 12.3 dispatches a bubbling `viewshow` for legacy and React views alike, after the list controller has set `data-parentid`, so it can place the header for the shown view. The MutationObserver stays only as a debounced fallback whose callback arms one timer and queries nothing.
- An async reply must prove it still belongs to the moment that asked: a captured song id for likes (both directions), a session key (`serverId|userId`, null without a token) plus an epoch for every request, and the request poll checks the session before each send. The RA's timing simulation found the one poll that slipped through between a token change and the next session check; a plain Node stub of `window`, `document` and `ApiClient` was enough to reproduce each race without a browser or a framework.
- SPAs keep cached views in the document: a document-wide search must skip jellyfin-web's hidden `.mainAnimatedPage.hide` views or it re-points at, or writes into, a page the user is not looking at.

## Card L1: station model, migration, rename bridge (68afb48..31521b1, acceptance pass 881846d)

- Unit tests that build services through `internal` test constructors prove the logic, not the wiring. The RA removed the save hook, the constructor migration and three DI lambdas with all 400 tests green. For an authorization card, add tests that build the real `Plugin` (temp config folder, small `IXmlSerializer`) and resolve services through their public constructors, in a non-parallel collection because `Plugin.Instance` is static.
- A plugin constructor must never throw in 12.3: `PluginManager.CreatePluginInstance` marks the plugin Malfunctioned and persists it. Wrap load and migration, log at Error, and degrade to a state that fails closed (unmigrated config still honours the 0.4.x tile name's blocks).
- Every degraded path needs a save test, not only a load test: the "migration failed" path kept old blocks in memory but lost them at the admin's first page save (F-R1), because the posted body looked migrated.
- Hold a concurrency test where the shared state is still the old object. `BasePlugin<T>.UpdateConfiguration` swaps `Configuration` before `SerializeToFile`, so a hold inside the serializer lets an ungated reader pass; holding inside `PrepareSave` exposes it.
- `IsEnabledFor` (12.3 `ChannelManager.cs:221-237`) hides a channel per user in My Media, but `GetChannelItems` checks neither it nor `IsVisible`; hiding is not enforcement. Copying a block to the new id is what makes Jellyfin enforce it everywhere.
- An earlier tile name can later belong to another plugin's channel; anything that widens access from earlier names (the carry-over) must skip ids owned by another registered channel, while the hiding side keeps them.
- A mutation runner that resets its worktree between mutants also wipes uncommitted patches; commit (or copy) the change under test before running it.

## Card L2: many items (be1ff54..2c7ceb5; fix rounds 283bb1c..7d1dac5)

- Jellyfin 12.3 builds a remote ffmpeg input as `-i "<url>"` with no escaping, and `Uri.ToString()` turns `%22` back into `"`. Build every URL that reaches `Path` or `EncoderPath` with `Uri.AbsoluteUri`, and pin it with a test carrying `%22`, a raw quote and a backslash. The bug had been in every release since 0.1; only admin configuration could reach it.
- "Primary" must mean "primary among what is published". A selector that ignores the validation applied elsewhere (`PrimaryStation()` vs `StationSources.Publish`) lets a refused station steer writes onto another station's item. Derive every role from the published list.
- Jellyfin applies a channel item's `Name`, `Overview` and `ImageUrl` only when it creates the item. Whatever the plugin writes for one role (primary overview, cover) must be actively undone when the role moves, every poll, not only on change.
- A budget that defers work must define what the user sees meanwhile. The logo budget first meant "nothing" (a blank card); "embedded logo now, real logo later" keeps the no-blank-cards rule.
- Jellyfin gives a channel no way to order its items (`DefaultSortFields` is advisory; the grid sorts by `SortName`). The poller writes `ForcedSortName` ("rank name") and repairs it every poll; it is not part of the user-data key and survives channel refreshes, because audio items are not in the library cache.
- HLS through Relay works because 12.3 forces an input format only for hardware-accelerated video files; Direct HLS is downgraded to Relay, which is what Jellyfin's own M3U tuner does. Playlist (`.pls`, `.m3u`) and DASH (`.mpd`) URLs are refused: ffmpeg cannot open the former, and the latter is a manifest.

## Card L4: per-station state (522d08f..882e3f1; fix rounds a1ff4e5..d4866ba)

- Shutdown is a behaviour, not an absence of behaviour: test it. Four mutants that turned a stop into "timed out" warnings, error lines or a 32 s hang survived 582 tests. A deterministic test cancels while requests are in flight (handler awaits `Task.Delay(Infinite, token)`) and asserts no log at Warning or above, no health record and no write; a second one cancels from inside the write phase.
- `Parallel.ForEachAsync` cancels every other body when one throws. Per-item isolation therefore depends on a catch in each body that swallows everything except the stopping token, and a plain `OperationCanceledException` from an HTTP timeout must count as that item's failure, not as shutdown.
- Moving a read earlier moves its failures too. The pre-fetch item lookup added to save a download put database errors inside the station's try, so a library lock looked like the station failing. Guard reads that only optimise, and treat their failure as "unknown".
- 12.3 `ImageSaver.SaveImageToLocation` opens the target with `FileMode.Create` before it observes the token, so a cancelled save truncates the image. Check the token just before the save, then save with `CancellationToken.None`; the save is a bounded local write.
- A health flag that resets on the first good sample flaps around its threshold. End a "slow" streak only below a lower ratio (0.8 of the interval).
- A hidden tag beats a wrong tag: after a station URL change, Feed withholds the image tag while the applied cover belongs to the old origin, until the write phase saves the new image or the logo.
- Concurrent `ILibraryManager.GetItemList` calls are safe in 12.3: the repository creates a `DbContext` per call (`BaseItemRepository.Querying.cs:68-104`). Verify that in the source; a locked test fake proves nothing about the real service.

## Card L3: config page (7571fdd..aaf15fe; fix round 6544a65..8e29e7a)

- An async answer in a dialog must prove it belongs to the dialog and the input that asked: a check number, the editor object and the URL still in the field. Without that, a 30-second probe answered after Cancel filled the next station's dialog and was saved. The card 18 lesson (stale replies) applies to admin pages too.
- A page script without tests regresses silently. A dependency-free node harness with a small fake DOM, pending promises the scenario resolves by hand, and one CI step caught 26 of 30 page mutants. Its cost is the fake DOM: it supports only what the page uses and fails loudly on anything new.
- A headers-only probe needs `HttpCompletionOption.ResponseHeadersRead` and tests whose audio bodies throw when read, plus one real socket serving an endless stream; otherwise "we never read the body" is a claim, not a property.
- .NET 10 rejects Shoutcast v1's `ICY 200 OK` as `HttpRequestError.InvalidResponse`; the message is the only clue. Match narrowly, fall back to a generic category, and never echo the message.
- Check the user-data claims in the source before warning users: 12.3 resolves user data by every key, including the external id and any row of the item, so favourites survive a rename even though `Name` is part of the first key.
- Station-chosen URLs (a playlist entry, the feed's stream mount) are request targets; give each the same rule as the admin's own URL before any request, and keep every follow-up request on the host of an address that already passed.

## Card L5: routes and station page per station (54e27a6..f54fac5; fix rounds 9dd4f1c..c45b105)

- Resolve a caller-named resource in exactly one place that runs after authorization and before the action, and make the action unable to get it any other way (`CallerStation()` throws if the filter did not run). In ASP.NET Core an authorization filter (`[StationAccess]`) runs before model binding and an action filter (`[StationRoute]`) after it, so a refused caller gets 403 before any id is looked at; verify that against the host's own MVC setup (Jellyfin 12.3 registers no global filters), not only a test server.
- A source-text ban list is not a rule: a fully qualified name split over a line, or a dependency that hands out the forbidden thing, gets past it. A reflection walk over held types is better but an exemption ("methods taking a `Uri`") reopened the hole. Scanning the IL of the class and its compiler-generated types (`MethodBody.GetILAsByteArray` + `Module.ResolveMember`) catches aliases, lambdas and async state machines; fail loudly on unknown opcodes and unresolved tokens. A deny-list scan still has limits (helpers outside the scanned types), so keep the behaviour tests that would catch a station mix-up.
- Receipts and other tokens should remember the whole identity they were issued for (station id and origin), and the redeeming route must take it from the receipt, never from the caller.
- A client that drops its UI on "gone" must also schedule its own way back. Two idle-page faults (after a feed 404, and after a second 404 inside the loop guard) only showed up in harness scenarios that did not nudge the DOM by hand; a test helper that simulates activity can hide exactly the path an idle user takes.
- Old cached clients are a real compatibility surface: an absent `stationId` meaning "the primary station" kept a cached Android page working; check what the server returns for any asset version (`ui.js?v=` returns the current script) before claiming an old client was tested.

## Card L7: docs and release candidate (1092ba3..37006e8; fix round bc87e41..5f1dbec)

- Check every UI name you send an admin to against the host's own strings. "Refresh Internet Channels" came from the original plan and spread into README, the page, the changelog and the checklist; Jellyfin 12.3 shows the task as "Refresh Channels" (`TaskRefreshChannels`) under the category "Internet Channels".
- A docs pass is a code review: reading every claim against the code found a real bug (F1: the channel entity was created only when a SUB/WAVE station existed, so a plain-stream-only list broke both admin buttons) and an overclaim (the `Items` rule blocked reads but DESIGN said it blocked writes).
- A host checklist is a program: run it in your head against the code. Steps that depend on a later step, a policy POST that replaces the whole policy, and a "press it again after reload" step that the page makes impossible were all caught by checking each expected text against the page script.
- `POST /Users/{id}/Policy` replaces the whole policy and needs the provider ids, so a checklist that sets one field must say GET, edit, POST the whole object (the card 5 lesson, again).

## Host test v0.5.0.0 (2026-10-28)

- A fake DOM that accepts what the browser accepts still misses what jellyfin-web changes. Its custom-elements polyfill replaces `document.createElement` and calls `toLowerCase()` on the second argument, so `createElement('button', { is: 'emby-button' })` threw inside `load()`: the spinner never cleared and the poller line stayed "checking…". Plain Firefox ran the same page fine. The harness now mimics the polyfill; when a page breaks only inside Jellyfin, read the browser console first.
- Check write paths in the server source before writing a checklist step that depends on them. Jellyfin 12.3's `UpdatePolicyAsync` never writes `BlockedChannels`: a policy POST that sets, nulls or empties it answers 204 and stores nothing. The step "block user C through the API" could not work, and the docs' claim that an Access page save clears blocks was wrong too. Read the result back after every write, not only the status code.
- A picker-driven header looked right in the mockup, but on the host users expected the header to follow what plays (card L5b). A static mockup shows one state; test the idle and the playing states with the HI before building controls such as a picker, a remembered pick and a Play button that the real flow makes unnecessary.
- Shortening help text drops the qualifiers that kept it true. Card L5c's draft said "the station logo shows" for a track without art; on a SUB/WAVE item that is the embedded Jellywave logo (`LiveItemArtwork.Logo`), not the station's own, so a new admin would read it wrong. Check each fact a short text keeps against the code, not against the long text it replaces.

## Card L8: header for plain streams

- A "never called" check can pass by matching nothing. The harness's `urls()` returns `METHOD url`, and the new check's pattern was anchored on the bare url, so "no SUB/WAVE route is called for a plain stream" held even when the bar repaint asked `Feed?stationId=plain`. Mutating the code each check guards (one mutant per check) showed it; check against the raw request list, and run the mutant before trusting a negative check.
- When two paths reach the same outcome, a scenario can prove the wrong one. A station re-checked into another kind was rebuilt by the feed's 404 (`stationGone`) even with the list-driven rebuild removed. Holding back the requests of the other path (`serve(keep)`) makes the scenario prove the path it names.
- A field shown only for one kind still has a value for the other. Check fills a hidden Genre or Website on a station detected as SUB/WAVE; without a guard in the draft, the hidden value would be saved and shown later if the station became a plain stream. Decide per field what a hidden input means, and test that a hidden filled field is not saved.
- Text the plugin writes into a Jellyfin field is rendered by Jellyfin's rules, not the plugin's: jellyfin-web shows an item overview as sanitized HTML and Markdown, so notes that the station page shows as text could become markup in the detail view (RA fix round). Ask of every written field who renders it and how, and strip what that renderer reads (`<` and `>`); Markdown links remain, and the threat model says so.
- A link label that shows the host is only honest when the host is ASCII. Serve the IDN (punycode) form (`Uri.IdnHost`, which throws on hosts IDNA refuses) and make the page's own rule accept only an ASCII host, so a Cyrillic look-alike shows as `xn--…`.

## Card S-b: IL scan allow-list

- A deny rule that checks a call's declaring type, generic arguments and result does not see its parameters. `JellywaveChannel.TileName(null!)` (takes a `PluginConfiguration`, returns a string) passed every deny rule of the station route scan; only the allow-list caught it. Check new rules with a mutant that a plausible refactor would write, not only with the bypasses the rule was designed against.
- A type's static constructor is a `ConstructorInfo` too (`Type.TypeInitializer`). The first walk treated it as "this type is constructed", walked every virtual method of `JellywaveChannel`, and reported sinks the helper never reaches; test `IsStatic` wherever a constructor means an instance.
- Exempting auto-properties wholesale reopens the gap through shared state: a service's or a static auto-property can hold a value that other code filled from the configuration. Exempt only instance accessors of types listed as data, and prove "auto" from the IL (one backing-field token), not from `[CompilerGenerated]` alone.
- (S-b fix round) An allow-list of types is only as strong as what it lets through unread. Listing data types by their auto-properties left property initializers, constructors the serializer or model binder runs, and getters only the serializer calls unseen; walk a listed type whole, and require that every plugin type handed to framework code (type tokens, generic arguments, parameter and return types) is listed. A reviewer's mutant harness (21 mutants, one per idea) found gaps of several kinds in a rule whose own controls all passed.
- Removing a fix and rerunning the controls shows which control proves it. Dropping the value-type rule failed nothing: every way to run a struct's override (`initobj`, `box`, `constrained.`) already passes a type token, which the handed-type rule reads. Say so instead of claiming a control proves it.

## Card L6: ICY titles for plain streams

- A timer-driven state change needs a scenario where the regular tick cannot do it too. The page's 5 s tick starts when a header is built, so a plain stream started from a fresh page ticks at exactly 30 s and the "Delayed" text appeared with or without the line's own boundary timer. Starting the plain stream after a SUB/WAVE station (the interval keeps its phase, 2 s off) made the 30 s check fail when the timer was removed; check a timing rule with the other trigger out of phase.
- "From playback start" on the page means when the page selects the station, which is after the list answers (the `playbackstart` handler re-reads the list first). A scenario that delayed the list by 20 s moved the 30 s mark with it. Name the event the clock starts on in the code and the docs, and test the delay that matters (the first title answer) rather than one that also moves the start.
- (L6 fix round, RA R1) Disposing an `HttpResponseMessage` does not close an HTTP/1.1 connection at once: `SocketsHttpHandler` drains up to `MaxResponseDrainSize` (1 MB) of an unread chunked or declared-length body, for up to `ResponseDrainTimeout` (2 s), to reuse the connection. For an audio stream that is a megabyte read and a late close after a "headers only" or "one block" request. A fake handler cannot show it; a real socket whose server stalls after a short tail does (the close came at 2.0 s until `MaxResponseDrainSize = 0`).
- (L6 fix round, RA R2) State that hangs off a DOM node dies with the node. The title polling was keyed to the header element, so when jellyfin-web evicted the cached view the bar the page had painted froze on an old title. Key ongoing work to what the user still sees (the bar this page painted), not to the element that started it.

## Card L10: Relay only

- Removing a delivery mode does not remove what clients can read. Without Direct no client is told to fetch a station, but the station URL is still the media source's `Path`, which PlaybackInfo returns to every user who can see the item. Write "relayed" in the docs, never "hidden", unless the field that carries the URL is gone too.
- `MediaSourceSchemaDate` only re-saves stale sources when it is later than the configuration's `LastChangedUtc` (the item date is the larger of the two). A configuration saved with an older build after the new schema date keeps the old sources until the next save. The other way round, a schema date in the future swallows every save made before it (Jellyfin re-saves only when the date is strictly later than the stored one), so set it to the time of the change, neither earlier nor later, and expect the first save after an upgrade to finish the job.

## Host run 2026-10-29 (v0.5.0.0 build with L10)

- A check script that prints server answers prints whatever the server put in them. Jellyfin echoes the caller's key in every `TranscodingUrl` (`ApiKey=`), so `verify-jellyfin.sh` printed the admin key on every run. Redact credentials in anything a script echoes, and test it by counting the real key in the output (`grep -c -F -f <keyfile>`) rather than reading the output by eye.
- A Jellyfin server can live under a base path. The test host answers `/System/Info/Public` with 302 at the root and 200 under `/emby`; with the root URL the script failed every check with no reason printed. The URL in a host step must include the base path.
- jellyfin-web 12.3's playing bar already has a Stop button, wired to `playbackManager.stop`, but its CSS hides it at widths up to 80em, so most windows show only play/pause. Read the shipped bundle on the host before building a control the client may already have.

## Card L11: first-run fixes and Stop instead of Pause

- Read the whole media-query ladder before relying on a control the client hides. The bar's Stop is hidden up to 80em, but up to 56em jellyfin-web hides its whole centre group, so on phones (and the Android app) there is no Stop to show at all, and hiding play/pause alone would have left the bar with no control. A fact found for one width is not a fact for every width.
- Listen where the event is fired, not where it is named. jellyfin-web 12.3's `playbackManager` re-fires `playbackstart`, `playbackstop` and `playerchange`, but turns a player's `pause` into a progress report; the script's listeners for `pause`, `unpause` and `statechange` on the manager had never fired. Grep the bundle for the trigger, not for the event name in an enum.
- A fake that delivers every event regardless of its target hides that kind of bug: the station page harness put all events in one bucket by name, so a listener on the wrong object passed. Key fake events by target, as the real event bus does.
- A negative rule ("never stop during a switch") is guarded by several checks at once (the station at pause time, the same player, the same station, still paused after the settle). Removing one guard at a time showed three of them were unproven; each needed its own scenario where only that guard stands in the way (another station, another item, another player current and paused within the settle).
- An unsaved-changes guard needs to know whether its page is still the one shown: jellyfin-web keeps hidden views in the document, and its own navigation cannot be stopped, so a listener left on `window` would ask about changes the page already dropped. Track `pageshow`/`pagehide` and remove the listener on `viewdestroy`.
- Compare what the user can change, not more. A normalised signature (sorted keys, blank equals missing, `SortOrder` left out) had parts no scenario could tell apart; the plain copy without the page-only key is what a mutant could prove.
- (L11 fix round) A host step is code too: check that a correct build can pass it. (h)11 expected "not saved yet" after a reorder, but only an edited or toggled row is marked unsaved; the tester would have failed a correct build. Walk each expected text back to the line that writes it.
- (L11 fix round) An event can mean more than its name. The cast player fires `pause` on every play-state change, so on cast "pause → stop" rests on the settle and the paused check alone. When a rule reacts to an event, read every place that fires it, per player.
- (L11 fix round) A note that describes the page goes stale when the page reloads under it ("Your changes are still on this page" after the changes were dropped). Clear status notes where the state they describe is replaced.
- (L11 fix round) Two codes that must agree (the page's and the server's blank-name rule) need a test with the input where they could differ: a non-http scheme. Equal outputs on the easy inputs proved nothing.

## Card L13: first-run page layout, plain-stream health, who can see it

- "The server already keeps it" needs reading against the state's lifetime, not only its fields. `StationTitles` had the outcome, but a failure stored no time and the entry went 45 s after the last listener, so the health line the sketch showed ("no answer 2 h ago") needed its own record with its own lifetime. Ask how long a value lives before building a view on it.
- A record tied to a URL does not have to hold the URL. A per-process hash of it lets the reader ask "was this read of the URL the station has now?", so a changed URL shows nothing at once, and the "never the URL in the state" rule holds; a reflection test on the record's field types keeps it that way.
- An attribute test proves the attribute is there, not that the pipeline enforces it. Running the admin controller in a real MVC host (a role claim standing in for Jellyfin's policy) showed the 403 for the new route and every old one; a mutant that only the pipeline can judge (`[AllowAnonymous]` on the method) is the one worth running there alone.
- "Reuse the rule" is testable: the new count must equal what the grant pass calls "already visible" on the same users. A copy of `Classify` without the rename bridge would have passed every example built without an earlier tile; the equality test with a bridge-blocked user caught it.
- A sketch approved "as drawn" can still differ from the card's text (column names, a heading). Build what the text lists, keep the rest as it was, and put each difference in front of HI rather than choosing silently.
- Moving a field breaks host steps far from the card: (e)34 and (e)43 expected "✓ in the tile" on a station that had been played, which a correct L13 build shows as "✓ answered … ago". Search the checklist for every text the change can alter, not only the section that names the page.
- (L13 fix round) A mutant runner must call any build failure a build failure. Mine looked for `error CS` only, so a mutant that StyleCop or an ASP.NET analyzer refused to build was reported as a survivor, and I called one "equivalent" without running it. Match `: error ` and treat a non-building mutant as not run.
- (L13 fix round) Run mutants in a scratch worktree, never in the working tree: an interrupted runner leaves a mutant in a tracked file, and a commit can pick it up.
- (L13 fix round) "Answered" needs a moment, not an outcome. A stream that sent its headers and then a slow title is up; a redirect is a third state the relay and the sampler disagree on. A callback at the exact point (after the status check) says more than any exception type caught later.
- (L13 fix round) A field inside a closed fold cannot show the browser's validation bubble. When a form is split into folds, do the check in the save code (`novalidate`, `checkValidity`, open the fold, `reportValidity`) so every Save button behaves the same.
- (L13 fix round) A banner that names something must name the saved value, not the field the admin is typing in; a check that types into the field between two reads is what tells them apart.

## Card U1: stack refresh (Jellyfin 12.4)

- A version bump has two kinds of "12.3" in the docs: what the server does now (re-date it, citing the release diff)
  and where a fact was read (12.3 source line numbers, a bundle read on a 12.3 host). Re-dating the second kind would
  claim a check that has not happened. Say the rule once at the top of the document and keep provenance as it was.
- Bundle details (chunk numbers, module ids, a commit hash) can change with a rebuild even when the source does not;
  jellyfin-web 12.4 changed only its package files, which can be enough to renumber chunks. Keep them tied to the build
  they were read from and list them for the host re-check.
- A harness check that a text has left the page records a decision (L5c: the repository URL lives in the README).
  When a later card brings the text back, narrow the check (only in the grey line) instead of deleting it, so the
  help text stays short.
- Confirm the bump in the built assembly, not only in `project.assets.json`: the references read from the DLL
  (12.4.0.0) are what `targetAbi` must cover, and a mutant with `targetAbi` 12.3.0.0 shows `AbiTests` catches it.

## Card L9: starter stations

- "Fresh install" needs a signal an upgrade cannot also give. A marker element in the file cannot be it: an element
  missing from an older file reads as the constructor's default, so the marker's default would have to mean both
  "fresh" and "old". The absence of the file, checked before the base class's lazy load writes defaults, is the only
  signal Jellyfin leaves, and it has to be read first thing in the constructor.
- `XmlSerializer` fills a collection the constructor made by appending, so seed data in a configuration constructor
  doubles on every load and brings removed entries back. Seed after loading, and test against hand-written files as
  well as round trips: a round trip through the same code can hide the shape an older build wrote.
- Check a station's terms, not only its stream. Every URL answered and looked right; only the terms page (linked from
  the site footer, not the FAQ) said "no third-party clients" and "no re-streaming". When a brief's list conflicts
  with a rule the card already states, build the rule and put the change in its own revertable commit.
- Probe the audio, not only the headers: one stream said `icy-br: 128` and nothing about its rate, and ffprobe found
  48 kHz where every other starter is 44.1 kHz.
- (L9 fix round) Reusing a failure flag for a new failure path carries its words along: "could not be read" and "blocks
  on the old tile" were wrong for a fresh install that could not save. When a new path sets an existing flag, read the
  text every consumer shows for it (log, Health, page), not only the behaviour it gates.
- (L9 fix round) "Nothing was written" holds only if the write fails before it starts. `SerializeToFile` creates the
  file first, so a failure midway leaves a truncated file; say which failure a guarantee covers.

## Card L14: ask a SUB/WAVE station only while someone listens

- A rule that narrows what a loop does breaks every old test of that loop at once, for a reason none of them is about.
  Give the old tests an explicit "every station needed" demand (`StationDemand.EveryStation`, internal, never
  registered) and test the rule with its own harness, instead of teaching every old harness about listeners.
- "Ask only stations that are due" looked natural for a woken poll but changed the meaning of the public
  `PollOnceAsync` (two polls in a row no longer asked twice). Splitting the poll kinds kept it: a regular poll asks every
  needed station, a woken one only those not asked since they became needed, and the loop keeps the regular timing.
- A wake is a signal, not work: releasing a one-slot semaphore from the event handler keeps fetches and item writes on
  the one poll loop, bounds a burst to one extra poll, and cannot race the write phase.
- "Idle" and "failing" both clear the state and show the logo; reusing the failure path would have reused its health
  write and warning too. Reuse the look, not the path, and test that health stays as it was.
- A mutant runner killed by a timeout leaves its last mutant in place, and the next run measures against it. Reset the
  worktree (`git checkout -- .`) before each mutant, not only after.
- (L14 fix round) A deadline that has passed must win over a pending signal. "Wait for a wake or the rest of the
  interval" read fine, but with the rest at zero a waiting wake still answered first, so a steady trickle of wakes
  replaced every regular poll. Test the loop itself, not only the poll it calls: no test ran `LoopAsync`, so the
  timing variables were never exercised.
- (L14 fix round) A clock that runs faster than real time (10×) lets a loop test cover a 5 s interval in half a second
  while the loop's real-time waits (`Task.Delay`, a semaphore timeout) still pace it; poll for the expected count with
  a deadline so the fixed code finishes fast and the broken one fails at the deadline instead of hanging.
- (L14 fix round) A signal path that a user can drive needs the same gate as the route path. The station routes were
  gated, but Jellyfin's playback report accepts any item id from any signed-in user, so the demand counted listeners
  the tile refuses. When the host's code is not available to read, gate on what its event carries.
- (L14 fix round) A mutant runner must check the build's exit code, not grep its tail: `if (false)` is a warning, so an
  error here, and the run then tested the previous mutant's binaries and reported a kill. Same lesson as e8dea54, one
  level up. Prefer a runtime-false condition (`x < 0`) so the mutant compiles.
- (L14 fix round) A rejected promise that a page leaves unhandled ends a node harness before its checks run; record
  `unhandledRejection` in the scenario that provokes it, so the failure is a named check.
- (L14 fix round) "The page polls in a background tab" assumed the page polls when nothing plays. Read when the timer
  runs before fixing its throttling: here it runs only while a station plays, and the playing session already keeps
  the station needed.
- (L14 fix round 2) An early return that skips "nothing to do" can also skip the bookkeeping of a state change the same
  pass decided. The woken poll's "nobody new to ask" return came after `NeededStations` had already moved a station to
  idle, so the idle look, the idle mark and the "asked" reset never happened. Ask of every early return: did anything
  above it change state that only the code below records?
- (L14 fix round 2) A loop test paced by real seconds needs a deadline sized for a starved thread pool, not for an idle
  machine: three 1 s gaps fit 10 s alone but not always under the parallel run. Raise the deadline (a passing run still
  ends early) and print the elapsed time, rather than shrinking the gap, which also thins the signal the R1 test needs.
- (L14 fix round 2) A banned-substring check over JSON with random ids or timestamps is a lottery: a bare "8443" or
  "7700" can turn up in a Guid or in a time's fraction (one failure in many runs). Ban a port with its colon, which an
  id never has and a time cannot have before those digits.
- (L14 fix round 2) A socket test that counts what the server sent before the client closed measures the scheduler:
  on one core the client read its block and closed while the server was still writing (49152 of 81569). Assert the
  premise (more was offered than the client may read) and the close timing, not an exact byte count. Pinning the run
  to one core (`taskset -c 0`) reproduced it in eight runs where two cores did not in twelve.
- (L14 fix round 2) Gate every consumer of the same user-driven signal, not only the one under review: the title
  sampler read the same sessions as the demand and kept the gap the S2 fix closed for SUB/WAVE stations.
- (R1) Read the base class before trusting a framework hook. `BasePlugin<T>.LoadConfiguration` writes defaults over a
  file it cannot read, and `UpdateConfiguration` assigns before it saves; both looked harmless from the outside. Take
  the load and the save over (deserialize and assign before the lazy getter runs; write, then assign) rather than
  wrapping them.
- (R1) "Write a temp file, then swap" needs every step behind a seam to be testable: create, write some bytes, swap
  before, swap after. The "swap after" case (the rename happened, then an error) is the one that shows memory and
  disk can still differ by one save; the guarantee is "a whole file", not "the file matches memory".
- (R1) A plugin exception on Jellyfin's configuration POST reaches the browser as a status code only (the exception
  middleware keeps the message back outside development mode), so a server-side refusal has to reach the page
  another way (here Health). A change time the browser sets can go backwards; stamp it on the server,
  and floor it at the date that already decides the item date, or a save can leave `DateModified` where it was.

## Card R3 (release pipeline, 2026-10-30)

- Check everything a release can be wrong about before it becomes public. The first version published the draft and
  only then compared the manifest, so a conflicting entry left a public zip the catalog's checksum did not match
  (RA). With immutable releases on, such bytes can never be fixed, only superseded.
- GitHub's default concurrency (`queue: single`) cancels a *waiting* run when a newer one arrives;
  `cancel-in-progress: false` only protects the running one. `queue: max` keeps them all.
- A stand-in for an external CLI must fail both before and after its side effect: real `gh` can create a release or
  land an upload and still report an error. Treat only the specific "not found" answer as absence.
- Mutate the script before trusting its test: the first test passed with five of ten deliberate faults in place
  (RA), including dropped version and ABI checks.

- (R1 fix round) "Atomic replace" is atomic on Linux (one `rename`) but not on Windows: `ReplaceFile` can rename the
  old file to the backup name and then fail, so a catch that only deletes the temp file leaves no settings file at
  all. When a swap fails, look at what is on disk before cleaning up, and finish the swap from whatever complete file
  is left.
- (R1 fix round) A new file gets the process umask, not the old file's permissions: a save turned 0600 into 0664.
  Copy the mode onto every file that replaces or copies the settings, before any byte is written.
- (R1 fix round) A backup slot can destroy the evidence it is meant to keep: the first save after a failed load would
  have pushed the unreadable file into `.previous`, over the last good file. Skip the backup when another copy already
  holds what is being replaced.
- (R1 fix round) Name the cause the admin can act on. "Check the folder is writable" was wrong when the file itself
  could not be read (root-owned 0600); say both, read the file and write the folder.

## Card R2a (server tightening, 2026-10-30)

- An id that is "stable" across installs is also public. Jellyfin hashes the external id and the tile name into the
  item id and streams items without sign-in, so a fixed seed id is a guessable stream URL on every install. Make seed
  ids random and save them once; fix the old ones with a one-way re-key that is idempotent on its own output.
- A count of "the page's normal use" has to be made against the page's real timers, then through the server's caches:
  the page makes 35 requests in its busiest minute, the station sees 17. A 5 s request-status cache would have been 21
  (a 3 s poll against a 5 s lifetime misses every second poll); 8 s gives one miss in three.
- A limiter that never waits is simpler and safer than one that queues: with "refuse at once" there is no queue to
  bound, nobody holds a global slot while waiting for a station slot, and cancellation only has to release what was
  taken. The permit's scope is "until the answer is read and disposed", which `StationClient` already guarantees by
  reading every body before it returns.
- Concurrent misses that share one fetch must not share the first caller's cancellation token: give the fetch its own
  source and cancel it when the last waiter leaves, outside the cache's lock (cancellation callbacks can run
  continuations inline on the cancelling thread).
- Walk image structure; do not count byte patterns. A frame count from searching for `0x2C` or `acTL` is fooled by pixel
  data; a block and chunk walk that must reach the end marker also refuses truncated and contradictory files. Test
  fixtures that were "header only" had to become whole files once the walk read past the header.
- Hand the page a token of your own, not the upstream id. Two stations can hand out the same request id; keyed by
  our random token, nothing crosses, and the station's id never needs to be trusted for uniqueness.

- (R2a fix round) A refund rule written for the polite case is a free pass for the hostile one. "A station's refusal
  does not use the quota" let one user loop made-up song ids, because SUB/WAVE answers 409 to any id not on air (RA,
  500 calls, none refused). Give back only the window the refusal is about (request, like); keep the general slot
  spent. Give everything back only when nothing left this server.
- (R2a fix round) Ask what an attacker can make the upstream answer, then check what the cache keeps of it: a 409 per
  made-up id would have pushed real lyrics out. Don't keep answers the caller can mint at will, and evict the entry
  that expires soonest, not the oldest.
- (R2a fix round) Order the checks so a refusal costs nothing it should not: the receipt slot was reserved before the
  budget check, so an over-budget send evicted the caller's own oldest receipt and then said 429.
- (R2a fix round) A key part nothing tests is a key part a refactor can drop: replacing the cache key's origin with a
  constant left every test green (RA). One test per cache now changes only the base URL.
- (R2a fix round) Every write path needs the same invariant: the startup re-key was undone by the next save from a
  page opened before the restart.
- (R2a fix round) A test that waits on a held answer hangs when a limit is raised. Derive counts from the constants and
  run the suite under `timeout`.

## Card R2b (page, 2026-10-30)

- A retry callback needs the same guards as an answer, and the timer needs an owner. Three defences turned out to be
  layered (the reset clears the timers, the timer checks session and epoch when it fires, the callback checks the
  song or portrait it was for); with any two of them gone the old tests stayed green. Only a test that looks at the
  armed timers, and one that fires a retry between two ticks with the DOM-change pass silenced, tells them apart.
- In the fake page, the script's own DOM changes run the DOM-change pass, which checks the session too. A test of
  "the retry checks the session" passed with that check removed until the fake `MutationObserver` was silenced.
- `Retry-After` comes as seconds or as an HTTP date, and `Date.parse('-5')` is a valid date. Parse a date only when
  the value has letters, and clamp everything else; the longest wait this server sends (an hour) needs minutes on
  screen, not "Try again in 3412 s".
- Say which request outcomes are certain. A 429 or 503 from this server means nothing left; a network failure or
  another 5xx may have reached the station. The text stays in the box either way, so the page has to tell the listener
  which case it is, and never press Send for them.
- A Save handler wired straight to `addEventListener('click', save)` gets the event as its first argument. Giving
  `save` an argument (the confirmed tile name) meant wrapping both callers.
- (R2b fix round) "Retry after the server's wait" is not enough for a poll: a busy 503 says 2 s, so an open page asks
  every 2 s for as long as the station is busy. Back off on consecutive refusals of the same thing, and start afresh on
  an answer or a change of what is asked.
- (R2b fix round) A wait is a state the screen must show honestly: during a refused portrait's wait the old DJ's face sat
  next to the new name, and a tooltip-only notice is invisible on touch, TV and the Android app.
- (R2b fix round) Some guards cannot be told apart by any test: `later()`'s epoch check only ever differs when its own
  session check just reset the state, and then every callback's own check (cleared song id, removed header) refuses
  too. Say so instead of writing a test that pretends otherwise; arm timers only for the current epoch so the reason
  stays true.
- (R2b fix round) "Named like a secret" needs the names people actually use: joined (`apikey`, `PHPSESSID`), acronyms
  (`APIKey`, `XAuth`) and digits (`token2`), with negative tests (`monkey`, `keyboard`) to keep substring matching out.

## Card R4 (threat model and DESIGN pass, 2026-10-30)

- A setting name in a doc is a claim about the server too. "Withhold Allow audio playback that requires transcoding"
  read like a lock; in 12.4 it shapes what PlaybackInfo and the media source lists offer, and on `/Audio/{id}/stream` it
  only forces stream copy for a signed-in user (`EncodingHelper.TryStreamCopy`); nothing refuses the request. Name the
  setting and say where Jellyfin applies it, and grep the encoder too, not only the controllers and helpers: the first
  pass missed that check and stated the wrong reason.
- "Probably; untested" can often be settled by reading the source instead of testing: ffmpeg's http input follows up to
  `max_redirects` (8), and Jellyfin's static path uses its default client, which keeps redirects on.
- A row that says "until the admin presses X" hides a stronger fact when X never closes it. Check what a control
  closes route by route before writing that it closes a window.

## Card R5 (README rewrite, 2026-10-30)

- A README grown card by card answers "what changed"; a reader asks "what do I do first". Fix the order first (install,
  first sound, then detail), and most of the length stops mattering.
- Before cutting a fact from a README, grep the docs for it. Most had a home in DESIGN, but the limits of Stop instead of
  Pause (SyncPlay, audio focus) lived only in the README, and THREAT-MODEL names several things "the README says".
- The manifest header is outside every release path: `update-manifest.sh` writes only `.versions` and
  `check-manifest.sh` does not compare `description` or `overview`, so the catalog said "SUB/WAVE tile" for a whole phase.

## Device pass, tablet (2026-10-30)

- A control hidden by a class does not show up in the media-query ladder. L11 read the CSS and concluded that width
  decides the bar's centre group; the bar's script also hides it on any browser it calls mobile. When a page hides
  something, search its script for the class as well as its stylesheet for the rule.
- "Phones and the Android app" was two conditions treated as one, width and device, and a tablet has one without the
  other. Test the combinations a rule splits: here a mobile device above 56em, which a tablet reaches and the Pixel 9
  in landscape does not.
- A force-close does not clear an Android WebView's cache. When the server's headers look right and a client still
  behaves as if it runs old code, Clear cache is the cheap test to try before reading more source.
- Check the installed build before reading a retest. One retest ran on the old build, because the deploy had not
  happened. `md5sum` on the host caught it; without that, a correct fix would have looked wrong and sent the search
  the wrong way. Make the checksum a step in the checklist, before the device steps.
