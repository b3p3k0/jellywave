# Jellywave threat model

Status: 2026-10-27, describes plugin version 0.4.0.0 (cards 1–15b).
Written from the code and [`DESIGN.md`](DESIGN.md); where this file and the code disagree, the code
wins and this file is wrong.

Jellywave puts a SUB/WAVE radio station into Jellyfin: a channel tile, a poller that writes the
current track onto the live item, an optional station page injected into jellyfin-web, and an
admin button that adds the channel once to every user who cannot see it. Each of those crosses a trust
boundary.
This file says which ones, what holds them, and what is left open on purpose.

## Assets

| Asset | Why it matters |
|---|---|
| Jellyfin user sessions in jellyfin-web | The injected `ui.js` runs in the web client's origin with the signed-in user's session, admins included. Script injection there is account takeover. |
| User policies (`EnabledChannels`) | The access grant button writes them, and only when an admin presses it. A wrong write shows a channel to someone the admin blocked, drops another setting, or undoes an admin's removal. |
| The live item and its images | The poller writes its description and Primary image; other plugins' channels and the music library must not be touched. |
| The Jellyfin server's network position | The server calls the station. A redirect or a crafted URL could turn that into requests to other hosts (SSRF). |
| The station's request budget and identity | Every Jellyfin user shares it. |
| Request receipts | Map a station request id to the Jellyfin user who sent it; the accounting trail behind a free-text name. |
| Admin configuration | Station URL, delivery mode, tile name. |
| Release artifacts and `manifest.json` | Users install from the manifest. A tampered zip or entry runs on every server that updates. |

## Trust boundaries

1. **Admin configuration is trusted.** Only an administrator can save it or call the admin routes
   (`GET /Jellywave/Station`, `GET /Jellywave/Health`, `POST /Jellywave/GrantAccess`,
   `GET /Jellywave/WebUi`, all `RequiresElevation`). The admin probe can make the server fetch any
   http(s) URL the admin types; that is the admin's choice, not a hole. The configuration is still
   validated, so a mistake fails closed: a base URL with userinfo, a query or a fragment is refused
   and the station is treated as not configured.
2. **Station responses are untrusted data.** The station is admin-chosen but its answers are not
   trusted. These controls cover the plugin's own requests (feed, state, schedule, likes, requests,
   lyrics, images), not the audio stream, which ffmpeg (Relay) or the client (Direct) fetches:
   - Bodies are bounded: 256 KB for JSON, 4 MB for images, 8 s per call, body included.
   - The plugin follows no redirect on its own requests. Its named HTTP client has `AllowAutoRedirect = false`;
     a 3xx is the answer, mapped to a failure or "no art".
   - Images are sniffed. The type comes from magic bytes (JPEG, PNG, GIF, WebP), the size from the
     header (at most 4096 px a side, 16 777 216 px area); anything else is "no art". The declared
     content type only has to be missing or `image/*`.
   - Ids that go back into station URLs (cover, persona, song, request) must be 1–64 characters of
     letters, digits, `_` and `-`.
   - Text written to the live item's description is whitespace-collapsed and cut at 120 characters
     per field. Jellyfin's own clients render that description under their own rules.
3. **Jellyfin users must be signed in and able to see the channel.** Every station route (Feed,
   Avatar, Like GET and POST, Schedule, Lyrics, Request POST, Request/{id}) carries `[Authorize]`
   and `[StationAccess]`. The filter runs Jellyfin's own `Channel.IsVisible(user)`: block list,
   enabled list or `EnableAllChannels`, then parental controls. Administrators get no exemption.
   A missing or colliding channel fails closed with 404. A reflection test fails when a route is
   neither anonymous, admin-only nor filtered.
4. **API keys are refused on station routes.** Jellyfin's default policy lets any API key through;
   the filter answers 403 unless the `Jellyfin-IsApiKey` claim is present and parses as false.
5. **The browser page renders station text with `textContent` or `innerText`.** The header's markup is a fixed
   string; every station-supplied value (track, DJ, tagline, show, recent tracks, lyrics, schedule,
   request outcomes, the playing bar's lines) goes in through `textContent`, a created element's
   `textContent`, or `innerText` (the playing bar); all three are text, never markup. Images are
   Jellyfin image URLs built from the item id and tag, or a blob URL of the portrait fetched with
   the session. The page talks only to the Jellyfin server, never to the station.
6. **The anonymous assets are static.** `ui.js` and `ui.css` are embedded in the DLL and served
   without sign-in (the page needs them before sign-in); they carry no data.

## Controls

| Control | Card |
|---|---|
| Static media sources only, no `IRequiresMediaInfoCallback` (no duplicate or injected sources) | 2 |
| Empty station URL by default; no hostname in source | 1, 3 |
| JSON body cap and 8 s timeout covering the body | 3 |
| `Artists` never written (no Music-library artists created from station data) | 4 |
| Access grant writes only this channel's id into `EnabledChannels`, never sets `EnableAllChannels`, never removes an id, leaves block lists alone, logs counts and never user names | 6 |
| `Album`, `Name` and `AlbumArtists` never written (no orphan user-data rows); cover id validated; 4 MB image cap with headers judged first | 7 |
| Station routes never return the station URL; the page talks only to Jellyfin; request text capped at 280, name cut at 40 | 8a, 8c |
| Station text rendered with `textContent` or `innerText`; the header markup is a fixed string | 8b, 8d |
| Persona and song ids validated before they reach a station URL; portrait fetched with the session, not `?api_key=` | 8d |
| `targetAbi` matches the packages the DLL is built against (check script and test) | 12 |
| Release: read-only build job, separate publish job holding the only write token, actions pinned by SHA, Dependabot, CI on every push and pull request | 12 |
| `[StationAccess]` on every station route: API keys refused, user must exist, `Channel.IsVisible` decides, fail closed without a channel; reflection test | 13 |
| Request receipts: only the submitting user can poll a request; user id logged with the request id | 13 |
| Own HTTP client with redirects refused; the admin probe suggests a redirect target only when it passes the URL rules | 14 |
| Base URL refuses userinfo, query and fragment; stream URL must match the base on scheme, host, port and userinfo; client URL refuses userinfo | 14 |
| `ImageSniffer`: type from magic bytes, header dimensions bounded, malformed input is "unknown" | 14 |
| Channel found by its derived id, not by name; a name collision with another plugin's channel fails closed in the poller, the granter and the station routes | 15 |
| Grant passes serialized under one lock; each user re-read before it is saved | 15 |
| No background policy writer: no startup pass, no user event consumer, no setting. The grant runs only from the admin-only `POST /Jellywave/GrantAccess` button, so an admin's removal of the channel from a user stands until the next press; registrator and constructor-reflection tests | 15b |
| `verify-jellyfin.sh` selects the channel by exact name and fails on none or more than one | 15 |
| Station data cached on the server (now-playing state, schedule, applied cover, missing-cover memory) is keyed by station origin, so a station URL change never serves the old station's data; a JSON `null` feed is a failure; repeated ids and null list entries from the station never fail a route | 17 |

## Accepted risks

| Risk | Why it is accepted |
|---|---|
| Shared station identity and request budget | The station sees the server as one caller; one user can use up everyone's requests. HI chose the station's own limits over a plugin limiter; the page explains it. |
| Free-text DJ name | A listener can type any name the DJ will see. The real user id is kept in the receipt and the log line, so abuse can be traced. |
| Stream fetch outside the redirect control | In Relay mode Jellyfin's ffmpeg fetches the stream, in Direct mode the client does; neither uses the plugin's client. A station that redirects its stream can point ffmpeg at another host (ffmpeg's http input probably follows 3xx; untested). The plugin cannot change how Jellyfin invokes ffmpeg, and the admin chooses the station. |
| Plain-HTTP station URL | The station normally runs on plain HTTP on the LAN; refusing it would break the common install. |
| Station URL disclosed to clients | The stream URL is in the live item's playback info in both modes, which `Items/{id}/PlaybackInfo` returns to any signed-in user who can see the item (`Channel.IsVisible` included) and to any API key; in Direct mode clients fetch it themselves. SUB/WAVE streams are public; listener auth is not supported. |
| Name-derived channel id | Jellyfin's plugin API has no other channel identity. A collision fails closed; renaming changes the id, so grants and block lists naming the old id stop applying. |
| User-save window against dashboard saves | Only while the grant button's pass is running: a dashboard policy save that lands between the plugin's read and save of that user (milliseconds) is overwritten; `IUserManager` has no partial update. The admin sees the old value on reload. |
| New users are not granted automatically | Since card 15b nothing grants in the background. The dashboard's Add User page leaves "Enable access to all channels" unticked, so a user added there, from a template or by a provisioning tool cannot see the station until the admin ticks it or the station, the tool's template includes it, or the button is pressed again. The safe direction: a missing grant hides the station. |
| A press re-adds the station to users an admin unticked | The button adds the channel to everyone who cannot see it, and an unticked user looks the same as one never granted. Only a `BlockedChannels` entry survives, and jellyfin-web has no screen for it; saving the user's Access page in jellyfin-web clears it (`Access.tsx` posts `BlockedChannels = null`). The press is an explicit admin action, and the page says to untick those users again afterwards. |
| Live-item whole-row write racing Jellyfin's channel refresh | Jellyfin has no partial item update. Narrowed: the cover is downloaded first, then the item is looked up and written; every poll reconciles the item. |
| GIF, APNG and animated WebP frames | Only the first frame's header is sized; a file within 4 MB and 4096 × 4096 can still carry many frames for Jellyfin's image processor to decode. The station is admin-chosen. |
| jellyfin-web DOM selectors | `.itemsContainer[data-parentid]`, `.nowPlayingBar` and `.card` are jellyfin-web internals that can move between releases; the page degrades to doing nothing. |
| 1000-receipt cap eviction | One user filling the cap evicts other users' receipts, whose polls then say "lost track". The station's own rate limit keeps the map far below the cap. |
| Receipts lost on restart | Receipts live in memory; a request pending across a restart shows "lost track". |

## Not covered here

Jellyfin itself, SUB/WAVE itself and the File Transformation plugin are outside this model; see
[`SECURITY.md`](../SECURITY.md) for where to report those.
